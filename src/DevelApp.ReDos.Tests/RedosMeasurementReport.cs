using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using DevelApp.StepLexer;

namespace DevelApp.ReDos.Tests
{
    /// <summary>One length-timed measurement (StepLexer or non-backtracking).</summary>
    public sealed class LengthMeasurement
    {
        /// <summary>Input length in bytes.</summary>
        public int LengthBytes { get; set; }

        /// <summary>Elapsed time in milliseconds (minimum of the repeats).</summary>
        public double Milliseconds { get; set; }

        /// <summary>Tokens emitted (StepLexer runs only; -1 for regex runs).</summary>
        public int TokenCount { get; set; } = -1;

        /// <summary>Position where matching stalled (StepLexer runs only).</summary>
        public int NoMatchPosition { get; set; } = -1;

        /// <summary>Whether the whole input was consumed (StepLexer runs only).</summary>
        public bool CompletedInput { get; set; }
    }

    /// <summary>The traditional-engine baseline outcome for one pattern.</summary>
    public sealed class BacktrackingBaseline
    {
        /// <summary>Input length in bytes used for the baseline.</summary>
        public int LengthBytes { get; set; }

        /// <summary>Elapsed time in milliseconds.</summary>
        public double Milliseconds { get; set; }

        /// <summary>Whether the engine exceeded its match timeout.</summary>
        public bool TimedOut { get; set; }

        /// <summary>The match timeout applied, in milliseconds.</summary>
        public int TimeoutMs { get; set; }

        /// <summary>Whether the input matched.</summary>
        public bool IsMatch { get; set; }
    }

    /// <summary>The linear fit of one scaling pattern's timing curve.</summary>
    public sealed class ScalingFit
    {
        /// <summary>Input lengths measured, in bytes.</summary>
        public int[] LengthBytes { get; set; } = Array.Empty<int>();

        /// <summary>Minimum-of-five timings in milliseconds, parallel to the lengths.</summary>
        public double[] MinOfFiveMilliseconds { get; set; } = Array.Empty<double>();

        /// <summary>Fitted slope in nanoseconds per input byte.</summary>
        public double NanosecondsPerByte { get; set; }

        /// <summary>R² of the least-squares fit against a linear model.</summary>
        public double R2 { get; set; }

        /// <summary>
        /// Slope-stability bound: t(100 KB) / t(16 KB). Linear scaling
        /// predicts 6.25; a quadratic regression gives 39.
        /// </summary>
        public double GrowthRatioHundredKbOverSixteenKb { get; set; }
    }

    /// <summary>All measurements for one pattern in the report.</summary>
    public sealed class PatternReport
    {
        /// <summary>Catalog name of the pattern.</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>Where the pattern comes from.</summary>
        public string Source { get; set; } = string.Empty;

        /// <summary>The regex body under test.</summary>
        public string Pattern { get; set; } = string.Empty;

        /// <summary>The ReDoS family.</summary>
        public string Family { get; set; } = string.Empty;

        /// <summary>The adversarial input shape.</summary>
        public string InputKind { get; set; } = string.Empty;

        /// <summary>Whether the StepLexer matcher supports the pattern's constructs.</summary>
        public bool SupportedByStepLexer { get; set; }

        /// <summary>Whether the traditional backtracking engine is expected to blow up on the adversarial input.</summary>
        public bool ExpectedBacktrackingBlowup { get; set; }

        /// <summary>Honest-measurement note recorded with the pattern.</summary>
        public string Note { get; set; } = string.Empty;

        /// <summary>StepLexer timings across adversarial lengths.</summary>
        public List<LengthMeasurement> StepLexer { get; set; } = new();

        /// <summary>.NET non-backtracking reference timings across adversarial lengths.</summary>
        public List<LengthMeasurement> NonBacktracking { get; set; } = new();

        /// <summary>Traditional backtracking engine outcome (representative length).</summary>
        public BacktrackingBaseline? Backtracking { get; set; }

