using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using DevelApp.StepLexer;
using DevelApp.StepParser;
using Xunit;
using Xunit.Abstractions;

namespace DevelApp.StepParser.Tests
{
    /// <summary>
    /// ML training-label harness (issue #93, part of the #58 plan tracked
    /// by #105): captures labeled observations for the two ML-assist
    /// prototypes.
    ///
    /// Rule labels (schema ml-label-rule/1): one record per lexer
    /// rule-evaluation attempt — the issue #74 prioritizer feature vector
    /// for the (rule, next input byte) pair plus whether the attempt
    /// matched.
    ///
    /// Path labels (schema ml-label-path/1): one record per (parser step,
    /// path) while the live GLR path count exceeds the deterministic path
    /// budget — the issue #75 pruner feature vector keyed by path id, with
    /// succeeded marking whether that path ultimately completed the parse.
    ///
    /// The corpus test is env-gated and skipped by default (CI included):
    /// set ML_TRACE_OUT (output directory) and MINOTAUR_GRAMMARS_DIR
    /// (grammar repository checkout) to run it, e.g. from the repo root:
    ///
    ///   MINOTAUR_GRAMMARS_DIR=/path/to/Minotaur-Grammars \
    ///   ML_TRACE_OUT=/tmp/ml-labels dotnet test --filter MlLabel
    ///
    /// The capture itself is observation-only and thread-local: attaching
    /// a capture never changes lexing or parsing behavior (the always-on
    /// smoke tests below pin that the wiring works in CI). Both buffers are
    /// budgeted (DefaultMaxRecords): pathological lexical/path ambiguity
    /// stops recording and latches Overflowed instead of buffering without
    /// bound (issue #93: unbounded buffering OOM-killed capture runs).
    /// </summary>
    public class MlLabelTraceTests
    {
        private readonly ITestOutputHelper _output;

        public MlLabelTraceTests(ITestOutputHelper output)
        {
            _output = output;
        }

        [Fact]
        public void MlLabels_RecordCorpusLabelsForGrammarRepositoryExamples()
        {
            var outDir = Environment.GetEnvironmentVariable("ML_TRACE_OUT");
            var grammarsDir = Environment.GetEnvironmentVariable("MINOTAUR_GRAMMARS_DIR");
            if (string.IsNullOrWhiteSpace(outDir) || string.IsNullOrWhiteSpace(grammarsDir))
            {
                _output.WriteLine(
                    "ML_TRACE_OUT or MINOTAUR_GRAMMARS_DIR is not set - skipping " +
                    "ML label capture (offline/dev mode).");
                return;
            }

            grammarsDir = Path.GetFullPath(grammarsDir);
            outDir = Path.GetFullPath(outDir);
            Directory.CreateDirectory(outDir);

            var examples = Directory.EnumerateFiles(
                grammarsDir, "*Examples.txt", SearchOption.AllDirectories).OrderBy(p => p).ToList();
            Assert.True(
                examples.Count > 0,
                $"No *Examples.txt files found under {grammarsDir} - nothing to label.");

            var ruleLabelPath = Path.Combine(outDir, "ml-rule-labels.jsonl");
            var pathLabelPath = Path.Combine(outDir, "ml-path-labels.jsonl");
            var labeledGrammars = 0;
            long ruleLabelCount = 0;
            long pathLabelCount = 0;

            using (var ruleWriter = new StreamWriter(ruleLabelPath, append: false))
            using (var pathWriter = new StreamWriter(pathLabelPath, append: false))
            {
                foreach (var examplePath in examples)
                {
                    var grammarPath = ResolveGrammarForExample(examplePath);
                    if (grammarPath == null)
                    {
                        _output.WriteLine(
                            $"  SKIP     {Relative(grammarsDir, examplePath)} (no sibling grammar)");
                        continue;
                    }

                    var grammarContent = File.ReadAllText(grammarPath);
                    var inputContent = File.ReadAllText(examplePath);
                    var grammarName = Path.GetFileNameWithoutExtension(grammarPath);

                    var (ruleBuffer, pathCapture) = CaptureOneParse(
                        grammarContent, Path.GetFileName(grammarPath),
                        inputContent, Path.GetFileName(examplePath),
                        out var parseError);

                    if (parseError != null)
                    {
                        _output.WriteLine(
                            $"  SKIP     {Relative(grammarsDir, examplePath)} (parse threw: {parseError})");
                        continue;
                    }

                    if (ruleBuffer.Overflowed || pathCapture.Overflowed)
                    {
                        // Pathological lexical/path ambiguity blew past the
                        // capture budget (issue #93: unbounded buffering
                        // OOM-killed capture runs on such grammars). The
                        // stored prefix is position/step-biased, so the
                        // whole grammar's labels are discarded rather than
                        // trained on.
                        _output.WriteLine(
                            $"  SKIP     {Relative(grammarsDir, examplePath)} " +
                            "(capture budget exceeded - pathological ambiguity; labels discarded)");
                        continue;
                    }

                    foreach (var record in ruleBuffer.Records)
                    {
                        ruleWriter.WriteLine(JsonSerializer.Serialize(
                            new
                            {
                                schema = "ml-label-rule/1",
                                grammarName,
                                features = record.Features,
                                matched = record.Matched
                            }));
                        ruleLabelCount++;
                    }

                    foreach (var record in pathCapture.Records)
                    {
                        pathWriter.WriteLine(JsonSerializer.Serialize(
                            new
                            {
                                schema = "ml-label-path/1",
                                grammarName,
                                step = record.Step,
                                pathId = record.PathId,
                                features = record.Features,
                                succeeded = pathCapture.Succeeded(record.PathId)
                            }));
                        pathLabelCount++;
                    }

                    labeledGrammars++;
                    _output.WriteLine(
                        $"  LABELED  {Relative(grammarsDir, grammarPath)} " +
                        $"({ruleBuffer.Records.Count} rule labels, {pathCapture.Records.Count} path labels)");
                }
            }

            _output.WriteLine(
                $"Labels written to {ruleLabelPath} ({ruleLabelCount}) and " +
                $"{pathLabelPath} ({pathLabelCount}).");
            Assert.True(labeledGrammars > 0, "No (grammar, example) pairs could be labeled.");
            Assert.True(ruleLabelCount > 0, "No rule labels recorded - the capture wiring is broken.");
        }

