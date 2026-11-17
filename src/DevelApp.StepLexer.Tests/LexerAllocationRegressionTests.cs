using System.Text;

namespace DevelApp.StepLexer.Tests
{
    /// <summary>
    /// Allocation regression test for the StepLexer quadratic growth fix
    /// (#52). Lives in the non-parallel collection
    /// (<see cref="NonParallelCollection"/>): the measurement reads the
    /// process-wide <c>GC.GetTotalAllocatedBytes</c> counter, so any test
    /// running concurrently would add its allocations to the window and
    /// flake the linear-growth assertion (observed on loaded CI runners:
    /// 2811 vs 5776 bytes/token against a 2x threshold).
    /// </summary>
    [Collection(NonParallelCollection.Name)]
    public class LexerAllocationRegressionTests
    {
        [Fact]
        public void LexerAllocation_GrowsLinearlyWithInputSize()
        {
            // Quadratic per-step work previously made per-token allocation
            // grow ~8x when the input grew 8x. Take the minimum of three
            // measurements per size; with parallelization disabled for this
            // collection there is no cross-test allocation during the window.
            var small = MinAllocatedPerToken(1_000);
            var large = MinAllocatedPerToken(8_000);

            // Linear growth keeps per-token cost flat; allow generous
            // headroom for residual noise but stay well below the ~8x a
            // quadratic term would produce.
            Assert.True(large <= small * 2.0,
                $"Per-token allocation grew super-linearly: {small:F0} bytes/token at 1k vs {large:F0} bytes/token at 8k");
        }

        private static double MinAllocatedPerToken(int tokenCount, int samples = 3)
        {
            var min = double.MaxValue;
            for (var i = 0; i < samples; i++)
            {
                min = Math.Min(min, MeasureAllocatedPerToken(tokenCount));
            }
            return min;
        }

        /// <summary>
        /// Measure lexer allocations per produced token for the given input size.
        /// </summary>
        private static double MeasureAllocatedPerToken(int tokenCount)
        {
            var input = Encoding.UTF8.GetBytes(LexerPerformanceRegressionTests.GenerateNumbers(tokenCount));
            var lexer = LexerPerformanceRegressionTests.CreateNumberLexer();
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