        /// <summary>Linear fit of the StepLexer scaling curve (scaling patterns only).</summary>
        public ScalingFit? Scaling { get; set; }
    }

    /// <summary>The full issue-#103 measurement report.</summary>
    public sealed class RedosMeasurementReport
    {
        /// <summary>When the report was generated (UTC).</summary>
        public string GeneratedAtUtc { get; set; } = string.Empty;

        /// <summary>Engine under test (name + assembly version).</summary>
        public string EngineUnderTest { get; set; } = string.Empty;

        /// <summary>.NET runtime of the measurement run.</summary>
        public string DotNetRuntime { get; set; } = string.Empty;

        /// <summary>Operating system of the measurement run.</summary>
        public string OperatingSystem { get; set; } = string.Empty;

        /// <summary>Logical processors available.</summary>
        public int ProcessorCount { get; set; }

        /// <summary>Globalization mode of the run (invariant vs ICU).</summary>
        public string Globalization { get; set; } = string.Empty;

        /// <summary>Hard StepLexer per-case timeout used by the suite, in milliseconds.</summary>
        public int StepLexerHardTimeoutMs { get; set; }

        /// <summary>Match timeout applied to the backtracking baseline, in milliseconds.</summary>
        public int BacktrackingTimeoutMs { get; set; }

        /// <summary>Per-pattern measurements.</summary>
        public List<PatternReport> Patterns { get; set; } = new();

        /// <summary>
        /// Build the full report: the article/corpus immunity grid, the
        /// baseline comparisons on identical inputs, the scaling fits for
        /// the supported-subset analogues, and the many-token overhead
        /// measurement.
        /// </summary>
        public static RedosMeasurementReport Build()
        {
            var report = new RedosMeasurementReport
            {
                GeneratedAtUtc = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                EngineUnderTest = RedosMeasurementHarness.StepLexerEngineVersion(),
                DotNetRuntime = $"{RuntimeInformation.FrameworkDescription} (System.Text.RegularExpressions backtracking + RegexOptions.NonBacktracking)",
                OperatingSystem = RuntimeInformation.OSDescription,
                ProcessorCount = Environment.ProcessorCount,
                Globalization = System.Globalization.CultureInfo.InvariantCulture.Equals(System.Globalization.CultureInfo.CurrentCulture)
                    ? "invariant"
                    : "ICU",
                StepLexerHardTimeoutMs = RedosMeasurementHarness.StepLexerHardTimeoutMs,
                BacktrackingTimeoutMs = RedosMeasurementHarness.BacktrackingBaselineTimeoutMs,
            };

            var lengths = new[] { RedosPatternCatalog.SmallLengthBytes, RedosPatternCatalog.MediumLengthBytes, RedosPatternCatalog.LargeLengthBytes };

            foreach (var pattern in RedosPatternCatalog.ArticleAndCorpusPatterns)
            {
                var entry = CreatePatternReport(pattern, lengths);
                entry.Backtracking = CreateBacktrackingBaseline(pattern);
                report.Patterns.Add(entry);
            }

            foreach (var pattern in RedosPatternCatalog.ScalingPatterns)
            {
                var entry = CreatePatternReport(pattern, lengths);
                entry.Backtracking = CreateBacktrackingBaseline(pattern);
                report.Patterns.Add(entry);
            }

            var manyToken = RedosPatternCatalog.ManyTokenPattern;
            var manyTokenEntry = new PatternReport
            {
                Name = manyToken.Name,
                Source = manyToken.Source,
                Pattern = manyToken.CorePattern,
                Family = manyToken.Family,
                InputKind = manyToken.InputKind.ToString(),
                SupportedByStepLexer = manyToken.SupportedByStepLexer,
            };
            foreach (var length in new[] { RedosPatternCatalog.SmallLengthBytes, RedosPatternCatalog.MediumLengthBytes })
            {
                var input = manyToken.BuildInput(length);
                var run = RedosMeasurementHarness.RunStepLexer(manyToken.LexerRulePattern, input);
                manyTokenEntry.StepLexer.Add(new LengthMeasurement
                {
                    LengthBytes = length,
                    Milliseconds = run.ElapsedMilliseconds,
                    TokenCount = run.TokenCount,
                    NoMatchPosition = run.LastNoMatchPosition,
                    CompletedInput = run.CompletedInput,
                });
            }

            report.Patterns.Add(manyTokenEntry);
            return report;
        }

