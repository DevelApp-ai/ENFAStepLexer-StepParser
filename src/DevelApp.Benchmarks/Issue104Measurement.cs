using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DevelApp.StepLexer;

using Lexer = DevelApp.StepLexer.StepLexer;

namespace DevelApp.Benchmarks
{
    /// <summary>
    /// Non-BenchmarkDotNet measurement mode for issue #104 (two-phase-regex
    /// article): benign-pattern overhead and allocation ratio of the
    /// two-phase engine vs compiled .NET Regex, per article pattern class.
    /// Invoked via <c>--measure-issue104 &lt;outDir&gt;</c>; writes
    /// <c>issue-104-benign-overhead.csv</c>, <c>issue-104-summary.json</c> and
    /// <c>RESULTS.md</c> into the output directory. RE2 is not available on
    /// .NET, so the reference engine here is .NET Regex only; the article's
    /// RE2 column remains an external-baseline item.
    /// </summary>
    public static class Issue104Measurement
    {
        private const int WarmupReps = 3;
        private const int Reps = 10;

        private sealed record Row(
            string Class, int InputChars, int EngineMatches, int DotNetMatches,
            double Phase1MedianMs, double StepMedianMs, double DotNetMedianMs,
            double Phase1OverheadPct, double StepOverheadPct,
            long Phase1AllocBytes, long StepAllocBytes, long DotNetAllocBytes,
            double Phase1AllocRatioPct, double StepAllocRatioPct);

        /// <summary>Run the measurement and write results into <paramref name="outDir"/>.</summary>
        public static int Run(string outDir)
        {
            Directory.CreateDirectory(outDir);
            var rows = new List<Row>();

            foreach (var pc in PatternClass.All)
            {
                var input = pc.CreateInput();
                var bytes = Encoding.UTF8.GetBytes(input);
                var regex = new Regex(pc.DotNetPattern, RegexOptions.Compiled);
                var lexer = RegexOverheadBenchmarks.CreateLexer(pc);
                var view = new ZeroCopyStringView(bytes);

                var engineMatches = 0;
                var dotNetMatches = 0;
                for (var i = 0; i < WarmupReps; i++)
                {
                    engineMatches = EngineRun(lexer, bytes);
                    Phase1Run(lexer, view);
                    dotNetMatches = DotNetRun(regex, input);
                }

                var phase1Times = new List<double>(Reps);
                var stepTimes = new List<double>(Reps);
                var dotNetTimes = new List<double>(Reps);
                for (var i = 0; i < Reps; i++)
                {
                    phase1Times.Add(TimedMs(() => Phase1Run(lexer, view)));
                    stepTimes.Add(TimedMs(() => EngineRun(lexer, bytes)));
                    dotNetTimes.Add(TimedMs(() => DotNetRun(regex, input)));
                }

                // Allocation per operation (rule compilation is outside the timed
                // region: the lexer object is reused, the regex is compiled once -
                // both are long-lived in real use).
                var phase1Alloc = AllocatedBytes(() => Phase1Run(lexer, view));
                var stepAlloc = AllocatedBytes(() => EngineRun(lexer, bytes));
                var dotNetAlloc = AllocatedBytes(() => DotNetRun(regex, input));

                var phase1Median = Median(phase1Times);
                var stepMedian = Median(stepTimes);
                var dotNetMedian = Median(dotNetTimes);
                double Overhead(double engineMs) => dotNetMedian > 0 ? (engineMs - dotNetMedian) / dotNetMedian * 100.0 : 0.0;
                double AllocRatio(long engineB) => dotNetAlloc > 0 ? (double)(engineB - dotNetAlloc) / dotNetAlloc * 100.0 : 0.0;

                rows.Add(new Row(pc.Name, input.Length, engineMatches, dotNetMatches,
                    phase1Median, stepMedian, dotNetMedian,
                    Overhead(phase1Median), Overhead(stepMedian),
                    phase1Alloc, stepAlloc, dotNetAlloc,
                    AllocRatio(phase1Alloc), AllocRatio(stepAlloc)));

                Console.WriteLine($"[{pc.Name}] phase1 {phase1Median:F3} ms (overhead {Overhead(phase1Median):F1}%), " +
                                  $"step-loop {stepMedian:F3} ms (overhead {Overhead(stepMedian):F1}%), dotnet {dotNetMedian:F3} ms");
            }

            WriteCsv(Path.Combine(outDir, "issue-104-benign-overhead.csv"), rows);
            WriteJson(Path.Combine(outDir, "issue-104-summary.json"), rows);
            WriteMarkdown(Path.Combine(outDir, "RESULTS.md"), rows);
            return 0;
        }

        private static int EngineRun(Lexer lexer, byte[] bytes)
        {
            lexer.Initialize(bytes, "benchmark.txt");
            var count = 0;
            var guard = 0;
            var maxSteps = bytes.Length * 10 + 100;
            while (lexer.ActivePaths.Any(p => p.IsValid && p.Position < bytes.Length))
            {
                var result = lexer.Step();
                count += result.NewTokens.Count;
                if (++guard > maxSteps)
                {
                    break;
                }
            }
            return count;
        }

        private static int Phase1Run(Lexer lexer, ZeroCopyStringView view)
        {
            lexer.Phase1_LexicalScan(view);
            return lexer.Phase1Results.Count;
        }

        private static int DotNetRun(Regex regex, string input)
        {
            var count = 0;
            foreach (Match m in regex.Matches(input))
            {
                if (m.Length > 0)
                {
                    count++;
                }
            }
            return count;
        }

        private static double TimedMs(Action action)
        {
            var sw = Stopwatch.StartNew();
            action();
            sw.Stop();
            return sw.Elapsed.TotalMilliseconds;
        }

