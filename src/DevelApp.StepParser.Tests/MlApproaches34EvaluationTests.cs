using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using DevelApp.StepParser;
using Xunit;
using Xunit.Abstractions;

namespace DevelApp.StepParser.Tests
{
    /// <summary>
    /// Evaluation harness for the issue #76 sub-issue of #58 (plan item 5,
    /// candidate approaches 3 and 4): hotspot-guided strategy selection and
    /// learned token-reuse prediction.
    ///
    /// - <see cref="Evaluate_CorpusWritesAnalyticsAndParseTimeCsv"/> is the
    ///   env-gated corpus run (ML_EVAL_OUT + MINOTAUR_GRAMMARS_DIR): it
    ///   writes one CSV row per (grammar, example) with the
    ///   CognitiveGraphAnalytics hotspot metrics next to the parse
    ///   observables, so the correlation between hotspot metrics and parse
    ///   cost can be computed — the evidence base for the approach-3
    ///   go/no-go in docs/ML-Assist-Evaluation-Approaches-3-4.md.
    /// - The always-on tests cover the approach-4 instrumentation
    ///   (<see cref="RealTimeParserSession.LastReuseRatio"/>): the
    ///   deterministic tail reuse baseline that a learned blast-radius
    ///   prediction would have to beat.
    /// </summary>
    public class MlApproaches34EvaluationTests
    {
        private readonly ITestOutputHelper _output;

        public MlApproaches34EvaluationTests(ITestOutputHelper output)
        {
            _output = output;
        }

        private const string Grammar = @"
Grammar: ReuseEval
<IDENT> ::= /[a-zA-Z][a-zA-Z0-9]*/
<NUMBER> ::= /[0-9]+/
<SEMI> ::= ';'
<item> ::= <IDENT>
<item> ::= <NUMBER>
<start> ::= <item> (';' <item>)*
";

        // ------------------------------------------------------------------
        // Approach 4: deterministic tail-reuse baseline instrumentation
        // ------------------------------------------------------------------

        [Fact]
        public void ReuseRatio_MeasuredAfterMidFileEdit()
        {
            var session = new RealTimeParserSession(Grammar, "eval.txt");
            var source = string.Join(";", Enumerable.Range(0, 200).Select(i => i % 2 == 0 ? $"name{i}" : $"{i}"));
            session.Initialize(source);

            // Edit near the front: most of the tail must be reused.
            session.ApplyEdit(0, 1, "x");

            Assert.True(session.LastReusedTokenCount > 0,
                "the aligned tail reuse must be observable (issue #76 instrumentation)");
            Assert.InRange(session.LastReuseRatio, 0.0, 1.0);
            _output.WriteLine($"reuse: {session.LastReusedTokenCount} tokens, ratio {session.LastReuseRatio:0.00}");
        }

        [Fact]
        public void ReuseRatio_HighForPlainTypingEdit()
        {
            var session = new RealTimeParserSession(Grammar, "eval.txt");
            session.Initialize("alpha;123;beta;456;gamma;789");

            // Typing-style edit at the very end of text: the last token
            // must be re-lexed (it may absorb the inserted character), so
            // the reuse ratio is 0 for that single-token tail - the blast
            // radius of the edit, measured.
            session.ApplyEdit(session.Text.Length, 0, "9");
            Assert.Equal(0, session.LastReusedTokenCount);
            Assert.Equal(0.0, session.LastReuseRatio);

            // A mid-text single-character insertion: the tail from the
            // next token onward aligns by construction, so reuse is high.
            session.ApplyEdit("alpha;123;".Length, 0, "4");
            Assert.True(session.LastReuseRatio > 0.5,
                $"plain typing edit should reuse most of the tail (got {session.LastReuseRatio:0.00})");
        }

        // ------------------------------------------------------------------
        // Approach 3: corpus evaluation run (env-gated, offline/dev mode)
        // ------------------------------------------------------------------

