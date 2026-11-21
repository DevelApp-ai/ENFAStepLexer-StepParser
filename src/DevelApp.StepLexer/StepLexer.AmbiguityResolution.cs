using System;
using System.Collections.Generic;

namespace DevelApp.StepLexer
{
    public partial class StepLexer
    {

        /// <summary>
        /// Check if token can be split for ambiguity resolution
        /// </summary>
        private bool CanSplitToken(StepToken token, TokenRule rule)
        {
            // For demonstration - tokens like "\x{41}\xFF" can be split.
            // Issue #87: scan the token's UTF-8 view directly (no decode);
            // string-constructed tokens fall back to the string scan.
            if (token.TryGetValueView(out var view))
            {
                var span = view.AsSpan();
                var escapeIndex = span.IndexOf("\\x{"u8);
                return escapeIndex >= 0 && span.Slice(escapeIndex).IndexOf((byte)'}') >= 0;
            }

            return token.Value.Contains("\\x{") && token.Value.Contains("}");
        }

        /// <summary>
        /// Generate split tokens for ambiguity resolution
        /// </summary>
        private List<StepToken> GenerateSplitTokens(StepToken originalToken, TokenRule rule, ICodeLocation location)
        {
            var splits = new List<StepToken>();
            
            // Example implementation for hex escape splitting
            if (originalToken.Value.Contains("\\x{") && originalToken.Value.Contains("}"))
            {
                // Split into individual hex escapes
                var parts = originalToken.Value.Split(new[] { "\\x{" }, StringSplitOptions.RemoveEmptyEntries);
                foreach (var part in parts)
                {
                    if (part.Contains("}"))
                    {
                        var closingBraceIndex = part.IndexOf("}");
                        if (closingBraceIndex > 0) // Make sure it's found and not at position 0
                        {
                            var hex = part.Substring(0, closingBraceIndex);
                            splits.Add(new StepToken("HEX_ESCAPE", $"\\x{{{hex}}}", location, originalToken.Context));
                        }
                    }
                }
            }
            
            return splits;
        }

        /// <summary>
        /// Merge identical paths to optimize performance
        /// </summary>
        /// <remarks>
        /// Two paths merge only when they share the same position, context
        /// and token type sequence. The token type sequence is compared via
        /// an incrementally maintained fingerprint (see
        /// <see cref="LexerPath.GetTokenFingerprint"/>) so merging is O(1)
        /// amortized per step instead of building a joined string of all
        /// token types, which made step cost grow quadratically with input
        /// size. Fingerprint collisions are resolved by an exact sequence
        /// comparison before merging.
        /// </remarks>
        private List<LexerPath> MergePaths(List<LexerPath> paths)
        {
            if (paths.Count <= 1)
            {
                // Fast path: nothing can be merged, skip all bucketing work.
                return paths;
            }

            var merged = new List<LexerPath>(paths.Count);
            var buckets = new Dictionary<(int Position, string Context), List<LexerPath>>();

            foreach (var path in paths)
            {
                if (!buckets.TryGetValue((path.Position, path.CurrentContext), out var bucket))
                {
                    bucket = new List<LexerPath>();
                    buckets[(path.Position, path.CurrentContext)] = bucket;
                }
                bucket.Add(path);
            }

            foreach (var bucket in buckets.Values)
            {
                if (bucket.Count == 1)
                {
                    merged.Add(bucket[0]);
                    continue;
                }

                var representatives = new List<LexerPath>();
                var fingerprints = new List<long>();

                foreach (var path in bucket)
                {
                    var fingerprint = path.GetTokenFingerprint();
                    var duplicate = false;

                    for (int i = 0; i < representatives.Count; i++)
                    {
                        if (fingerprints[i] == fingerprint && TokenTypesEqual(representatives[i], path))
                        {
                            duplicate = true;
                            break;
                        }
                    }

                    if (!duplicate)
                    {
                        representatives.Add(path);
                        fingerprints.Add(fingerprint);
                    }
                }

                merged.AddRange(representatives);
            }

            return merged;
        }

        /// <summary>
        /// Ordering used when pruning the lexer path budget: paths that
        /// have consumed more input first (they are the ones that can
        /// still tile the rest of the document), then paths that emitted
        /// more tokens, then by path id so the truncation is
        /// deterministic run to run. Mirrors the position-first ordering
        /// of the parser's budget pruning (issue #80, #157 part 3).
        /// </summary>
        /// <param name="first">The first lexer path to compare.</param>
        /// <param name="second">The second lexer path to compare.</param>
        /// <returns>A negative value when <paramref name="first"/> sorts
        /// before <paramref name="second"/>, zero when equal, positive
        /// otherwise.</returns>
        private static int ComparePathsForPruning(LexerPath first, LexerPath second)
        {
            int byPosition = second.Position.CompareTo(first.Position);
            if (byPosition != 0)
            {
                return byPosition;
            }

            int byTokenCount = second.Tokens.Count.CompareTo(first.Tokens.Count);
            if (byTokenCount != 0)
            {
                return byTokenCount;
            }

            return first.PathId.CompareTo(second.PathId);
        }

        /// <summary>
        /// Apply the deterministic active-path budget (issue #157, part 1).
        /// When the merged active set exceeds <see cref="PathBudget"/>, the
        /// set is ordered by <see cref="ComparePathsForPruning"/> (input
        /// position first) and truncated. The furthest-advanced path is
        /// always inside the kept prefix, so the budget can never strand
        /// the input behind unmatchable interpretations: the run loop's
        /// stall guard, which measures the maximum position over all
        /// active paths, keeps seeing progress. No-op in
        /// <see cref="LexingMode.GreedyLongestMatch"/> and whenever the
        /// active set is within budget.
        /// </summary>
        private void PruneToPathBudget()
        {
            var budget = PathBudget;
            if (budget < 1 || _activePaths.Count <= budget)
            {
                return;
            }

            _activePaths.Sort(ComparePathsForPruning);
            _activePaths.RemoveRange(budget, _activePaths.Count - budget);
        }

        /// <summary>
        /// Check whether two paths carry identical token type sequences.
        /// </summary>
        private static bool TokenTypesEqual(LexerPath first, LexerPath second)
        {
            if (ReferenceEquals(first, second))
            {
                return true;
            }

            var firstTokens = first.Tokens;
            var secondTokens = second.Tokens;

            if (firstTokens.Count != secondTokens.Count)
            {
                return false;
            }

            for (int i = 0; i < firstTokens.Count; i++)
            {
                if (firstTokens[i].Type != secondTokens[i].Type)
                {
                    return false;
                }
            }

            return true;
        }
    }
}