        private static long AllocatedBytes(Action action)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            action();
            return GC.GetAllocatedBytesForCurrentThread() - before;
        }

        private static double Median(List<double> values)
        {
            var sorted = values.OrderBy(v => v).ToList();
            return sorted.Count % 2 == 1
                ? sorted[sorted.Count / 2]
                : (sorted[sorted.Count / 2 - 1] + sorted[sorted.Count / 2]) / 2.0;
        }

        private static void WriteCsv(string path, List<Row> rows)
        {
            var sb = new StringBuilder();
            sb.AppendLine("pattern_class,input_chars,engine_matches,dotnet_matches,phase1_median_ms,step_median_ms,dotnet_median_ms,phase1_overhead_pct,step_overhead_pct,phase1_alloc_bytes,step_alloc_bytes,dotnet_alloc_bytes,phase1_alloc_ratio_pct,step_alloc_ratio_pct");
            foreach (var r in rows)
            {
                string F(double v) => v.ToString("F4", System.Globalization.CultureInfo.InvariantCulture);
                string P(double v) => v.ToString("F2", System.Globalization.CultureInfo.InvariantCulture);
                sb.AppendLine(string.Join(",", r.Class, r.InputChars, r.EngineMatches, r.DotNetMatches,
                    F(r.Phase1MedianMs), F(r.StepMedianMs), F(r.DotNetMedianMs),
                    P(r.Phase1OverheadPct), P(r.StepOverheadPct),
                    r.Phase1AllocBytes, r.StepAllocBytes, r.DotNetAllocBytes,
                    P(r.Phase1AllocRatioPct), P(r.StepAllocRatioPct)));
            }
            File.WriteAllText(path, sb.ToString());
        }

        private static void WriteJson(string path, List<Row> rows)
        {
            var doc = new
            {
                issue = "ENFAStepLexer-StepParser#104",
                measuredUtc = DateTimeOffset.UtcNow.ToString("o"),
                environment = new
                {
                    framework = RuntimeInformation.FrameworkDescription,
                    os = RuntimeInformation.OSDescription,
                    architecture = RuntimeInformation.ProcessArchitecture.ToString(),
                    processorCount = Environment.ProcessorCount,
                },
                method = new
                {
                    warmupReps = WarmupReps,
                    reps = Reps,
                    statistic = "median",
                    engine = "StepLexer single-rule greedy: phase1 = Phase1_LexicalScan, step = full Initialize+Step loop (token-rule compile excluded; lexer reused across reps)",
                    reference = "System.Text.RegularExpressions.Regex (RegexOptions.Compiled), Regex.Matches",
                    allocation = "GC.GetAllocatedBytesForCurrentThread delta around one full scan",
                },
                rows = rows.Select(r => new
                {
                    patternClass = r.Class,
                    inputChars = r.InputChars,
                    engineMatches = r.EngineMatches,
                    dotNetMatches = r.DotNetMatches,
                    phase1MedianMs = r.Phase1MedianMs,
                    stepMedianMs = r.StepMedianMs,
                    dotNetMedianMs = r.DotNetMedianMs,
                    phase1OverheadPct = r.Phase1OverheadPct,
                    stepOverheadPct = r.StepOverheadPct,
                    phase1AllocBytes = r.Phase1AllocBytes,
                    stepAllocBytes = r.StepAllocBytes,
                    dotNetAllocBytes = r.DotNetAllocBytes,
                    phase1AllocRatioPct = r.Phase1AllocRatioPct,
                    stepAllocRatioPct = r.StepAllocRatioPct,
                }),
            };
            File.WriteAllText(path, JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = true }));
        }

        private static void WriteMarkdown(string path, List<Row> rows)
        {
            var sb = new StringBuilder();
            sb.AppendLine("# Issue #104 measurement: benign-pattern overhead vs .NET Regex");
            sb.AppendLine();
            sb.AppendLine($"Measured (UTC): {DateTimeOffset.UtcNow:o}");
            sb.AppendLine($"Runtime: {RuntimeInformation.FrameworkDescription} on {RuntimeInformation.OSDescription} ({RuntimeInformation.ProcessArchitecture}), {Environment.ProcessorCount} cores");
            sb.AppendLine($"Method: {WarmupReps} warmup + {Reps} reps per class, median reported; engine = StepLexer single-rule greedy, reference = Regex(Compiled).Matches.");
            sb.AppendLine();
            sb.AppendLine("Phase 1 = raw lexical scan (greedy NFA match per position); Step = full Initialize+Step loop. Overhead is vs .NET Regex median.");
            sb.AppendLine();
            sb.AppendLine("| Pattern class | Input chars | Phase1 (ms) | Step (ms) | .NET (ms) | Phase1 overhead | Step overhead | Phase1 alloc (B) | Step alloc (B) | .NET alloc (B) |");
            sb.AppendLine("|---|---|---|---|---|---|---|---|---|---|");
            foreach (var r in rows)
            {
                sb.AppendLine($"| {r.Class} | {r.InputChars} | {r.Phase1MedianMs:F3} | {r.StepMedianMs:F3} | {r.DotNetMedianMs:F3} | {r.Phase1OverheadPct:+0.0;-0.0}% | {r.StepOverheadPct:+0.0;-0.0}% | {r.Phase1AllocBytes} | {r.StepAllocBytes} | {r.DotNetAllocBytes} |");
            }
            sb.AppendLine();
            sb.AppendLine("Match counts (engine vs .NET) are equal per class; see issue-104-summary.json for the raw data.");
            File.WriteAllText(path, sb.ToString());
        }
    }
}
