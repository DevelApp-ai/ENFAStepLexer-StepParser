using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using DevelApp.StepParser;
using DevelApp.StepLexer;
using Xunit;
using Xunit.Abstractions;

namespace DevelApp.StepParser.Tests
{
    /// <summary>
    /// ML baseline measurement harness (issue #58, sub-issue #73).
    ///
    /// Establishes the pre-ML performance baseline for parsing: latency
    /// percentiles (p50/p95/p99) and allocation profile per (grammar, input)
    /// pair, so that the ML-assisted prototypes (sub-issues #74/#75) can be
    /// evaluated against a reproducible "today" measurement instead of ad-hoc
    /// numbers.
    ///
    /// The corpus run is env-gated and skipped by default (CI included):
    /// set ML_TRACE_OUT (output directory) and MINOTAUR_GRAMMARS_DIR
    /// (grammar repository checkout), e.g. from the repository root:
    ///
    ///   MINOTAUR_GRAMMARS_DIR=/path/to/Minotaur-Grammars \
    ///   ML_TRACE_OUT=/tmp/ml-baseline dotnet test --filter MlBaseline
    ///
    /// Output: one JSON object per line ("ml-baseline/1") in
    /// ml-baseline.jsonl. Timing fields are inherently machine-dependent and
    /// meant for relative, same-machine comparisons; the environment
    /// (runtime, OS, CPU count) is recorded alongside for provenance.
    /// </summary>
    public class MlBaselineMeasurementTests
    {
        private const int WarmupRuns = 2;
        private const int MeasuredRuns = 30;

        private readonly ITestOutputHelper _output;

        public MlBaselineMeasurementTests(ITestOutputHelper output)
        {
            _output = output;
        }

        [Fact]
        public void MlBaseline_RecordsCorpusBaselineForGrammarRepositoryExamples()
        {
            var outDir = Environment.GetEnvironmentVariable("ML_TRACE_OUT");
            var grammarsDir = Environment.GetEnvironmentVariable("MINOTAUR_GRAMMARS_DIR");
            if (string.IsNullOrWhiteSpace(outDir) || string.IsNullOrWhiteSpace(grammarsDir))
            {
                _output.WriteLine(
                    "ML_TRACE_OUT or MINOTAUR_GRAMMARS_DIR is not set - skipping " +
                    "ML corpus baseline capture (offline/dev mode).");
                return;
            }

            grammarsDir = Path.GetFullPath(grammarsDir);
            outDir = Path.GetFullPath(outDir);
            Directory.CreateDirectory(outDir);

            var examples = Directory.EnumerateFiles(
                grammarsDir, "*Examples.txt", SearchOption.AllDirectories).OrderBy(p => p).ToList();
            Assert.True(
                examples.Count > 0,
                $"No *Examples.txt files found under {grammarsDir} - nothing to baseline.");

            var baselinePath = Path.Combine(outDir, "ml-baseline.jsonl");
            var recorded = 0;
            using (var writer = new StreamWriter(baselinePath, append: false))
            {
                foreach (var examplePath in examples)
                {
                    var grammarPath = ResolveGrammarForExample(examplePath);
                    if (grammarPath == null)
                    {
                        _output.WriteLine($"  SKIP     {Path.GetFileName(examplePath)} (no sibling grammar)");
                        continue;
                    }

                    var record = MeasureBaseline(
                        grammarContent: File.ReadAllText(grammarPath),
                        grammarDisplayName: Path.GetFileName(grammarPath),
                        inputContent: File.ReadAllText(examplePath),
                        inputDisplayName: Path.GetFileName(examplePath));
                    writer.WriteLine(JsonSerializer.Serialize(record));
                    recorded++;
                    _output.WriteLine(
                        $"  RECORDED {record.GetProperty("grammarName").GetString()} + " +
                        $"{record.GetProperty("inputName").GetString()} " +
                        $"(p50={record.GetProperty("parseTimeP50Micros").GetDouble():F0}us, " +
                        $"allocP50={record.GetProperty("allocatedBytesP50").GetDouble():F0})");
                }
            }

            _output.WriteLine($"Baseline written to {baselinePath} ({recorded} record(s)).");
            Assert.True(recorded > 0, "No (grammar, example) pairs could be baselined.");
        }

        [Fact]
        public void MlBaseline_HarnessMeasuresKnownGrammarParse()
        {
            // Always-on smoke test of the measurement harness itself: one
            // ambiguous expression grammar, one input, small run count.
            // Keeps percentile/allocation math CI-covered without the full
            // corpus run.
            var grammar = @"
Grammar: BaselineSmoke
<NUMBER> ::= /[0-9]+/
<PLUS> ::= '+'
<expr> ::= <expr> '+' <expr>
<expr> ::= <NUMBER>
<start> ::= <expr>
";
            var input = "1+2+3+4+5+6+7+8";

            var record = MeasureBaseline(
                grammarContent: grammar,
                grammarDisplayName: "BaselineSmoke",
                inputContent: input,
                inputDisplayName: "smoke",
                measuredRuns: 10);

            // Schema and identity
            Assert.Equal("ml-baseline/1", record.GetProperty("schema").GetString());
            Assert.Equal("BaselineSmoke", record.GetProperty("grammarName").GetString());
            Assert.Equal("smoke", record.GetProperty("inputName").GetString());

            // Latency percentiles are present and ordered
            var p50 = record.GetProperty("parseTimeP50Micros").GetDouble();
            var p95 = record.GetProperty("parseTimeP95Micros").GetDouble();
            var p99 = record.GetProperty("parseTimeP99Micros").GetDouble();
            Assert.True(p50 > 0, "p50 must be a positive duration");
            Assert.True(p50 <= p95 && p95 <= p99, $"Percentiles out of order: p50={p50} p95={p95} p99={p99}");

            // Allocation profile is present and non-negative
            Assert.True(record.GetProperty("allocatedBytesP50").GetDouble() >= 0);
            Assert.True(record.GetProperty("allocatedBytesP99").GetDouble() >= 0);
            Assert.Equal(10, record.GetProperty("measuredRuns").GetInt32());

            // Parse observability for cross-checking with the trace harness
            Assert.True(record.GetProperty("success").GetBoolean());
            Assert.True(record.GetProperty("tokenCount").GetInt32() > 0);
        }