        [Fact]
        public void MlLabels_CaptureRuleLabelsForKnownGrammar()
        {
            var grammar = TestGrammars.Get(
                "test-grammars/step-parser-tests/StepParserTests/SimpleExpr.grammar");
            var (ruleBuffer, _) = CaptureOneParse(
                grammar, "SimpleExpr.grammar", "1+2*3", "input.txt", out var parseError);

            Assert.Null(parseError);
            Assert.False(ruleBuffer.Overflowed);
            Assert.NotEmpty(ruleBuffer.Records);
            Assert.All(ruleBuffer.Records, r =>
                Assert.Equal(LearnedRulePrioritizer.FeatureCount, r.Features.Length));
            Assert.Contains(ruleBuffer.Records, r => r.Matched);
        }

        [Fact]
        public void MlLabels_CapturePathLabelsForAmbiguousGrammar()
        {
            var (_, pathCapture) = CaptureOneParse(
                AmbiguousExprGrammar, "AmbiguousExpr.grammar", GenerateSum(50), "sum.txt",
                out var parseError);

            Assert.Null(parseError);
            Assert.False(pathCapture.Overflowed);

            var pathRecords = pathCapture.Records;
            Assert.NotEmpty(pathRecords);
            Assert.All(pathRecords, r =>
                Assert.Equal(LearnedPathPruner.FeatureCount, r.Features.Length));
        }

