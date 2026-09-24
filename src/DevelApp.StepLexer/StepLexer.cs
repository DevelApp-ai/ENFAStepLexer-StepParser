using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace DevelApp.StepLexer
{

    /// <summary>
    /// Integrated step lexer with two-phase regex pattern parsing and multi-path source code tokenization
    /// Combines regex pattern compilation capabilities with character-by-character tokenization
    /// Implements zero-copy UTF-8 processing and single-pass ambiguity resolution
    /// </summary>
    public partial class StepLexer
    {
        private readonly List<TokenRule> _rules = new();
        private readonly List<LexerPath> _activePaths = new();
        private readonly IContextStack _contextStack = new ContextStack();
        private ReadOnlyMemory<byte> _input;
        private string _fileName = string.Empty;
        private int _nextPathId = 0;
        
        // Two-phase regex pattern parsing components
        private readonly List<SplittableToken> _phase1Tokens = new();
        private readonly List<ParsedState> _phase2States = new();

        /// <summary>
        /// Current active paths being tracked
        /// </summary>
        public IReadOnlyList<LexerPath> ActivePaths => _activePaths;

        /// <summary>
        /// Current context stack
        /// </summary>
        public IContextStack ContextStack => _contextStack;

        /// <summary>
        /// Gets the byte offset of the most recent input position where no
        /// rule matched (i.e. where a lexer path stalled), or -1 if every
        /// consumed position matched. Used for detailed diagnostics.
        /// </summary>
        public int LastNoMatchPosition { get; private set; } = -1;

        /// <summary>
        /// Gets a value indicating whether at least one lexer path consumed
        /// the input up to its very end. When this is <see langword="false"/>
        /// and <see cref="LastNoMatchPosition"/> is set, the input contained
        /// text that no rule could match.
        /// </summary>
        public bool CompletedInput { get; private set; }

        /// <summary>
        /// Add a tokenization rule
        /// </summary>
        public void AddRule(TokenRule rule)
        {
            _rules.Add(rule);
            // Sort by priority (higher priority first)
            _rules.Sort((a, b) => b.Priority.CompareTo(a.Priority));
        }

        /// <summary>
        /// Remove all tokenization rules, returning the lexer to its
        /// pre-configured state so a new grammar can be loaded without
        /// duplicating rules.
        /// </summary>
        public void ClearRules()
        {
            _rules.Clear();
        }

        /// <summary>
        /// Initialize lexer with input text
        /// </summary>
        public void Initialize(ReadOnlyMemory<byte> input, string fileName = "")
        {
            _input = input;
            _fileName = fileName;
            _activePaths.Clear();
            _activePaths.Add(new LexerPath(_nextPathId++, 0));
            _contextStack.Push("default");
            LastNoMatchPosition = -1;
            CompletedInput = input.Length == 0;
        }

        /// <summary>
        /// Process next character and return updated paths
        /// </summary>
        public LexerStepResult Step()
        {
            var result = new LexerStepResult();
            var newPaths = new List<LexerPath>();

            foreach (var path in _activePaths.Where(p => p.IsValid))
            {
                var stepResult = ProcessPath(path);
                result.NewTokens.AddRange(stepResult.Tokens);
                newPaths.AddRange(stepResult.Paths);
                
                if (stepResult.ContextChanges.Any())
                {
                    result.ContextChanges.AddRange(stepResult.ContextChanges);
                }
            }

            // Update active paths and merge identical paths
            _activePaths.Clear();
            _activePaths.AddRange(MergePaths(newPaths));

            // Remove invalid paths
            _activePaths.RemoveAll(p => !p.IsValid);

            result.ActivePathCount = _activePaths.Count;
            result.IsComplete = _activePaths.All(p => p.Position >= _input.Length);

            // IsComplete with a non-empty path list means at least one path
            // consumed the input to its very end (with an empty list it is
            // vacuously true and means all paths stalled instead).
            if (result.IsComplete && _activePaths.Count > 0)
            {
                CompletedInput = true;
            }

            return result;
        }

        /// <summary>
        /// Optional learned rule-evaluation prioritizer (issue #74). When
        /// set, rules are evaluated in descending predicted match
        /// probability instead of declaration order. This changes ONLY the
        /// evaluation order: every applicable rule is still attempted and
        /// the collected matches are processed in declaration order, so
        /// tokens, paths and diagnostics stay identical to the default
        /// behavior. The StepParser gates this property behind
        /// <c>MlAssistFeature.LearnedRulePrioritization</c>.
        /// </summary>
        public ILearnedRulePrioritizer? RulePrioritizer { get; set; }

        /// <summary>
        /// Process a single lexer path
        /// </summary>
        private PathStepResult ProcessPath(LexerPath path)
        {
            var result = new PathStepResult();

            if (path.Position >= _input.Length)
            {
                // A path reached the very end of the input, which means the
                // input was consumed completely by this path.
                CompletedInput = true;
                path.IsValid = false;
                return result;
            }

            var remainingInput = _input.Span.Slice(path.Position);
            var (line, column) = CalculateLineColumn(path.Position);

            // Try to match rules in priority order
            var matches = new List<(TokenRule rule, int length, string matchText)>();

            // Issue #74 instrumentation: count attempts and the attempts
            // wasted before the first success, so the effect of a learned
            // evaluation ordering is measurable without changing behavior.
            var attempts = 0;
            var wastedBeforeFirstMatch = 0;
            var sawMatch = false;

            var prioritizer = RulePrioritizer;
            if (prioritizer == null)
            {
                foreach (var rule in _rules)
                {
                    // Check context compatibility
                    if (!IsRuleApplicableInContext(rule, path.CurrentContext))
                    {
                        continue;
                    }

                    attempts++;
                    var match = TryMatchRule(rule, remainingInput);
                    if (match.success)
                    {
                        sawMatch = true;
                        matches.Add((rule, match.length, match.text));
                    }
                    else if (!sawMatch)
                    {
                        wastedBeforeFirstMatch++;
                    }
                }
            }
            else
            {
                // Learned ordering (issue #74): evaluate rules in
                // descending predicted match probability. Ordering only —
                // every applicable rule is still attempted, and the matches
                // are collected with their declaration index so the final
                // match list can be restored to declaration order below,
                // keeping the emitted tokens/paths bit-identical.
                var firstByte = remainingInput[0];
                var evaluationOrder = new List<(TokenRule rule, int index, double probability)>(_rules.Count);
                for (var i = 0; i < _rules.Count; i++)
                {
                    var rule = _rules[i];
                    var probability = IsRuleApplicableInContext(rule, path.CurrentContext)
                        ? prioritizer.PredictMatchProbability(rule, firstByte, path.CurrentContext)
                        : -1.0; // context-inapplicable: evaluated (skipped) first, cheap
                    evaluationOrder.Add((rule, i, probability));
                }

                evaluationOrder.Sort(static (a, b) =>
                    a.probability != b.probability
                        ? b.probability.CompareTo(a.probability)
                        : a.index.CompareTo(b.index));

                var orderedMatches =
                    new List<(int index, TokenRule rule, int length, string matchText)>();
                foreach (var (rule, index, _) in evaluationOrder)
                {
                    if (!IsRuleApplicableInContext(rule, path.CurrentContext))
                    {
                        continue;
                    }

                    attempts++;
                    var match = TryMatchRule(rule, remainingInput);
                    if (match.success)
                    {
                        sawMatch = true;
                        orderedMatches.Add((index, rule, match.length, match.text));
                    }
                    else if (!sawMatch)
                    {
                        wastedBeforeFirstMatch++;
                    }
                }

                // Restore declaration order for processing: the paths and
                // path IDs assigned by ProcessSingleMatch/ProcessMultipleMatches
                // must not depend on the ML ordering (zero semantic change).
                orderedMatches.Sort(static (a, b) => a.index.CompareTo(b.index));
                foreach (var (_, rule, length, text) in orderedMatches)
                {
                    matches.Add((rule, length, text));
                }
            }

            RuleMatchDiagnostics.RecordPosition(attempts, wastedBeforeFirstMatch);

            if (matches.Count == 0)
            {
                // No matches - invalid path
                if (path.Position > LastNoMatchPosition)
                {
                    LastNoMatchPosition = path.Position;
                }

                path.IsValid = false;
                return result;
            }

            // Handle multiple matches - create paths for ambiguity
            if (matches.Count == 1)
            {
                var (rule, length, text) = matches[0];
                ProcessSingleMatch(path, rule, text, length, line, column, result);
            }
            else
            {
                // Multiple matches - create split paths
                ProcessMultipleMatches(path, matches, line, column, result);
            }

            return result;
        }

        /// <summary>
        /// Calculate line and column from byte position
        /// </summary>
        private (int line, int column) CalculateLineColumn(int position)
        {
            int line = 1, column = 1;
            
            for (int i = 0; i < Math.Min(position, _input.Length); i++)
            {
                if (_input.Span[i] == '\n')
                {
                    line++;
                    column = 1;
                }
                else
                {
                    column++;
                }
            }
            
            return (line, column);
        }
    }
}