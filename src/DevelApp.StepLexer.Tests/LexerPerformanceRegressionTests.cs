using System.Text;
using DevelApp.StepLexer;

namespace DevelApp.StepLexer.Tests
{
    /// <summary>
    /// Regression tests for the StepLexer quadratic growth fix (#52):
    /// incremental line/column lookup and O(1) amortized path merging must
    /// preserve the observable tokenization behavior while keeping total
    /// allocation linear in input size.
    /// </summary>
    public class LexerPerformanceRegressionTests
    {
        private static List<StepToken> RunToCompletion(StepLexer lexer, byte[] input)
        {
            lexer.Initialize(input, "test.txt");
            var tokens = new List<StepToken>();
            var guard = 0;
            while (lexer.ActivePaths.Any(p => p.IsValid && p.Position < input.Length))
            {
                var result = lexer.Step();
                tokens.AddRange(result.NewTokens);
                if (++guard > 100_000)
                {
                    break;
                }
            }
            return tokens;
        }

        private static StepLexer CreateNumberLexer()
        {
            var lexer = new StepLexer();
            lexer.AddRule(new TokenRule("NUMBER", @"/[0-9]+/"));
            lexer.AddRule(new TokenRule("WS", @"/[ \t\r\n]+/") { IsSkippable = true });
            return lexer;
        }

        private static string GenerateNumbers(int count)
        {
            return string.Join(" ", Enumerable.Range(0, count).Select(i => ((i * 7919) % 100_000).ToString()));
        }

        [Fact]
        public void TokenLocations_AreCorrectAcrossNewlines()
        {
            var lexer = CreateNumberLexer();
            var input = Encoding.UTF8.GetBytes("12\n345\n6");
            var tokens = RunToCompletion(lexer, input);

            var values = tokens.Select(t => t.Value).ToList();
            Assert.Equal(new[] { "12", "345", "6" }, values);

            Assert.Equal(1, tokens[0].Location.StartLine);
            Assert.Equal(1, tokens[0].Location.StartColumn);
            Assert.Equal(3, tokens[0].Location.EndColumn);

            // "345" starts right after the newline at byte 2
            Assert.Equal(2, tokens[1].Location.StartLine);
            Assert.Equal(1, tokens[1].Location.StartColumn);
            Assert.Equal(4, tokens[1].Location.EndColumn);

            // "6" starts right after the newline at byte 6
            Assert.Equal(3, tokens[2].Location.StartLine);
            Assert.Equal(1, tokens[2].Location.StartColumn);
        }

        [Fact]
        public void TokenLocations_CountMultibyteBytesInColumns()
        {
            var lexer = new StepLexer();
            lexer.AddRule(new TokenRule("WORD", @"/\p{L}+/"));
            lexer.AddRule(new TokenRule("WS", @"/[ \t\r\n]+/") { IsSkippable = true });

            // "héllo" is 6 UTF-8 bytes; the newline follows at byte 6
            var input = Encoding.UTF8.GetBytes("héllo\nab");
            var tokens = RunToCompletion(lexer, input);

            Assert.Equal(2, tokens.Count);

            Assert.Equal(1, tokens[0].Location.StartLine);
            Assert.Equal(1, tokens[0].Location.StartColumn);
            Assert.Equal(7, tokens[0].Location.EndColumn);

            Assert.Equal(2, tokens[1].Location.StartLine);
            Assert.Equal(1, tokens[1].Location.StartColumn);
            Assert.Equal(3, tokens[1].Location.EndColumn);
        }

        [Fact]
        public void TokenLocations_CarriageReturnCountsTowardColumn()
        {
            var lexer = CreateNumberLexer();

            // \r\n: only \n ends the line, so \r shifts the column by one byte
            var input = Encoding.UTF8.GetBytes("12\r\n34");
            var tokens = RunToCompletion(lexer, input);

            Assert.Equal(new[] { "12", "34" }, tokens.Select(t => t.Value));

            Assert.Equal(1, tokens[0].Location.StartLine);
            Assert.Equal(1, tokens[0].Location.StartColumn);

            Assert.Equal(2, tokens[1].Location.StartLine);
            Assert.Equal(1, tokens[1].Location.StartColumn);
        }

        [Fact]
        public void TokenLocations_ResetAcrossMultipleRuns()
        {
            // The line-break index is internal state; re-initializing with a
            // different input must fully reset it.
            var lexer = CreateNumberLexer();

            var first = RunToCompletion(lexer, Encoding.UTF8.GetBytes("1\n2\n3"));
            Assert.Equal(3, first.Count);
            Assert.Equal(3, first[2].Location.StartLine);

            var second = RunToCompletion(lexer, Encoding.UTF8.GetBytes("9 8"));
            Assert.Equal(2, second.Count);
            Assert.All(second, t => Assert.Equal(1, t.Location.StartLine));
        }