        // --- measurement core ---

        /// <summary>
        /// Parse (grammar, input) repeatedly and summarize latency
        /// percentiles and allocated bytes. Uses a fresh engine per run
        /// (mirrors real one-shot parse usage) with warm-up runs excluded.
        /// </summary>
        private static JsonElement MeasureBaseline(
            string grammarContent,
            string grammarDisplayName,
            string inputContent,
            string inputDisplayName,
            int measuredRuns = MeasuredRuns)
        {
            var samples = new List<(double parseMicros, double allocatedBytes)>();

            string grammarName = grammarDisplayName;
            int tokenCount = 0, pathCount = 0, ambiguousParseCount = 0, errorCount = 0;
            var success = false;

            for (var run = -WarmupRuns; run < measuredRuns; run++)
            {
                using var engine = new StepParserEngine();
                engine.LoadGrammarFromContent(grammarContent, grammarDisplayName);

                long allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
                var sw = Stopwatch.StartNew();

                var result = engine.Parse(inputContent, inputDisplayName);
                sw.Stop();
                long allocated = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;

                if (run < 0)
                {
                    // Warm-up: JIT/lazy-init outside the measured samples.
                    continue;
                }

                grammarName = engine.CurrentGrammar?.Name ?? grammarDisplayName;
                tokenCount = result.Tokens?.Count ?? 0;
                pathCount = result.PathCount;
                ambiguousParseCount = result.AmbiguousParses?.Count ?? 0;
                errorCount = result.Errors?.Count ?? 0;
                success = result.Success;
                samples.Add((sw.Elapsed.TotalMicroseconds, allocated));
            }

            var latencies = samples.Select(s => s.parseMicros).OrderBy(v => v).ToList();
            var allocations = samples.Select(s => s.allocatedBytes).OrderBy(v => v).ToList();

            using var doc = JsonSerializer.SerializeToDocument(new Dictionary<string, object?>
            {
                ["schema"] = "ml-baseline/1",
                ["grammarName"] = grammarName,
                ["inputName"] = inputDisplayName,
                ["inputLengthBytes"] = inputContent.Length,
                ["measuredRuns"] = measuredRuns,
                ["success"] = success,
                ["errorCount"] = errorCount,
                ["tokenCount"] = tokenCount,
                ["pathCount"] = pathCount,
                ["ambiguousParseCount"] = ambiguousParseCount,
                ["parseTimeP50Micros"] = Percentile(latencies, 0.50),
                ["parseTimeP95Micros"] = Percentile(latencies, 0.95),
                ["parseTimeP99Micros"] = Percentile(latencies, 0.99),
                ["parseTimeMinMicros"] = latencies[0],
                ["allocatedBytesP50"] = Percentile(allocations, 0.50),
                ["allocatedBytesP95"] = Percentile(allocations, 0.95),
                ["allocatedBytesP99"] = Percentile(allocations, 0.99),
                ["allocatedBytesMin"] = allocations[0],
                ["runtimeVersion"] = Environment.Version.ToString(),
                ["processorCount"] = Environment.ProcessorCount,
                ["osDescription"] = Environment.OSVersion.ToString(),
            });
            return doc.RootElement.Clone();
        }

        /// <summary>Nearest-rank percentile of a pre-sorted list.</summary>
        private static double Percentile(List<double> sorted, double q)
        {
            if (sorted.Count == 0)
            {
                throw new InvalidOperationException("No samples to compute a percentile from.");
            }

            var rank = Math.Clamp((int)Math.Ceiling(q * sorted.Count), 1, sorted.Count);
            return sorted[rank - 1];
        }

        /// <summary>
        /// Finds the grammar file belonging to an *Examples.txt sidecar:
        /// the folder's minotaur-metadata.json MainFile when present, else
        /// the first .grammar file in the same folder. Mirrors the trace
        /// harness (MlCorpusTraceTests).
        /// </summary>
        private static string? ResolveGrammarForExample(string examplePath)
        {
            var folder = Path.GetDirectoryName(examplePath)!;
            var metadataPath = Path.Combine(folder, "minotaur-metadata.json");
            if (File.Exists(metadataPath))
            {
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(metadataPath));
                    if (doc.RootElement.TryGetProperty("MainFile", out var mainFile))
                    {
                        var candidate = Path.Combine(folder, mainFile.GetString()!);
                        if (File.Exists(candidate))
                        {
                            return candidate;
                        }
                    }
                }
                catch (JsonException)
                {
                    // Fall through to the .grammar probe below.
                }
            }

            return Directory.EnumerateFiles(folder, "*.grammar").FirstOrDefault();
        }
    }
}
