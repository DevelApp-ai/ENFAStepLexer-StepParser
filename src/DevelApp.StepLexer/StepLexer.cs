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

        // Incrementally maintained line-break index for O(log n) line/column
        // lookup. _lineBreaks holds the input positions of all newline bytes
        // found so far; _lineBreakScanLimit is the number of input bytes
        // already scanned for newlines. Both are reset on Initialize.
        private readonly List<int> _lineBreaks = new();
        private int _lineBreakScanLimit;
        
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
        /// Add a tokenization rule
        /// </summary>
        public void AddRule(TokenRule rule)
        {
            _rules.Add(rule);
            // Sort by priority (higher priority first)
            _rules.Sort((a, b) => b.Priority.CompareTo(a.Priority));
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

            // Reset the incremental line-break index for the new input
            _lineBreaks.Clear();
            _lineBreakScanLimit = 0;
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

            return result;
        }

        /// <summary>
        /// Process a single lexer path
        /// </summary>
        private PathStepResult ProcessPath(LexerPath path)
        {
            var result = new PathStepResult();

            if (path.Position >= _input.Length)
            {
                path.IsValid = false;
                return result;
            }

            var remainingInput = _input.Span.Slice(path.Position);
            var (line, column) = CalculateLineColumn(path.Position);
            
            // Try to match rules in priority order
            var matches = new List<(TokenRule rule, int length, string matchText)>();

            foreach (var rule in _rules)
            {
                // Check context compatibility
                if (!IsRuleApplicableInContext(rule, path.CurrentContext))
                    continue;

                var match = TryMatchRule(rule, remainingInput);
                if (match.success)
                {
                    matches.Add((rule, match.length, match.text));
                }
            }

            if (matches.Count == 0)
            {
                // No matches - invalid path
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
        /// <remarks>
        /// Uses an incrementally maintained, sorted index of newline byte
        /// positions plus a binary search, so each lookup is O(log lines)
        /// with O(1) amortized indexing work per new input byte. The
        /// previous implementation rescanned the input from position 0 on
        /// every step, which made tokenization cost grow quadratically with
        /// input size.
        /// </remarks>
        private (int line, int column) CalculateLineColumn(int position)
        {
            var span = _input.Span;
            var limit = Math.Min(position, span.Length);

            // Extend the scanned newline index if the requested position
            // moves past what has been indexed so far.
            if (limit > _lineBreakScanLimit)
            {
                for (int i = _lineBreakScanLimit; i < limit; i++)
                {
                    if (span[i] == (byte)'\n')
                    {
                        _lineBreaks.Add(i);
                    }
                }
                _lineBreakScanLimit = limit;
            }

            // Binary search for the number of newlines before the position.
            int lo = 0, hi = _lineBreaks.Count - 1, newlineCount = 0;
            while (lo <= hi)
            {
                int mid = lo + (hi - lo) / 2;
                if (_lineBreaks[mid] < position)
                {
                    newlineCount = mid + 1;
                    lo = mid + 1;
                }
                else
                {
                    hi = mid - 1;
                }
            }

            var line = newlineCount + 1;
            var lastNewline = newlineCount > 0 ? _lineBreaks[newlineCount - 1] : -1;
            var column = position - lastNewline;

            return (line, column);
        }
    }
}