        [Fact]
        public void IdenticalRules_EmitEachMatchButMergePaths()
        {
            // Two rules with the same name and pattern both emit a token
            // (tokens are produced before paths are merged), but the two
            // resulting paths are identical (same position, context and
            // token type sequence) and must merge into a single active path.
            var lexer = new StepLexer();
            lexer.AddRule(new TokenRule("NUM", @"/[0-9]+/"));
            lexer.AddRule(new TokenRule("NUM", @"/[0-9]+/"));

            var input = Encoding.UTF8.GetBytes("12");
            lexer.Initialize(input, "test.txt");

            var firstStep = lexer.Step();

            Assert.Equal(2, firstStep.NewTokens.Count);
            Assert.All(firstStep.NewTokens, t => Assert.Equal("NUM", t.Type));
            Assert.Equal(1, lexer.ActivePaths.Count);
        }

        [Fact]
        public void DistinctRules_WithSamePattern_KeepSeparatePaths()
        {
            // Same position and context but a different token type sequence:
            // the paths must not be merged.
            var lexer = new StepLexer();
            lexer.AddRule(new TokenRule("NUMBER", @"/[0-9]+/"));
            lexer.AddRule(new TokenRule("DIGITS", @"/[0-9]+/"));

            var input = Encoding.UTF8.GetBytes("12");
            lexer.Initialize(input, "test.txt");

            var firstStep = lexer.Step();

            Assert.Equal(2, firstStep.NewTokens.Count);
            var types = firstStep.NewTokens.Select(t => t.Type).ToHashSet();
            Assert.Contains("NUMBER", types);
            Assert.Contains("DIGITS", types);
            Assert.Equal(2, lexer.ActivePaths.Count);
        }

        [Fact]
        public void LongAmbiguousStream_MergesIdenticalPathsEveryStep()
        {
            // With two identical rules every position is ambiguous: each
            // step clones the path per matching rule, and merging must fold
            // the identical clones back into one active path so the path
            // count stays bounded regardless of input length.
            var lexer = new StepLexer();
            lexer.AddRule(new TokenRule("NUM", @"/[0-9]+/"));
            lexer.AddRule(new TokenRule("NUM", @"/[0-9]+/"));
            lexer.AddRule(new TokenRule("WS", @"/[ \t\r\n]+/") { IsSkippable = true });

            var input = Encoding.UTF8.GetBytes("12 34 56 78 910");
            lexer.Initialize(input, "test.txt");

            var tokens = new List<StepToken>();
            var maxActivePaths = 0;
            var guard = 0;
            while (lexer.ActivePaths.Any(p => p.IsValid && p.Position < input.Length))
            {
                var result = lexer.Step();
                tokens.AddRange(result.NewTokens);
                maxActivePaths = Math.Max(maxActivePaths, lexer.ActivePaths.Count);
                if (++guard > 100_000)
                {
                    break;
                }
            }

            // Two tokens per number (one per rule), but only one active path
            // at any time because the clones merge every step.
            Assert.Equal(10, tokens.Count);
            Assert.Equal(1, maxActivePaths);
        }

        [Fact]
        public void LexerAllocation_GrowsLinearlyWithInputSize()
        {
            // Quadratic per-step work previously made per-token allocation
            // grow ~4x when the input grew 4x. Take the minimum of two
            // measurements per size to tolerate noise from parallel tests.
            var small = Math.Min(MeasureAllocatedPerToken(1_000), MeasureAllocatedPerToken(1_000));
            var large = Math.Min(MeasureAllocatedPerToken(4_000), MeasureAllocatedPerToken(4_000));

            // Linear growth keeps per-token cost flat; allow generous
            // headroom for noise but stay well below the ~4x a quadratic
            // term would produce.
            Assert.True(large <= small * 2.0,
                $"Per-token allocation grew super-linearly: {small:F0} bytes/token at 1k vs {large:F0} bytes/token at 4k");
        }

        /// <summary>
        /// Measure lexer allocations per produced token for the given input size.
        /// </summary>
        private static double MeasureAllocatedPerToken(int tokenCount)
        {
            var input = Encoding.UTF8.GetBytes(GenerateNumbers(tokenCount));
            var lexer = CreateNumberLexer();
            lexer.Initialize(input, "measure.txt");

            long allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
            var tokens = 0;
            var steps = 0;
            var maxSteps = input.Length * 10;

            while (lexer.ActivePaths.Any(p => p.IsValid && p.Position < input.Length) && steps < maxSteps)
            {
                var result = lexer.Step();
                tokens += result.NewTokens.Count;
                steps++;
                if (result.IsComplete)
                {
                    break;
                }
            }

            long allocated = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
            Assert.Equal(tokenCount, tokens);
            return (double)allocated / tokens;
        }
    }
}