        [Fact]
        public void MlLabels_CaptureBuffersAreThreadLocalAndDetachable()
        {
            Assert.False(RuleMatchLabelCapture.IsCapturing);
            Assert.Null(PathLabelCapture.Current);

            var ruleBuffer = RuleMatchLabelCapture.Begin();
            var pathCapture = PathLabelCapture.Begin();
            try
            {
                Assert.True(RuleMatchLabelCapture.IsCapturing);
                Assert.Same(pathCapture, PathLabelCapture.Current);

                pathCapture.Record(7, new double[LearnedPathPruner.FeatureCount]);
                pathCapture.NextStep();
                pathCapture.MarkSuccessful(new[] { 7 });

                Assert.Single(pathCapture.Records);
                Assert.Equal(0, pathCapture.Records[0].Step);
                Assert.Equal(7, pathCapture.Records[0].PathId);
                Assert.True(pathCapture.Succeeded(7));
                Assert.False(pathCapture.Succeeded(8));

                // A second Begin on the same thread returns a fresh buffer.
                var secondBuffer = RuleMatchLabelCapture.Begin();
                Assert.NotSame(ruleBuffer, secondBuffer);
            }
            finally
            {
                RuleMatchLabelCapture.End();
                PathLabelCapture.End();
            }

            Assert.False(RuleMatchLabelCapture.IsCapturing);
            Assert.Null(PathLabelCapture.Current);
        }

        /// <summary>
        /// The rule-label budget stops recording at MaxRecords and latches
        /// Overflowed, so a pathological lexing run cannot buffer unbounded
        /// labels (issue #93: unbounded buffering OOM-killed capture runs).
        /// </summary>
        [Fact]
        public void MlLabels_RuleBudgetStopsRecordingAndLatchesOverflow()
        {
            var grammar = TestGrammars.Get(
                "test-grammars/step-parser-tests/StepParserTests/SimpleExpr.grammar");

            var ruleBuffer = RuleMatchLabelCapture.Begin(maxRecords: 3);
            using var engine = new StepParserEngine();
            engine.LoadGrammarFromContent(grammar, "SimpleExpr.grammar");
            engine.Parse("1+2*3", "input.txt");
            RuleMatchLabelCapture.End();

            Assert.True(ruleBuffer.Overflowed);
            Assert.Equal(3, ruleBuffer.Records.Count);
        }

        /// <summary>
        /// The path-label budget stops recording at MaxRecords and latches
        /// Overflowed, so a pathological GLR ambiguity cannot buffer
        /// unbounded labels (issue #93).
        /// </summary>
        [Fact]
        public void MlLabels_PathBudgetStopsRecordingAndLatchesOverflow()
        {
            var pathCapture = PathLabelCapture.Begin(maxRecords: 1);
            using var engine = new StepParserEngine();
            engine.LoadGrammarFromContent(AmbiguousExprGrammar, "AmbiguousExpr.grammar");
            engine.Parse(GenerateSum(50), "sum.txt");
            PathLabelCapture.End();

            Assert.True(pathCapture.Overflowed);
            Assert.Single(pathCapture.Records);
        }

        // --- helpers ---

        /// <summary>
        /// Parse one (grammar, input) pair with both captures attached and
        /// return the recorded labels. The captures are always detached,
        /// even when the parse throws (the exception message is returned
        /// instead of propagating so one bad corpus entry cannot abort the
        /// whole capture run).
        /// </summary>
        private static (RuleMatchLabelBuffer ruleBuffer, PathLabelCapture pathCapture)
            CaptureOneParse(
                string grammarContent, string grammarFileName,
                string inputContent, string inputFileName,
                out string? parseError)
        {
            var ruleBuffer = RuleMatchLabelCapture.Begin();
            var pathCapture = PathLabelCapture.Begin();
            parseError = null;
            try
            {
                using var engine = new StepParserEngine();
                engine.LoadGrammarFromContent(grammarContent, grammarFileName);
                engine.Parse(inputContent, inputFileName);
            }
            catch (Exception ex)
            {
                parseError = ex.Message;
            }
            finally
            {
                RuleMatchLabelCapture.End();
                PathLabelCapture.End();
            }

            return (ruleBuffer, pathCapture);
        }

        private const string AmbiguousExprGrammar = @"
Grammar: AmbiguousExpr
<NUMBER> ::= /[0-9]+/
<PLUS> ::= '+'
<expr> ::= <expr> '+' <expr>
<expr> ::= <NUMBER>
<start> ::= <expr>
";

        private static string GenerateSum(int terms)
        {
            var sb = new System.Text.StringBuilder();
            for (var i = 0; i < terms; i++)
            {
                if (i > 0)
                {
                    sb.Append('+');
                }
                sb.Append((i * 7) % 10);
            }
            return sb.ToString();
        }

        /// <summary>
        /// Finds the grammar file belonging to an *Examples.txt sidecar:
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
