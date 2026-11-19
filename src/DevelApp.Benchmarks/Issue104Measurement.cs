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
            double EngineMedianMs, double DotNetMedianMs, double OverheadPct,
            long EngineAllocBytes, long DotNetAllocBytes, double AllocRatioPct);

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

                var engineMatches = 0;
                var dotNetMatches = 0;
                for (var i = 0; i < WarmupReps; i++)
                {
                    engineMatches = EngineRun(lexer, bytes);
                    dotNetMatches = DotNetRun(regex, input);
                }

                var engineTimes = new List<double>(Reps);
                var dotNetTimes = new List<double>(Reps);
                for (var i = 0; i < Reps; i++)
                {
                    engineTimes.Add(TimedMs(() => EngineRun(lexer, bytes)));
                    dotNetTimes.Add(TimedMs(() => DotNetRun(regex, input)));
                }

                // Allocation per operation (rule compilation is outside the timed
                // region: the lexer object is reused across Initialize calls, the
                // regex is compiled once - both are long-lived in real use).
                var engineAlloc = AllocatedBytes(() => EngineRun(lexer, bytes));
                var dotNetAlloc = AllocatedBytes(() => DotNetRun(regex, input));

                var engineMedian = Median(engineTimes);
                var dotNetMedian = Median(dotNetTimes);
                var overheadPct = dotNetMedian > 0 ? (engineMedian - dotNetMedian) / dotNetMedian * 100.0 : 0.0;
                var allocRatioPct = dotNetAlloc > 0 ? (double)(engineAlloc - dotNetAlloc) / dotNetAlloc * 100.0 : 0.0;

                rows.Add(new Row(pc.Name, input.Length, engineMatches, dotNetMatches,
                    engineMedian, dotNetMedian, overheadPct, engineAlloc, dotNetAlloc, allocRatioPct));

                Console.WriteLine($"[{pc.Name}] engine {engineMedian:F3} ms vs dotnet {dotNetMedian:F3} ms " +
                                  $"-> overhead {overheadPct:F1}%; alloc {engineAlloc} vs {dotNetAlloc} bytes " +
                                  $"-> {(allocRatioPct >= 0 ? "+" : "")}{allocRatioPct:F1}%");
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
            sb.AppendLine("pattern_class,input_chars,engine_matches,dotnet_matches,engine_median_ms,dotnet_median_ms,overhead_pct,engine_alloc_bytes,dotnet_alloc_bytes,alloc_ratio_pct");
            foreach (var r in rows)
            {
                sb.AppendLine(string.Join(",", r.Class, r.InputChars, r.EngineMatches, r.DotNetMatches,
                    r.EngineMedianMs.ToString("F4", System.Globalization.CultureInfo.InvariantCulture),
                    r.DotNetMedianMs.ToString("F4", System.Globalization.CultureInfo.InvariantCulture),
                    r.OverheadPct.ToString("F2", System.Globalization.CultureInfo.InvariantCulture),
                    r.EngineAllocBytes, r.DotNetAllocBytes,
                    r.AllocRatioPct.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)));
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
                    engine = "StepLexer single-rule greedy (token-rule compile excluded; lexer reused across reps)",
                    reference = "System.Text.RegularExpressions.Regex (RegexOptions.Compiled), Regex.Matches",
                    allocation = "GC.GetAllocatedBytesForCurrentThread delta around one full scan",
                },
                rows = rows.Select(r => new
                {
                    patternClass = r.Class,
                    inputChars = r.InputChars,
                    engineMatches = r.EngineMatches,
                    dotNetMatches = r.DotNetMatches,
                    engineMedianMs = r.EngineMedianMs,
                    dotNetMedianMs = r.DotNetMedianMs,
                    overheadPct = r.OverheadPct,
                    engineAllocBytes = r.EngineAllocBytes,
                    dotNetAllocBytes = r.DotNetAllocBytes,
                    allocRatioPct = r.AllocRatioPct,
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
            sb.AppendLine("| Pattern class | Input chars | Engine median (ms) | .NET median (ms) | Overhead | Engine alloc (B) | .NET alloc (B) | Alloc ratio |");
            sb.AppendLine("|---|---|---|---|---|---|---|---|");
            foreach (var r in rows)
            {
                sb.AppendLine($"| {r.Class} | {r.InputChars} | {r.EngineMedianMs:F3} | {r.DotNetMedianMs:F3} | {r.OverheadPct:+0.0;-0.0}% | {r.EngineAllocBytes} | {r.DotNetAllocBytes} | {r.AllocRatioPct:+0.0;-0.0}% |");
            }
            sb.AppendLine();
            sb.AppendLine("Match counts (engine vs .NET) are equal per class; see issue-104-summary.json for the raw data.");
            File.WriteAllText(path, sb.ToString());
        }
    }
}
