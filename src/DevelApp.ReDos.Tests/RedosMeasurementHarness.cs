using System;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using DevelApp.StepLexer;
using StepLexerEngine = DevelApp.StepLexer.StepLexer;

namespace DevelApp.ReDos.Tests
{
    /// <summary>The result of one StepLexer adversarial run.</summary>
    /// <param name="Completed">Whether the run finished within the hard timeout.</param>
    /// <param name="ElapsedMilliseconds">Measured wall time of the tokenization loop.</param>
    /// <param name="TokenCount">Tokens emitted before termination.</param>
    /// <param name="LastNoMatchPosition">Lexer position where matching stalled (-1 if never).</param>
    /// <param name="CompletedInput">Whether the whole input was consumed.</param>
    public sealed record StepLexerRunResult(
        bool Completed,
        double ElapsedMilliseconds,
        int TokenCount,
        int LastNoMatchPosition,
        bool CompletedInput);

    /// <summary>The result of one .NET regex baseline run.</summary>
    /// <param name="TimedOut">Whether the regex exceeded its match timeout.</param>
    /// <param name="ElapsedMilliseconds">Wall time (>= timeout when timed out).</param>
    /// <param name="IsMatch">Whether the input matched (false when timed out).</param>
    public sealed record RegexBaselineResult(bool TimedOut, double ElapsedMilliseconds, bool IsMatch);

    /// <summary>
    /// Timing helpers for the ReDoS-immunity suite (issue #103): a
    /// hard-timeout runner for the StepLexer tokenization, baseline
    /// runners for the .NET backtracking and non-backtracking engines,
    /// and the least-squares fit used by the linear-scaling assertions.
    /// </summary>
    public static class RedosMeasurementHarness
    {
        /// <summary>
        /// Hard per-case timeout for StepLexer runs: 10 s. A healthy run
        /// completes in milliseconds; anything approaching this bound is a
        /// ReDoS regression and fails the test.
        /// </summary>
        public const int StepLexerHardTimeoutMs = 10_000;

        /// <summary>
        /// Match timeout for the traditional backtracking baseline: 500 ms
        /// is orders of magnitude above a healthy match and low enough to
        /// keep the suite fast when the vulnerable patterns blow up.
        /// </summary>
        public const int BacktrackingBaselineTimeoutMs = 500;

        /// <summary>Match timeout for the non-backtracking reference baseline.</summary>
        public const int NonBacktrackingTimeoutMs = 5_000;

        /// <summary>
        /// Tokenize the adversarial input with a single rule and return the
        /// observed outcome. The tokenization loop is the shipped
        /// convention from the StepLexer test suite; the guard bound is the
        /// input length itself (zero-width matches are rejected by the
        /// engine, so a valid tokenization can never take more steps than
        /// input bytes).
        /// </summary>
        public static StepLexerRunResult RunStepLexer(string lexerRulePattern, byte[] input, int timeoutMs = StepLexerHardTimeoutMs)
        {
            var task = Task.Run(() => TokenizeToEnd(lexerRulePattern, input));
            if (!task.Wait(timeoutMs))
            {
                return new StepLexerRunResult(false, double.NaN, -1, -1, false);
            }

            var (tokenCount, lastNoMatchPosition, completedInput, elapsedMs) = task.Result;
            return new StepLexerRunResult(true, elapsedMs, tokenCount, lastNoMatchPosition, completedInput);
        }

        private static (int TokenCount, int LastNoMatchPosition, bool CompletedInput, double ElapsedMs) TokenizeToEnd(
            string lexerRulePattern, byte[] input)
        {
            var lexer = new StepLexerEngine();
            lexer.AddRule(new TokenRule("ADVERSARIAL", lexerRulePattern));
            lexer.Initialize(input, "redos-input.txt");

            var tokenCount = 0;
            var guard = 0;
            var watch = Stopwatch.StartNew();
            while (lexer.ActivePaths.Any(p => p.IsValid && p.Position < input.Length))
            {
                tokenCount += lexer.Step().NewTokens.Count;
                if (++guard > input.Length + 16)
                {
                    break;
                }
            }
            watch.Stop();

            return (tokenCount, lexer.LastNoMatchPosition, lexer.CompletedInput, watch.Elapsed.TotalMilliseconds);
        }

        /// <summary>
        /// Run the .NET backtracking engine (the traditional-engine
        /// baseline) with a hard match timeout. Exponential blowups are
        /// reported as timeouts, never as hangs.
        /// </summary>
        public static RegexBaselineResult RunBacktrackingBaseline(string corePattern, byte[] input)
        {
            var regex = new Regex(corePattern, RegexOptions.None, TimeSpan.FromMilliseconds(BacktrackingBaselineTimeoutMs));
            return RunBaseline(regex, input);
        }

        /// <summary>
        /// Run the .NET non-backtracking engine (the linear-time reference,
        /// RegexOptions.NonBacktracking) with a hard match timeout.
        /// </summary>
        public static RegexBaselineResult RunNonBacktrackingBaseline(string corePattern, byte[] input)
        {
            var regex = new Regex(corePattern, RegexOptions.NonBacktracking, TimeSpan.FromMilliseconds(NonBacktrackingTimeoutMs));
            return RunBaseline(regex, input);
        }

        private static RegexBaselineResult RunBaseline(Regex regex, byte[] input)
        {
            var text = Encoding.UTF8.GetString(input);
            var watch = Stopwatch.StartNew();
            try
            {
                var isMatch = regex.IsMatch(text);
                watch.Stop();
                return new RegexBaselineResult(false, watch.Elapsed.TotalMilliseconds, isMatch);
            }
            catch (RegexMatchTimeoutException)
            {
                watch.Stop();
                return new RegexBaselineResult(true, watch.Elapsed.TotalMilliseconds, false);
            }
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
        public static string StepLexerEngineVersion() =>
            $"DevelApp.StepLexer {typeof(StepLexerEngine).Assembly.GetName().Version}";
    }
}
