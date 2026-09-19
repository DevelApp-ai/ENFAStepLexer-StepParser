using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using DevelApp.StepParser;
using DevelApp.StepLexer;
using Xunit;
using Xunit.Abstractions;

namespace DevelApp.StepParser.Tests
{
    /// <summary>
    /// ML performance corpus/trace harness (issue #58, plan item 1).
    ///
    /// Captures (grammar, input, outcome, timing) traces for the grammar
    /// repository's example corpora so that ML-assisted optimizations
    /// (learned rule prioritization, GLR path pruning, strategy selection)
    /// can be trained and evaluated offline. The records are pure
    /// observations of the current deterministic engine - they influence
    /// nothing at runtime.
    ///
    /// The corpus test is env-gated and skipped by default (CI included):
    /// set ML_TRACE_OUT (output directory for the JSONL trace) and
    /// MINOTAUR_GRAMMARS_DIR (grammar repository checkout) to run it, e.g.
    /// from the repository root:
    ///
    ///   MINOTAUR_GRAMMARS_DIR=/path/to/Minotaur-Grammars \
    ///   ML_TRACE_OUT=/tmp/ml-trace dotnet test --filter MlCorpusTrace
    ///
    /// Trace format: one JSON object per line ("ml-trace/1"), fields:
    ///   grammarPath, grammarName, tokenRuleCount, productionRuleCount,
    ///   avgProductionRhsLength, importCount, inheritable,
    ///   examplePath, inputLength, inputLineCount,
    ///   success, errorCount, diagnosticCount, tokenCount, pathCount,
    ///   ambiguousParseCount, parseTimeMicros, wallClockMicros, exception
    ///
    /// No timestamps or machine-specific values are recorded so corpora are
    /// reproducible (same checkout + engine version = comparable traces;
    /// timing fields are inherently machine-dependent and meant for
    /// relative, same-run comparisons only).
    /// </summary>
    public class MlCorpusTraceTests
    {
        private readonly ITestOutputHelper _output;

        public MlCorpusTraceTests(ITestOutputHelper output)
        {
            _output = output;
        }

        [Fact]
        public void MlTrace_RecordsCorpusForGrammarRepositoryExamples()
        {
            var outDir = Environment.GetEnvironmentVariable("ML_TRACE_OUT");
            var grammarsDir = Environment.GetEnvironmentVariable("MINOTAUR_GRAMMARS_DIR");
            if (string.IsNullOrWhiteSpace(outDir) || string.IsNullOrWhiteSpace(grammarsDir))
            {
                _output.WriteLine(
                    "ML_TRACE_OUT or MINOTAUR_GRAMMARS_DIR is not set - skipping " +
                    "ML corpus trace capture (offline/dev mode).");
                return;
            }

            grammarsDir = Path.GetFullPath(grammarsDir);
            outDir = Path.GetFullPath(outDir);
            Directory.CreateDirectory(outDir);

            var examples = Directory.EnumerateFiles(
                grammarsDir, "*_Examples.txt", SearchOption.AllDirectories).OrderBy(p => p).ToList();
            Assert.True(
                examples.Count > 0,
                $"No *_Examples.txt files found under {grammarsDir} - nothing to trace.");

            var tracePath = Path.Combine(outDir, "ml-corpus-trace.jsonl");
            var recorded = 0;
            using (var writer = new StreamWriter(tracePath, append: false))
            {
                foreach (var examplePath in examples)
                {
                    var grammarPath = ResolveGrammarForExample(examplePath);
                    if (grammarPath == null)
                    {
                        _output.WriteLine($"  SKIP     {Relative(grammarsDir, examplePath)} (no sibling grammar)");
                        continue;
                    }

                    var record = RecordTrace(grammarsDir, grammarPath, examplePath);
                    writer.WriteLine(JsonSerializer.Serialize(record));
                    recorded++;
                    _output.WriteLine(
                        $"  RECORDED {record.GetProperty("grammarPath").GetString()} + " +
                        $"{record.GetProperty("examplePath").GetString()} " +
                        $"(success={record.GetProperty("success").GetBoolean()})");
                }
            }

            _output.WriteLine($"Trace written to {tracePath} ({recorded} record(s)).");
            Assert.True(recorded > 0, "No (grammar, example) pairs could be traced.");
        }

        [Fact]
        public void MlTrace_HarnessRecordsKnownGrammarParse()
        {
            // Always-on smoke test of the recorder itself: one known grammar,
            // one input, a temp output file. Keeps the harness CI-covered
            // without requiring the full corpus run.
            var grammar = TestGrammars.Get(
                "test-grammars/step-parser-tests/StepParserTests/SimpleExpr.grammar");
            var input = "1+2*3";

            var record = RecordTrace(
                grammarsRoot: null, grammarPath: null, examplePath: null,
                grammarContent: grammar, grammarDisplayName: "SimpleExpr",
                inputContent: input, inputDisplayName: "smoke");

            Assert.Equal("ml-trace/1", record.GetProperty("schema").GetString());
            Assert.Equal("SimpleExpr", record.GetProperty("grammarName").GetString());
            Assert.True(record.GetProperty("tokenRuleCount").GetInt32() > 0);
            Assert.True(record.GetProperty("productionRuleCount").GetInt32() > 0);
            Assert.Equal(5, record.GetProperty("inputLength").GetInt32());
            Assert.True(record.GetProperty("success").GetBoolean());
            Assert.True(record.GetProperty("tokenCount").GetInt32() > 0);
            Assert.True(record.GetProperty("parseTimeMicros").GetDouble() >= 0);
            Assert.True(record.GetProperty("wallClockMicros").GetDouble() > 0);
            Assert.Equal(string.Empty, record.GetProperty("exception").GetString());
        }