        [Fact]
        public void Evaluate_CorpusWritesAnalyticsAndParseTimeCsv()
        {
            var outDir = Environment.GetEnvironmentVariable("ML_EVAL_OUT");
            var grammarsDir = Environment.GetEnvironmentVariable("MINOTAUR_GRAMMARS_DIR");
            if (string.IsNullOrWhiteSpace(outDir) || string.IsNullOrWhiteSpace(grammarsDir))
            {
                _output.WriteLine(
                    "ML_EVAL_OUT or MINOTAUR_GRAMMARS_DIR is not set - skipping " +
                    "approaches-3/4 corpus evaluation run (offline/dev mode).");
                return;
            }

            grammarsDir = Path.GetFullPath(grammarsDir);
            outDir = Path.GetFullPath(outDir);
            Directory.CreateDirectory(outDir);

            var examples = Directory.EnumerateFiles(
                grammarsDir, "*Examples.txt", SearchOption.AllDirectories).OrderBy(p => p).ToList();
            Assert.True(examples.Count > 0, $"No *Examples.txt files found under {grammarsDir}.");

            var csvPath = Path.Combine(outDir, "ml-approaches34-eval.csv");
            var recorded = 0;
            using (var writer = new StreamWriter(csvPath, append: false))
            {
                writer.WriteLine("grammar,example,inputLength,success,parseTimeMicros,pathCount," +
                    "ambiguousParses,nodeCount,edgeCount,maxDepth,averageFanout,maxFanout," +
                    "ambiguityRate,nodesPerKb,complexityScore");

                foreach (var examplePath in examples)
                {
                    var grammarPath = ResolveGrammarForExample(examplePath);
                    if (grammarPath == null)
                    {
                        continue;
                    }

                    var row = EvaluateExample(grammarPath, examplePath);
                    if (row == null)
                    {
                        continue;
                    }

                    writer.WriteLine(string.Join(",", row));
                    recorded++;
                    _output.WriteLine($"  RECORDED {Path.GetFileName(grammarPath)} + {Path.GetFileName(examplePath)}");
                }
            }

            _output.WriteLine($"Evaluation CSV written to {csvPath} ({recorded} record(s)).");
            Assert.True(recorded > 0, "No (grammar, example) pairs could be evaluated.");
        }

        [Fact]
        public void Evaluate_HarnessRecordsKnownGrammarAnalytics()
        {
            // Always-on smoke test of the evaluation recorder itself: one
            // known grammar, one input, analytics attached.
            var row = EvaluateExampleFromContent("SimpleExprInline", "1+2*3", @"
Grammar: SimpleExprInline
<NUMBER> ::= /[0-9]+/
<PLUS> ::= '+'
<MUL> ::= '*'
<expr> ::= <expr> '+' <expr>
<expr> ::= <expr> '*' <expr>
<expr> ::= <NUMBER>
<start> ::= <expr>
");

            Assert.NotNull(row);
            Assert.Equal("5", row![2]); // inputLength for "1+2*3"
            Assert.Equal("1", row[3]); // success
            Assert.True(double.Parse(row[4], CultureInfo.InvariantCulture) >= 0); // parseTimeMicros
            Assert.True(int.Parse(row[5], CultureInfo.InvariantCulture) > 0); // pathCount
            // Analytics columns (complexityScore is the last).
            var complexity = double.Parse(row[^1], CultureInfo.InvariantCulture);
            Assert.True(complexity >= 0);
        }

        // --- harness ---

        private static string[]? EvaluateExample(string grammarPath, string examplePath)
        {
            return EvaluateExampleFromContent(
                Path.GetFileNameWithoutExtension(grammarPath),
                File.ReadAllText(examplePath),
                File.ReadAllText(grammarPath));
        }

        private static string[]? EvaluateExampleFromContent(
            string grammarName, string input, string grammarContent)
        {
            try
            {
                using var engine = new StepParserEngine();
                engine.LoadGrammarFromContent(grammarContent, grammarName + ".grammar");
                var result = engine.Parse(input, "example.txt");

                var analytics = result.Success
                    ? CognitiveGraphAnalytics.Analyze(result.CognitiveGraph)
                    : null;

                return new[]
                {
                    Quote(grammarName),
                    Quote("example"),
                    input.Length.ToString(CultureInfo.InvariantCulture),
                    result.Success ? "1" : "0",
                    Math.Round(result.ParseTime.TotalMicroseconds, 3).ToString(CultureInfo.InvariantCulture),
                    result.PathCount.ToString(CultureInfo.InvariantCulture),
                    (result.AmbiguousParses?.Count ?? 0).ToString(CultureInfo.InvariantCulture),
                    (analytics?.NodeCount ?? 0).ToString(CultureInfo.InvariantCulture),
                    (analytics?.EdgeCount ?? 0).ToString(CultureInfo.InvariantCulture),
                    (analytics?.MaxDepth ?? 0).ToString(CultureInfo.InvariantCulture),
                    analytics == null ? "0" : analytics.AverageFanout.ToString("0.###", CultureInfo.InvariantCulture),
                    (analytics?.MaxFanout ?? 0).ToString(CultureInfo.InvariantCulture),
                    analytics == null ? "0" : analytics.AmbiguityRate.ToString("0.###", CultureInfo.InvariantCulture),
                    analytics == null ? "0" : analytics.NodesPerKb.ToString("0.###", CultureInfo.InvariantCulture),
                    analytics == null ? "0" : analytics.ComplexityScore.ToString("0.###", CultureInfo.InvariantCulture),
                };
            }
            catch (Exception ex)
            {
                return new[]
                {
                    Quote(grammarName),
                    Quote("example"),
                    input.Length.ToString(CultureInfo.InvariantCulture),
                    "0", "0", "0", "0", "0", "0", "0", "0", "0", "0", "0", "0",
                    Quote("error: " + ex.Message)
                };
            }
        }

        private static string Quote(string value) =>
            "\"" + value.Replace("\"", "\"\"") + "\"";

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
    }
}
