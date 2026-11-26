using System;
using System.Collections.Generic;
using System.Globalization;
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
    /// ML-assist end-to-end evaluation harness (issue #93, part of the #58
    /// plan tracked by #105): measures the corpus with and without the
    /// trained artifacts and writes a comparison CSV.
    ///
    /// Env-gated and skipped by default (CI included): set ML_EVAL_OUT
    /// (output directory), MINOTAUR_GRAMMARS_DIR (grammar repository
    /// checkout) and optionally ML_ASSIST_ARTIFACT_DIR (trained artifacts;
    /// defaults to docs/ml-assist/artifacts in the repository) to run it:
    ///
    ///   MINOTAUR_GRAMMARS_DIR=/path/to/Minotaur-Grammars \
    ///   ML_EVAL_OUT=/tmp/ml-eval dotnet test -c Release \
    ///   --filter "FullyQualifiedName~MlAssistEvaluation"
    ///
    /// Contract: the harness FAILS if ML changes any parse success — the
    /// zero-semantic-change guarantee of issue #58 is asserted, not
    /// assumed. Timing numbers are machine-dependent and only meaningful
    /// for same-run, relative comparisons (see docs/ml-baseline/README.md).
    /// </summary>
    public class MlAssistEvaluationTests : IDisposable
    {
        private const int RunsPerArm = 3;

        private readonly ITestOutputHelper _output;

        public MlAssistEvaluationTests(ITestOutputHelper output)
        {
            _output = output;
            MlAssistOptions.ResetForTest();
            Environment.SetEnvironmentVariable(MlAssistOptions.DisableAllEnvVar, null);
        }

        public void Dispose()
        {
            MlAssistOptions.ResetForTest();
            Environment.SetEnvironmentVariable(MlAssistOptions.DisableAllEnvVar, null);
        }

        [Fact]
        public void MlAssist_MeasuresCorpusWithAndWithoutTrainedArtifacts()
        {
            var outDir = Environment.GetEnvironmentVariable("ML_EVAL_OUT");
            var grammarsDir = Environment.GetEnvironmentVariable("MINOTAUR_GRAMMARS_DIR");
            if (string.IsNullOrWhiteSpace(outDir) || string.IsNullOrWhiteSpace(grammarsDir))
            {
                _output.WriteLine(
                    "ML_EVAL_OUT or MINOTAUR_GRAMMARS_DIR is not set - skipping " +
                    "ML-assist evaluation (offline/dev mode).");
                return;
            }

            var artifactDir = Environment.GetEnvironmentVariable("ML_ASSIST_ARTIFACT_DIR");
            if (string.IsNullOrWhiteSpace(artifactDir))
            {
                var repoRoot = FindRepositoryRoot();
                Assert.True(repoRoot != null,
                    "Could not locate the repository root (ENFAStepLexer-StepParser.sln) - " +
                    "set ML_ASSIST_ARTIFACT_DIR explicitly.");
                artifactDir = Path.Combine(repoRoot!, "docs", "ml-assist", "artifacts");
            }

            var ruleArtifact = LoadArtifact(Path.Combine(artifactDir, "rule-prioritizer.json"));
            var pathArtifact = LoadArtifact(Path.Combine(artifactDir, "path-pruner.json"));
            if (ruleArtifact == null && pathArtifact == null)
            {
                _output.WriteLine(
                    $"No trained artifacts in {artifactDir} - train both models with " +
                    "tools/ml-assist/train.py first (docs/ML-Assist.md, issue #93).");
                return;
            }

            Assert.True(ruleArtifact != null && pathArtifact != null,
                "Evaluation expects both artifacts (rule-prioritizer.json and " +
                "path-pruner.json) - train both models with tools/ml-assist/train.py.");

            grammarsDir = Path.GetFullPath(grammarsDir);
            outDir = Path.GetFullPath(outDir);
            Directory.CreateDirectory(outDir);

            var examples = Directory.EnumerateFiles(
                grammarsDir, "*Examples.txt", SearchOption.AllDirectories).OrderBy(p => p).ToList();
            Assert.True(
                examples.Count > 0,
                $"No *Examples.txt files found under {grammarsDir} - nothing to measure.");

            var csvPath = Path.Combine(outDir, "ml-assist-evaluation.csv");
            var measured = 0;
            var semanticMismatches = new List<string>();
            var evaluationStartUtc = DateTime.UtcNow;
            using (var writer = new StreamWriter(csvPath, append: false))
            {
                writer.WriteLine(
                    "grammar,example,successOff,successOn,parseTimeOffMicros,parseTimeOnMicros," +
                    "wastedRatioOff,wastedRatioOn,pathsPruned,consultations");

                foreach (var examplePath in examples)
                {
                    var grammarPath = ResolveGrammarForExample(examplePath);
                    if (grammarPath == null)
                    {
                        _output.WriteLine(
                            $"  SKIP     {Relative(grammarsDir, examplePath)} (no sibling grammar)");
                        continue;
                    }

                    if (MlCorpusTriage.ShouldSkip(Path.GetFileName(grammarPath), out var skipReason))
                    {
                        _output.WriteLine(
                            $"  SKIP     {Relative(grammarsDir, grammarPath)} ({skipReason})");
                        continue;
                    }

                    if (MlCorpusTriage.BudgetExceeded(evaluationStartUtc, out var budgetMsg))
                    {
                        _output.WriteLine($"  BUDGET-STOP {budgetMsg}");
                        break;
                    }

                    var grammarContent = File.ReadAllText(grammarPath);
                    var inputContent = File.ReadAllText(examplePath);

                    // Baseline arm: the deterministic engine (the shipped default).
                    MlAssistOptions.ResetForTest();
                    var (successOff, timeOff, wastedOff, _, _) = MeasureParse(
                        grammarContent, Path.GetFileName(grammarPath),
                        inputContent, Path.GetFileName(examplePath),
                        rulePrioritizer: null, pathPruner: null);

                    // ML arm: trained artifacts enabled per feature with their model versions.
                    MlAssistOptions.ResetForTest();
                    var (rulePrioritizer, pathPruner) =
                        EnableArtifacts(ruleArtifact!, pathArtifact!);
                    var (successOn, timeOn, wastedOn, pruned, consultations) = MeasureParse(
                        grammarContent, Path.GetFileName(grammarPath),
                        inputContent, Path.GetFileName(examplePath),
                        rulePrioritizer, pathPruner);
                    MlAssistOptions.ResetForTest();

                    if (successOff != successOn)
                    {
                        // Collect instead of failing on the first hit so a single
                        // run reports every offending grammar (a corpus sweep is
                        // expensive; the harness must not stop at mismatch #1).
                        semanticMismatches.Add(
                            $"{Relative(grammarsDir, grammarPath)} + {Relative(grammarsDir, examplePath)} " +
                            $"(baseline {successOff}, ML-assisted {successOn})");
                    }

                    writer.WriteLine(string.Join(",",
                        Relative(grammarsDir, grammarPath),
                        Relative(grammarsDir, examplePath),
                        successOff,
                        successOn,
                        timeOff.ToString("F1", CultureInfo.InvariantCulture),
                        timeOn.ToString("F1", CultureInfo.InvariantCulture),
                        wastedOff.ToString("F4", CultureInfo.InvariantCulture),
                        wastedOn.ToString("F4", CultureInfo.InvariantCulture),
                        pruned,
                        consultations));

                    measured++;
                    _output.WriteLine(
                        $"  MEASURED {Relative(grammarsDir, grammarPath)} + " +
                        $"{Relative(grammarsDir, examplePath)} " +
                        $"(off {timeOff.ToString("F0", CultureInfo.InvariantCulture)}us, " +
                        $"on {timeOn.ToString("F0", CultureInfo.InvariantCulture)}us)");
                }
            }

            _output.WriteLine(
                $"Evaluation written to {csvPath} ({measured} record(s)). " +
                "Summarize with: python3 tools/ml-assist/train.py summarize-eval " + csvPath);

            if (semanticMismatches.Count > 0)
            {
                // Companion file so the full violation list survives even when
                // the assertion below short-circuits the xUnit output.
                File.WriteAllLines(
                    Path.Combine(outDir, "ml-eval-semantic-mismatches.txt"),
                    semanticMismatches);
            }

            // Coverage companion (honest reporting: what the corpus run did
            // and did not cover) so measurement reports can cite it.
            var coverage = Path.Combine(outDir, "ml-eval-coverage.txt");
            File.WriteAllLines(coverage, new[]
            {
                $"eval startedUtc={evaluationStartUtc:u} finishedUtc={DateTime.UtcNow:u}",
                $"measured={measured}",
                $"defaultSkipList={string.Join(",", MlCorpusTriage.DefaultSkipList)}",
                $"envSkipList={Environment.GetEnvironmentVariable(MlCorpusTriage.SkipListEnvVar) ?? string.Empty}",
                $"budgetMinutes={Environment.GetEnvironmentVariable(MlCorpusTriage.BudgetMinutesEnvVar) ?? string.Empty}",
            });

            Assert.True(measured > 0, "No (grammar, example) pairs could be measured.");

            Assert.True(
                semanticMismatches.Count == 0,
                "ML-assist changed parse success for " + semanticMismatches.Count +
                " grammar(s) (zero semantic change is a hard constraint of issue #58): " +
                string.Join("; ", semanticMismatches));
        }

        // --- helpers ---

        /// <summary>
        /// Parse one (grammar, input) pair several times with the given ML
        /// configuration and return the best parse time, the wasted-attempt
        /// ratio of the last run and the pruner counters. A fresh engine per
        /// run keeps the arms comparable; the minimum time tolerates noise
        /// from parallel tests.
        /// </summary>
        private static (bool success, double bestMicros, double wastedRatio, long pathsPruned, long consultations)
            MeasureParse(
                string grammarContent, string grammarFileName,
                string inputContent, string inputFileName,
                ILearnedRulePrioritizer? rulePrioritizer, LearnedPathPruner? pathPruner)
        {
            var best = double.MaxValue;
            var success = false;
            var wastedRatio = 0.0;
            for (var run = 0; run < RunsPerArm; run++)
            {
                RuleMatchDiagnostics.Reset();
                pathPruner?.ResetCounters();

                using var engine = new StepParserEngine();
                engine.RulePrioritizer = rulePrioritizer;
                engine.PathPruner = pathPruner;
                engine.LoadGrammarFromContent(grammarContent, grammarFileName);

                var result = engine.Parse(inputContent, inputFileName);
                success = result.Success;
                wastedRatio = RuleMatchDiagnostics.WastedAttemptRatio;
                best = Math.Min(best, result.ParseTime.TotalMicroseconds);
            }

            return (success, best, wastedRatio, pathPruner?.PathsPruned ?? 0, pathPruner?.Consultations ?? 0);
        }

        /// <summary>
        /// Load the trained artifacts and enable both features with their
        /// model versions (issue #58: every ML-assisted run is attributable
        /// to a concrete, reproducible model).
        /// </summary>
        private static (ILearnedRulePrioritizer? rule, LearnedPathPruner? path)
            EnableArtifacts(Artifact ruleArtifact, Artifact pathArtifact)
        {
            ILearnedRulePrioritizer? rulePrioritizer = null;
            LearnedPathPruner? pathPruner = null;

            rulePrioritizer = new LearnedRulePrioritizer(ruleArtifact.Version, ruleArtifact.Weights);
            MlAssistOptions.Enable(MlAssistFeature.LearnedRulePrioritization, ruleArtifact.Version);

            pathPruner = new LearnedPathPruner(
                pathArtifact.Version,
                pathArtifact.ConfidenceThreshold ?? 0.9,
                pathArtifact.MinPathCount ?? 10,
                pathArtifact.Weights);
            MlAssistOptions.Enable(MlAssistFeature.LearnedPathPruning, pathArtifact.Version);

            return (rulePrioritizer, pathPruner);
        }

        /// <summary>
        /// Read a trained artifact JSON (schema ml-assist-artifact/1).
        /// Returns null when the file does not exist yet.
        /// </summary>
        private static Artifact? LoadArtifact(string path)
        {
            if (!File.Exists(path))
            {
                return null;
            }

            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;

            double[] weights;
            if (root.TryGetProperty("weights", out var weightsElement)
                && weightsElement.ValueKind == JsonValueKind.Array)
            {
                weights = weightsElement.EnumerateArray()
                    .Select(e => e.GetDouble())
                    .ToArray();
            }
            else
            {
                weights = Array.Empty<double>();
            }

            return new Artifact
            {
                Version = root.TryGetProperty("modelVersion", out var versionElement)
                    && versionElement.ValueKind == JsonValueKind.String
                        ? versionElement.GetString() ?? string.Empty
                        : string.Empty,
                Weights = weights,
                ConfidenceThreshold =
                    root.TryGetProperty("confidenceThreshold", out var thresholdElement)
                        && thresholdElement.ValueKind == JsonValueKind.Number
                        ? thresholdElement.GetDouble()
                        : null,
                MinPathCount =
                    root.TryGetProperty("minPathCount", out var minPathElement)
                        && minPathElement.ValueKind == JsonValueKind.Number
                        ? minPathElement.GetInt32()
                        : null
            };
        }

        /// <summary>A trained artifact JSON (schema ml-assist-artifact/1).</summary>
        private sealed class Artifact
        {
            public string Version { get; set; } = string.Empty;
            public double[] Weights { get; set; } = Array.Empty<double>();
            public double? ConfidenceThreshold { get; set; }
            public int? MinPathCount { get; set; }
        }

        /// <summary>Walk up from the test directory to the solution root.</summary>
        private static string? FindRepositoryRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "ENFAStepLexer-StepParser.sln")))
            {
                dir = dir.Parent;
            }

            return dir?.FullName;
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
