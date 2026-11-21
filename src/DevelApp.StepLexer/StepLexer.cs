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

        /// <summary>
        /// Byte offsets at which each line starts (<c>_lineStarts[0] == 0</c>),
        /// built incrementally while <see cref="CalculateLineColumn"/> scans
        /// forward; reset by <see cref="Initialize"/> (issue #52/#133: the
        /// lookup must stay O(log lines) per step, not O(position), so
        /// file-scale lexing stays linear overall).
        /// </summary>
        private readonly List<int> _lineStarts = new() { 0 };

        /// <summary>Byte offset up to which <see cref="_lineStarts"/> is built.</summary>
        private int _lineScanUpTo;
        
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

            // Issue #87: resolve the pattern once at add time so the match
            // loop performs no pattern preprocessing. GetCompiledRule
            // recompiles if the rule's pattern is mutated later.
            _compiledRules[rule] = CompileRule(rule);
        }

        /// <summary>
        /// Remove all tokenization rules, returning the lexer to its
        /// pre-configured state so a new grammar can be loaded without
        /// duplicating rules.
        /// </summary>
        public void ClearRules()
        {
            _rules.Clear();
            _compiledRules.Clear();
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
            _lineStarts.Clear();
            _lineStarts.Add(0);
            _lineScanUpTo = 0;
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

            // Issue #157, part 1: bound the exploratory path set after
            // merging; without a cap the per-rule forks compound
            // exponentially on overlap-heavy grammars (see PathBudget).
            PruneToPathBudget();

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
        /// Lexing mode (issue #133). <see cref="LexingMode.Exploratory"/>
        /// (default) forks a path per matching rule; 
        /// <see cref="LexingMode.GreedyLongestMatch"/> keeps only the
        /// longest match per position (priority tie-break), so exactly one
        /// path survives every step and lexing is linear in the input
        /// length with a single coherent token path.
        /// </summary>
        public LexingMode Mode { get; set; } = LexingMode.Exploratory;

        /// <summary>
        /// Deterministic active-path budget applied after every step in
        /// <see cref="LexingMode.Exploratory"/> (issue #157, part 1).
        /// Exploratory lexing forks one path per matching rule per
        /// position, so grammars whose rules overlap heavily (multi-byte
        /// tokens, generic identifier rules, overlapping keyword and
        /// punctuation classes) double their path count nearly every
        /// step: measured corpus cases forked to hundreds of thousands of
        /// concurrent paths within the first ~20 bytes of input, burning
        /// minutes of CPU before the parser ever ran (the 82/128 corpus
        /// blowups this issue tracks are lex-level, not parse-level).
        /// The budget is the lexer-side analogue of
        /// <c>DevelApp.StepParser.StepParser.PathBudget</c>: when the
        /// merged active set exceeds the budget, paths are ordered by
        /// input position first (paths that consumed the most input are
        /// the ones that can still tile the document), then by token
        /// count, then by path id for determinism, and the set is
        /// truncated. Tokens already emitted are unaffected, so the
        /// ambiguous token union shrinks at the margin but never loses
        /// span coverage. In <see cref="LexingMode.GreedyLongestMatch"/>
        /// the budget is inert (exactly one path survives every step).
        /// </summary>
        public int PathBudget { get; set; } = 64;

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

            // Try to match rules in priority order. Matchers return only the
            // byte length (issue #87); token text is materialized lazily
            // from the UTF-8 input via ZeroCopyStringView.
            var matches = new List<(TokenRule rule, int length)>();

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

                    // Issue #93: label capture for the #74 training corpus
                    // (observation-only; inert unless the ML trace harness
                    // is capturing on this thread).
                    if (RuleMatchLabelCapture.IsCapturing)
                    {
                        RuleMatchLabelCapture.Record(rule, remainingInput[0], match.success);
                    }
                    if (match.success)
                    {
                        sawMatch = true;
                        matches.Add((rule, match.length));
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
                    new List<(int index, TokenRule rule, int length)>();
                foreach (var (rule, index, _) in evaluationOrder)
                {
                    if (!IsRuleApplicableInContext(rule, path.CurrentContext))
                    {
                        continue;
                    }

                    attempts++;
                    var match = TryMatchRule(rule, remainingInput);

                    // Issue #93: label capture for the #74 training corpus
                    // (observation-only; inert unless the ML trace harness
                    // is capturing on this thread).
                    if (RuleMatchLabelCapture.IsCapturing)
                    {
                        RuleMatchLabelCapture.Record(rule, remainingInput[0], match.success);
                    }
                    if (match.success)
                    {
                        sawMatch = true;
                        orderedMatches.Add((index, rule, match.length));
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
                foreach (var (_, rule, length) in orderedMatches)
                {
                    matches.Add((rule, length));
                }
            }

            RuleMatchDiagnostics.RecordPosition(attempts, wastedBeforeFirstMatch);

            // Issue #133: deterministic greedy/longest-match mode - reduce
            // the collected matches to a single best one: the longest match
            // wins; ties keep the first entry, which is priority order
            // (rules are sorted by descending priority at add time), so
            // keyword-over-identifier style priorities still apply on ties.
            if (Mode == LexingMode.GreedyLongestMatch && matches.Count > 1)
            {
                var best = matches[0];
                for (int i = 1; i < matches.Count; i++)
                {
                    if (matches[i].length > best.length)
                    {
                        best = matches[i];
                    }
                }
                matches.Clear();
                matches.Add(best);
            }

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
                var (rule, length) = matches[0];
                ProcessSingleMatch(path, rule, length, line, column, result);
            }
            else
            {
                // Multiple matches - create split paths
                ProcessMultipleMatches(path, matches, line, column, result);
            }

            return result;
        }

        /// <summary>
        /// Calculate line and column from byte position. Line starts are
        /// recorded incrementally (the scan cursor only moves forward), so
        /// the per-step cost is O(log lines) plus amortized O(1) scanning
        /// instead of rescanning the whole prefix (issue #52/#133). Only
        /// '\n' ends a line; '\r' counts toward the column, as before.
        /// </summary>
        private (int line, int column) CalculateLineColumn(int position)
        {
            var span = _input.Span;
            int limit = Math.Min(position, span.Length);
            while (_lineScanUpTo < limit)
            {
                if (span[_lineScanUpTo] == '\n')
                {
                    _lineStarts.Add(_lineScanUpTo + 1);
                }
                _lineScanUpTo++;
            }

            // Largest line start <= position (line starts are sorted).
            int lo = 0, hi = _lineStarts.Count - 1;
            while (lo < hi)
            {
                int mid = (lo + hi + 1) / 2;
                if (_lineStarts[mid] <= position)
                {
                    lo = mid;
                }
                else
                {
                    hi = mid - 1;
                }
            }

            return (lo + 1, position - _lineStarts[lo] + 1);
        }
    }
}