        private static PatternReport CreatePatternReport(RedosPattern pattern, int[] lengths)
        {
            var entry = new PatternReport
            {
                Name = pattern.Name,
                Source = pattern.Source,
                Pattern = pattern.CorePattern,
                Family = pattern.Family,
                InputKind = pattern.InputKind.ToString(),
                SupportedByStepLexer = pattern.SupportedByStepLexer,
                ExpectedBacktrackingBlowup = pattern.ExpectedBacktrackingBlowup,
                Note = pattern.Note,
            };

            foreach (var length in lengths)
            {
                var input = pattern.BuildInput(length);

                var stepLexerRun = RedosMeasurementHarness.RunStepLexer(pattern.LexerRulePattern, input);
                entry.StepLexer.Add(new LengthMeasurement
                {
                    LengthBytes = length,
                    Milliseconds = stepLexerRun.ElapsedMilliseconds,
                    TokenCount = stepLexerRun.TokenCount,
                    NoMatchPosition = stepLexerRun.LastNoMatchPosition,
                    CompletedInput = stepLexerRun.CompletedInput,
                });

                var nonBacktrackingRun = RedosMeasurementHarness.RunNonBacktrackingBaseline(pattern.CorePattern, input);
                entry.NonBacktracking.Add(new LengthMeasurement
                {
                    LengthBytes = length,
                    Milliseconds = nonBacktrackingRun.ElapsedMilliseconds,
                });
            }

            if (pattern.SupportedByStepLexer)
            {
                var scalingLengths = new[] { 1_024, 4_096, 16_384, 32_768, 65_536, 102_400 };
                RedosMeasurementHarness.RunStepLexer(pattern.LexerRulePattern, pattern.BuildInput(scalingLengths[0]));
                var times = scalingLengths
                    .Select(length => RedosMeasurementHarness.MinOf(
                        5,
                        () => RedosMeasurementHarness.RunStepLexer(pattern.LexerRulePattern, pattern.BuildInput(length)).ElapsedMilliseconds))
                    .ToArray();
                var (slope, _, r2) = RedosMeasurementHarness.Fit(scalingLengths.Select(l => (double)l).ToArray(), times);

                // Slope stability via the growth ratio t(100 KB) / t(16 KB)
                // (linear predicts 6.25, quadratic 39); absolute and
                // marginal per-byte rates at these microsecond scales are
                // dominated by timer noise - see LinearScalingTests.
                entry.Scaling = new ScalingFit
                {
                    LengthBytes = scalingLengths,
                    MinOfFiveMilliseconds = times,
                    NanosecondsPerByte = slope * 1_000_000,
                    R2 = r2,
                    GrowthRatioHundredKbOverSixteenKb = times[^1] / times[2],
                };
            }

            return entry;
        }

        private static BacktrackingBaseline? CreateBacktrackingBaseline(RedosPattern pattern)
        {
            var input = pattern.BuildInput(pattern.BaselineLengthBytes);
            var run = RedosMeasurementHarness.RunBacktrackingBaseline(pattern.CorePattern, input);
            return new BacktrackingBaseline
            {
                LengthBytes = pattern.BaselineLengthBytes,
                Milliseconds = run.ElapsedMilliseconds,
                TimedOut = run.TimedOut,
                TimeoutMs = RedosMeasurementHarness.BacktrackingBaselineTimeoutMs,
                IsMatch = run.IsMatch,
            };
        }

        /// <summary>Serialize the report to indented JSON.</summary>
        public string ToJson() =>
            JsonSerializer.Serialize(this, new JsonSerializerOptions
            {
                WriteIndented = true,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            });
    }
}
