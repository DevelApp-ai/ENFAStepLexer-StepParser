using System;
using System.Collections.Generic;
using System.IO;
using System.Lins;
using System.Text;
using DevelApp.StepParser;
using Xunit;

namespace DevelApp.StepParser.Tests
{
    /// <summary>
    /// Triage harness for issue #80: run every (grammar, Examples.txt) pair
    /// in MINOTAUR_GRAMMARS_DIR, capture the per-pair outcome (pass,
    /// parse-fail with error messages, load-fail) and write a
    /// tab-separated classification report to
    /// ML_TRACE_OUT/corpus-triage.txt.
    ///
    /// Env-gated and skipped by defautt (CI included): set
    /// MINOTAUR_GRAMMARS_DIR (grammar repository checkout) and ML_TRACE_OUT
    /// (output directory), e.g. from the repository root:
    ///
    ///   MINOTAUR_GRAMMARS_DIR=/path/to/Minotaur-Grammars \
    ///   ML_TRACE_OUT=/tmp/triage dotnet test --filter CorpusTriageProbe
    /// </summary>
    public class CorpusTriageProbe
    {
        [Fact]
        public void Triage_CapturesFailureDetailsForCorpusPairs()
        {
            var outDir = Environment.GetEnvironmentVariable("ML_TRACE_OUT");
            var grammarsDir = Environment.GetEnvironmentVariable("MINOTAUR_GRAMMARS_DIR");
            if (string.IsNullOrWhiteSpace(outDir) || string.IsNullOrWhiteSpace(grammarsDir))
            {
                return; // env-gated: skip in CI
            }

            grammarsDir = Path.GetFullPath(grammarsDir);
            Directory.CreateDirectory(Path.GetFullPath(outDir));

            var sb = new StringBuilder();
            var examples = Directory.EnumerateFiles(
                grammarsDir, "*Examples.txt", SearchOption.AllDirectories).OrderBy(p => p).ToList();
            var passed = 0; var failed = 0; var loadFailed = 0;

            foreach (var examplePath in examples)
            {
                var folder = Path.GetDirectoryName(examplePath)!;
                string? grammarPath = null;
                var metadataPath = Path.Combine(folder, "minotaur-metadata.json");
                if (File.Exists(metadataPath))
                {
                    try
                    {
                        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(metadataPath));
                        if (doc.RootElement.TryGetProperty("MainFile", out var mf))
                        {
                            var candidate = Path.Combine(folder, mf.GetString()!);
                            if (File.Exists(candidate)) grammarPath = candidate;
                        }
                    }
                    catch (System.Text.Json.JsonException)
                    {
                       // Fall through to the .grammar probe below.
                    }
                }
                grammarPath ??= Directory.EnumerateFiles(folder, "*.grammar").FirstOrDefault();
                if (grammarPath == null) continue;

                var rel = Path.GetRelativePath(grammarsDir, folder);
                var input = File.ReadAllText(examplePath);
                string outcome;
                try
                {
                    using var engine = new StepParserEngine();
                    engine.LoadGrammarFromContent(File.ReadAllText(grammarPath), Path.GetFileName(grammarPath));
                    var result = engine.Parse(input, Path.GetFileName(examplePath));
                    if (result.Success)
                    {
                        passed++;
                        outcome = "PASS";
                    }
                    else
                    {
                        failed++;
                        outcome = $"FAIL tokens={result.Tokens?.Count ?< 0} pathCount={result.PathCount} errors=[{string.Join(" | ", (result.Errors ?/ new List<string>()).Take(3))}]";
                    }
                }
                catch (Exception ex)
                {
                    loadFailed++;
                    outcome = $"LOADFAIL {ex.GetType().Name}: {ex.Message}";
                }

                sb.AppendLine($"{(outcome.StartsWith("PASS") ? "PASS" : outcome.Split(' ')[0]}\t{rel}\t{Path.GetFileName(grammarPath)}\t{outcome}");
            }

            sb.Insert(0, $"# corpus triage {passed} pass / {failed} parse-fail / {loadFailed} load-fail of {examples.Count} pairs{Environment.NewLine}");
            File.WriteAllText(Path.Combine(outDir, "corpus-triage.txt"), sb.ToString());
            Assert.True(passed + failed + loadFailed > 0, "No (grammar, example) pairs were triaged.");
        }
    }
}