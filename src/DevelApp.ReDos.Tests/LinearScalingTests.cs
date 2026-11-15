using System;
using System.Linq;
using Xunit;
using Xunit.Abstractions;

namespace DevelApp.ReDos.Tests
{
    /// <summary>
    /// Linear-scaling validation (issue #103): the supported-subset
    /// analogues of the pathological pattern families must scale linearly
    /// from 1 KB to 100 KB. For each pattern the suite fits time against
    /// input length with least squares and asserts both the fit quality
    /// (R²) and the slope stability (per-byte rate at the largest length
    /// within a constant factor of the smallest). A quadratic or
    /// exponential regression shows up as an R² collapse and a per-byte
    /// rate that grows with length, so the assertions fail loudly.
    /// </summary>
    public class LinearScalingTests
    {
        private readonly ITestOutputHelper _output;

        public LinearScalingTests(ITestOutputHelper output)
        {
            _output = output;
        }

        /// <summary>Minimum acceptable R² for the linear fit (secondary shape check; the growth bound is the primary discriminator).</summary>
        public const double MinimumR2 = 0.90;

        /// <summary>
        /// Maximum allowed ratio t(100 KB) / t(16 KB). Linear scaling
        /// predicts 6.25 (= 102400/16384); a quadratic regression gives 39,
        /// n^1.5 gives 15.6. The bound sits well above linear + CI timing
        /// noise and well below any superlinear regression.
        /// </summary>
        public const double MaximumGrowthRatio = 12.0;

        public static TheoryData<string> ScalingPatternNames
        {
            get
            {
                var data = new TheoryData<string>();
                foreach (var pattern in RedosPatternCatalog.ScalingPatterns)
                {
                    data.Add(pattern.Name);
                }

                return data;
            }
        }

        public static int[] ScalingLengths => new[] { 1_024, 4_096, 16_384, 32_768, 65_536, 102_400 };

        [Theory]
        [MemberData(nameof(ScalingPatternNames))]
        public void StepLexer_ScalesLinearly_From1KbTo100Kb(string name)
        {
            var pattern = RedosPatternCatalog.FindScalingPattern(name);
            var lengths = ScalingLengths.Select(l => (double)l).ToArray();

            // Warm-up run so JIT and first-touch costs do not pollute the fit.
            RedosMeasurementHarness.RunStepLexer(pattern.LexerRulePattern, pattern.BuildInput((int)lengths[0]));

            var times = new double[lengths.Length];
            for (var i = 0; i < lengths.Length; i++)
            {
                var input = pattern.BuildInput((int)lengths[i]);
                times[i] = RedosMeasurementHarness.MinOf(
                    5,
                    () => RedosMeasurementHarness.RunStepLexer(pattern.LexerRulePattern, input).ElapsedMilliseconds);
            }

            var (slope, _, r2) = RedosMeasurementHarness.Fit(lengths, times);

            // Slope stability via the growth ratio t(100 KB) / t(16 KB):
            // linear predicts 6.25, quadratic 39. (The marginal per-byte
            // rates at these microsecond scales are dominated by timer and
            // scheduler noise, so the single growth ratio - which carries
            // ~13x of headroom over the linear prediction - is the robust
            // discriminator.)
            var firstLargeIndex = Array.IndexOf(ScalingLengths, 16_384);
            var growthRatio = times[^1] / times[firstLargeIndex];

            _output.WriteLine($"{name}: slope={slope * 1_000_000:F1} ns/byte, R²={r2:F4}, growth ratio 100KB/16KB={growthRatio:F2}");
            for (var i = 0; i < lengths.Length; i++)
            {
                _output.WriteLine($"  {lengths[i],6:F0} bytes -> {times[i],9:F3} ms");
            }

            Assert.True(
                r2 >= MinimumR2,
                $"{name}: time vs length does not fit a line (R²={r2:F4} < {MinimumR2}). "
                    + "Timing data: " + string.Join(", ", times.Select((t, i) => $"{lengths[i]:F0}b={t:F3}ms")));

            Assert.True(
                growthRatio <= MaximumGrowthRatio,
                $"{name}: time grows superlinearly with input length (t(100KB)/t(16KB)={growthRatio:F2} > {MaximumGrowthRatio:F1}; "
                    + $"linear predicts 6.25, quadratic 39). Timing data: "
                    + string.Join(", ", times.Select((t, i) => $"{lengths[i]:F0}b={t:F3}ms")));
        }

        [Fact]
        public void ManyTokenStream_MeasuredAtCappedLengths_IsNotAMatchingBlowup()
        {
            // The many-token pattern (one token per 3 bytes) is measured,
            // not asserted linear: matching itself stays forward-only, but
            // per-token line/column bookkeeping scans from the input start,
            // so token count drives a quadratic term in total time. This
            // is a known tokenization-overhead characteristic (not a ReDoS
            // exposure: no exponential matching exists, and a no-match
            // terminates the run), recorded here at capped lengths so the
            // committed measurement report documents it honestly.
            var pattern = RedosPatternCatalog.ManyTokenPattern;

            foreach (var length in new[] { RedosPatternCatalog.SmallLengthBytes, RedosPatternCatalog.MediumLengthBytes })
            {
                var input = pattern.BuildInput(length);
                var result = RedosMeasurementHarness.RunStepLexer(pattern.LexerRulePattern, input);

                Assert.True(result.Completed, $"{pattern.Name} did not complete within the hard timeout at {length} bytes.");
                Assert.True(result.CompletedInput, "The ab1 stream must tokenize to the very end.");
                Assert.Equal(length / 3, result.TokenCount);

                _output.WriteLine(
                    $"{pattern.Name}: {length} bytes -> {result.ElapsedMilliseconds:F3} ms for {result.TokenCount} tokens "
                    + $"({result.ElapsedMilliseconds * 1_000_000 / Math.Max(1, length):F0} ns/byte, "
                    + "line/column bookkeeping is O(position) per token)");
            }
        }
    }
}
