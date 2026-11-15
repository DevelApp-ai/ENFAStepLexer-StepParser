using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using Xunit.Abstractions;

namespace DevelApp.ReDos.Parser.Tests
{
    /// <summary>
    /// Parser-layer linear-scaling validation (issue #103 extension):
    /// the single-token grammars must scale linearly from 1 KB to
    /// 100 KB of input. The whole input is one token on purpose: the
    /// parser's per-token bookkeeping is O(position), so a fit with a
    /// token count growing in n would measure that known quadratic
    /// characteristic instead of parser linearity (the same reasoning
    /// as the lexer suite's many-token measurement). For each grammar
    /// the suite fits parse time against input length with least
    /// squares and asserts the fit quality (R²) and the growth ratio
    /// t(100 KB)/t(16 KB), mirroring the lexer suite's constants.
    /// </summary>
    public class ParserLinearScalingTests
    {
        private readonly ITestOutputHelper _output;

        public ParserLinearScalingTests(ITestOutputHelper output)
        {
            _output = output;
        }

        /// <summary>Minimum acceptable R² for the linear fit (secondary shape check; the growth bound is the primary discriminator).</summary>
        public const double MinimumR2 = 0.90;

        /// <summary>
        /// Maximum allowed ratio t(100 KB) / t(16 KB). Linear scaling
        /// predicts 6.25 (= 102400/16384); a quadratic regression gives
        /// 39. The bound sits well above linear + CI timing noise and
        /// well below any superlinear regression.
        /// </summary>
        public const double MaximumGrowthRatio = 12.0;

        /// <summary>Input lengths (bytes of the single token) used for the fits.</summary>
        public static int[] ScalingLengths => new[] { 1_024, 4_096, 16_384, 32_768, 65_536, 102_400 };

        public static TheoryData<string> ScalingGrammarNames
        {
            get
            {
                var data = new TheoryData<string>();
                foreach (var grammar in GrammarRedosCatalog.ScalingGrammars)
                {
                    data.Add(grammar.Name);
                }

                return data;
            }
        }

        [Theory]
        [MemberData(nameof(ScalingGrammarNames))]
        public void StepParser_ScalesLinearly_From1KbTo100Kb(string name)
        {
            var grammar = GrammarRedosCatalog.ScalingGrammars.Single(g => g.Name == name);
            var lengths = ScalingLengths.Select(l => (double)l).ToArray();

            // Warm-up run so grammar loading and first-touch costs do not
            // pollute the fit.
            ParserRedosHarness.RunParse(grammar.GrammarText, grammar.BuildInput(ScalingLengths[0]));

            // The parses here are sub-millisecond, so on a shared runner
            // one noisy window can break an otherwise linear fit (a
            // single high sample skews R², a single low sample skews the
            // growth ratio). When a threshold is violated, re-measure the
            // whole curve, bounded, so CPU contention in one window
            // cannot produce a false ReDoS signal. The LAST attempt is
            // what gets asserted, so a genuinely superlinear regression
            // still fails deterministically on every attempt.
            const int MaxAttempts = 3;

            var times = new double[lengths.Length];
            double slope = 0, r2 = 0, growthRatio = 0;
            var violations = new List<string>();

            for (var attempt = 1; ; attempt++)
            {
                for (var i = 0; i < lengths.Length; i++)
                {
                    var input = grammar.BuildInput(ScalingLengths[i]);
                    times[i] = ParserRedosHarness.MinOf(
                        5,
                        () => ParserRedosHarness.RunParse(grammar.GrammarText, input).ElapsedMilliseconds);
                }

                (slope, _, r2) = ParserRedosHarness.Fit(lengths, times);
                var firstLargeIndex = Array.IndexOf(ScalingLengths, 16_384);
                growthRatio = times[^1] / times[firstLargeIndex];

                _output.WriteLine(
                    $"{name}: attempt {attempt}/{MaxAttempts}: slope={slope * 1_000_000:F1} ns/byte, R²={r2:F4}, growth ratio 100KB/16KB={growthRatio:F2}");
                for (var i = 0; i < lengths.Length; i++)
                {
                    _output.WriteLine($"  {lengths[i],6:F0} bytes -> {times[i],9:F3} ms");
                }

                var r2Violation = r2 < MinimumR2;
                var ratioViolation = growthRatio > MaximumGrowthRatio;
                if (!r2Violation && !ratioViolation)
                {
                    break;
                }

                violations.Add(
                    $"attempt {attempt}: R²={r2:F4}"
                        + (r2Violation ? $" (< {MinimumR2})" : string.Empty)
                        + $", growth ratio={growthRatio:F2}"
                        + (ratioViolation ? $" (> {MaximumGrowthRatio:F1})" : string.Empty));

                if (attempt >= MaxAttempts)
                {
                    break;
                }
            }

            Assert.True(
                r2 >= MinimumR2,
                $"{name}: parse time vs length does not fit a line (R²={r2:F4} < {MinimumR2}) after {MaxAttempts} attempts. "
                    + "Attempt summary: " + string.Join("; ", violations) + ". Timing data: "
                    + string.Join(", ", times.Select((t, i) => $"{lengths[i]:F0}b={t:F3}ms")));

            Assert.True(
                growthRatio <= MaximumGrowthRatio,
                $"{name}: parse time grows superlinearly with input length (t(100KB)/t(16KB)={growthRatio:F2} > {MaximumGrowthRatio:F1}; "
                    + $"linear predicts 6.25, quadratic 39) after {MaxAttempts} attempts. Attempt summary: "
                    + string.Join("; ", violations) + ". Timing data: "
                    + string.Join(", ", times.Select((t, i) => $"{lengths[i]:F0}b={t:F3}ms")));
        }
    }
}