        // --- recorder ---

        private static JsonElement RecordTrace(
            string? grammarsRoot, string? grammarPath, string? examplePath,
            string? grammarContent = null, string? grammarDisplayName = null,
            string? inputContent = null, string? inputDisplayName = null)
        {
            grammarContent ??= File.ReadAllText(grammarPath!);
            inputContent ??= File.ReadAllText(examplePath!);

            string success = "false", exception = "";
            int errorCount = 0, diagnosticCount = 0, tokenCount = 0, pathCount = 0, ambiguousParseCount = 0;
            double parseTimeMicros = 0;
            var grammarStats = new GrammarStats();

            var sw = Stopwatch.StartNew();
            try
            {
                using var engine = new StepParserEngine();
                engine.LoadGrammarFromContent(grammarContent, Path.GetFileName(grammarPath ?? "grammar.grammar"));
                grammarStats = GrammarStats.From(engine.CurrentGrammar);

                var result = engine.Parse(inputContent, Path.GetFileName(examplePath ?? "input.txt"));
                success = result.Success ? "true" : "false";
                errorCount = result.Errors?.Count ?? 0;
                diagnosticCount = result.Diagnostics?.Count ?? 0;
                tokenCount = result.Tokens?.Count ?? 0;
                pathCount = result.PathCount;
                ambiguousParseCount = result.AmbiguousParses?.Count ?? 0;
                parseTimeMicros = result.ParseTime.TotalMicroseconds;
            }
            catch (Exception ex)
            {
                exception = ex.Message;
            }
            sw.Stop();

            using var doc = JsonSerializer.SerializeToDocument(new Dictionary<string, object?>
            {
                ["schema"] = "ml-trace/1",
                ["grammarPath"] = grammarPath != null && grammarsRoot != null
                    ? Relative(grammarsRoot, grammarPath) : grammarDisplayName,
                ["grammarName"] = grammarStats.Name ?? grammarDisplayName,
                ["tokenRuleCount"] = grammarStats.TokenRuleCount,
                ["productionRuleCount"] = grammarStats.ProductionRuleCount,
                ["avgProductionRhsLength"] = grammarStats.AvgProductionRhsLength,
                ["importCount"] = grammarStats.ImportCount,
                ["inheritable"] = grammarStats.Inheritable,
                ["examplePath"] = examplePath != null && grammarsRoot != null
                    ? Relative(grammarsRoot, examplePath) : inputDisplayName,
                ["inputLength"] = inputContent.Length,
                ["inputLineCount"] = inputContent.Count(ch => ch == '\n') + 1,
                ["success"] = success == "true",
                ["errorCount"] = errorCount,
                ["diagnosticCount"] = diagnosticCount,
                ["tokenCount"] = tokenCount,
                ["pathCount"] = pathCount,
                ["ambiguousParseCount"] = ambiguousParseCount,
                ["parseTimeMicros"] = parseTimeMicros,
                ["wallClockMicros"] = sw.Elapsed.TotalMicroseconds,
                ["exception"] = exception,
            });
            return doc.RootElement.Clone();
        }

        private readonly struct GrammarStats
        {
            public string? Name { get; init; }
            public int TokenRuleCount { get; init; }
            public int ProductionRuleCount { get; init; }
            public double AvgProductionRhsLength { get; init; }
            public int ImportCount { get; init; }
            public bool Inheritable { get; init; }

            public static GrammarStats From(GrammarDefinition? grammar)
            {
                if (grammar == null)
                {
                    return new GrammarStats();
                }

                var productions = grammar.ProductionRules ?? new List<ProductionRule>();
                var rhsCount = productions.Count;
                var rhsTotal = productions.Sum(p => p.RightHandSide?.Count ?? 0);
                return new GrammarStats
                {
                    Name = grammar.Name,
                    TokenRuleCount = grammar.TokenRules?.Count ?? 0,
                    ProductionRuleCount = productions.Count,
                    AvgProductionRhsLength = rhsCount == 0 ? 0 : (double)rhsTotal / rhsCount,
                    ImportCount = grammar.Imports?.Count ?? 0,
                    Inheritable = grammar.IsInheritable,
                };
            }
        }

        /// <summary>
        /// Finds the grammar file belonging to an *_Examples.txt sidecar:
        /// the folder's minotaur-metadata.json MainFile when present, else
        /// the first .grammar file in the same folder.
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
                    if (doc.RootElement.TryGetProperty("MainFile", out var mainFile)
                        && mainFile.ValueKind == JsonValueKind.String)
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
                    // Fall through to the first .grammar file.
                }
            }

            return Directory.EnumerateFiles(folder, "*.grammar").FirstOrDefault();
        }

        private static string Relative(string root, string path)
            => Path.GetRelativePath(root, path).Replace('\\', '/');
    }
}
