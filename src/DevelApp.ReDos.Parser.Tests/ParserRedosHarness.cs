using System;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using DevelApp.StepParser;

namespace DevelApp.ReDos.Parser.Tests
{
    /// <summary>The result of one parser adversarial run.</summary>
    /// <param name="Completed">Whether the parse finished within the hard wall timeout.</param>
    /// <param name="ElapsedMilliseconds">Measured wall time of the parse call.</param>
    /// <param name="Success">Whether the input derived (a parse graph was produced).</param>
    /// <param name="PathCount">Active parse paths reported by the engine after the run.</param>
    /// <param name="TokenCount">Tokens the lexer emitted for the input.</param>
    /// <param name="ErrorCount">Errors recorded by the engine.</param>
    public sealed record ParserRunResult(
        bool Completed,
        double ElapsedMilliseconds,
        bool Success,
        int PathCount,
        int TokenCount,
        int ErrorCount);

    /// <summary>
    /// Timing and budget helpers for the parser-layer ReDoS suite: a
    /// hard-timeout runner for <see cref="StepParserEngine.Parse"/>, the
    /// documented path-budget constants the immunity assertions cite,
    /// and the least-squares fit used by the scaling assertions. Compact
    /// copies of the lexer-suite helpers - the two suites must stay
    /// independent projects (the parser suite restores the private
    /// CognitiveGraph feed and cannot reference the lexer suite's
    /// assembly).
    /// </summary>
    public static class ParserRedosHarness
    {
        /// <summary>
        /// Hard wall timeout for one adversarial parse: 30 s. A healthy
        /// budgeted parse completes in milliseconds even at 1024 tokens;
        /// anything approaching this bound is a parser ReDoS regression
        /// and fails the test.
        /// </summary>
        public const int ParseHardTimeoutMs = 30_000;

        /// <summary>
        /// The StepParser's hard active-path budget (from the StepParser
        /// source): active paths are pruned to this bound every step, so
        /// however ambiguous the grammar, the engine never holds more
        /// than this many live derivations. This constant is the
        /// assertable parser-immunity invariant.
        /// </summary>
        public const int MaxActivePathBudget = 10;

        /// <summary>
        /// Run one adversarial parse under the hard wall timeout: load
        /// the grammar, parse the input, and return the observed
        /// outcome. A parse that exceeds the timeout is reported as
        /// incomplete - never as a hang.
        /// </summary>
        public static ParserRunResult RunParse(string grammarText, string input, int timeoutMs = ParseHardTimeoutMs)
        {
            var task = Task.Run(() => ParseOnce(grammarText, input));
            if (!task.Wait(timeoutMs))
            {
                return new ParserRunResult(false, double.NaN, false, -1, -1, -1);
            }

            return task.Result;
        }

        private static ParserRunResult ParseOnce(string grammarText, string input)
        {
            using var engine = new StepParserEngine();
            engine.LoadGrammarFromContent(grammarText, "redos-grammar");
            var watch = Stopwatch.StartNew();
            var result = engine.Parse(input, "redos-input.txt");
            watch.Stop();
            return new ParserRunResult(
                true,
                watch.Elapsed.TotalMilliseconds,
                result.Success,
                result.PathCount,
                result.Tokens.Count,
                result.Errors.Count);
        }

        /// <summary>Return the minimum of <paramref name="reps"/> measurements.</summary>
        public static double MinOf(int reps, Func<double> measure)
        {
            var best = double.MaxValue;
            for (var i = 0; i < reps; i++)
            {
                var value = measure();
                if (value < best)
                {
                    best = value;
                }
            }

            return best;
        }

        /// <summary>Least-squares fit of y = slope*x + intercept, with R².</summary>
        public static (double Slope, double Intercept, double R2) Fit(double[] xs, double[] ys)
        {
            var n = xs.Length;
            double sumX = 0, sumY = 0, sumXY = 0, sumX2 = 0, sumY2 = 0;
            for (var i = 0; i < n; i++)
            {
                var x = xs[i];
                var y = ys[i];
                sumX += x;
                sumY += y;
                sumXY += x * y;
                sumX2 += x * x;
                sumY2 += y * y;
            }

            var denominator = n * sumX2 - sumX * sumX;
            if (denominator == 0)
            {
                throw new InvalidOperationException("Cannot fit a line through a single distinct x value.");
            }

            var slope = (n * sumXY - sumX * sumY) / denominator;
            var intercept = (sumY - slope * sumX) / n;
            var r2Denominator = (n * sumX2 - sumX * sumX) * (n * sumY2 - sumY * sumY);
            var r2 = r2Denominator == 0
                ? 1.0
                : Math.Pow(n * sumXY - sumX * sumY, 2) / r2Denominator;
            return (slope, intercept, r2);
        }

        /// <summary>The engine-under-test version string for measurement reports.</summary>
        public static string StepParserEngineVersion() =>
            $"DevelApp.StepParser {typeof(StepParserEngine).Assembly.GetName().Version}";
    }
}
