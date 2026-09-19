using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DevelApp.StepParser;
using Xunit;
using Xunit.Abstractions;

namespace DevelApp.StepParser.Tests
{
    /// <summary>
    /// Parse-validates grammar files from the consolidated grammar repository
    /// (https://github.com/DevelApp-ai/Minotaur-Grammars) through the full
    /// StepLexer/StepParser pipeline: <see cref="StepParserEngine.LoadGrammarFromContent"/>
    /// parses the grammar definition and configures both the lexer and the parser
    /// with its token and production rules.
    ///
    /// The grammar loader is deliberately lenient (malformed lines are skipped
    /// rather than rejected), so "did not throw" is not a sufficient check.
    /// Each grammar must additionally pass structural validation: a parsed
    /// <see cref="GrammarDefinition"/>, a non-blank Grammar: header, and at
    /// least one token or production rule.
    ///
    /// Modes (environment variable GRAMMAR_VALIDATION_MODE; all modes require
    /// MINOTAUR_GRAMMARS_DIR to be set, otherwise the test is skipped):
    ///
    ///  - "changed": validate only the files listed in GRAMMAR_VALIDATION_FILES
    ///    (newline-separated repository-relative paths). Files that appear in
    ///    the baseline (.grammar-parse-baseline.txt in the grammar repository
    ///    root) may keep failing (pre-existing state); anything else must pass.
    ///    This is the fast per-PR path used by Minotaur-Grammars CI.
    ///
    ///  - "verify" (default): validate ALL grammar files in the repository and
    ///    require the set of failing files to match the baseline exactly, both
    ///    flagging regressions (new failures) and unexpectedly fixed files
    ///    (baseline should be refreshed via "record").
    ///
    ///  - "record": validate ALL grammar files and write the observed set of
    ///    failing files to GRAMMAR_VALIDATION_BASELINE_OUT for upload as a CI
    ///    artifact; the test itself always passes in this mode. Used to create
    ///    or refresh the baseline.
    /// </summary>
    public class GrammarRepositoryParseTests
    {
        private const string BaselineFileName = ".grammar-parse-baseline.txt";

        private readonly ITestOutputHelper _output;

        public GrammarRepositoryParseTests(ITestOutputHelper output)
        {
            _output = output;
        }

        [Fact]
        public void GrammarRepository_GrammarsAreParseableByStepLexerAndStepParser()
        {
            var dir = Environment.GetEnvironmentVariable("MINOTAUR_GRAMMARS_DIR");
            if (string.IsNullOrWhiteSpace(dir))
            {
                _output.WriteLine(
                    "MINOTAUR_GRAMMARS_DIR is not set - skipping grammar repository " +
                    "parse validation (offline/dev mode).");
                return;
            }

            dir = Path.GetFullPath(dir);
            var mode = (Environment.GetEnvironmentVariable("GRAMMAR_VALIDATION_MODE") ?? "verify")
                .Trim().ToLowerInvariant();
            if (mode == "none")
            {
                _output.WriteLine("GRAMMAR_VALIDATION_MODE=none - validation skipped.");
                return;
            }

            var baseline = LoadBaseline(dir);
            _output.WriteLine($"Mode: {mode}; baseline entries: {baseline.Count}");

            var targets = mode == "changed"
                ? LoadChangedFileList()
                : DiscoverAllGrammarFiles(dir);
            Assert.True(
                targets.Count > 0,
                $"No grammar files to validate (mode={mode}, dir={dir}).");

            _output.WriteLine($"Validating {targets.Count} grammar file(s) with StepLexer/StepParser...");

            var failures = new SortedSet<string>();
            foreach (var relativePath in targets)
            {
                var errors = ValidateGrammar(Path.Combine(dir, relativePath));
                if (errors.Count == 0)
                {
                    _output.WriteLine($"  OK       {relativePath}");
                    continue;
                }

                failures.Add(relativePath);
                foreach (var error in errors)
                {
                    _output.WriteLine($"  FAIL     {relativePath}: {error}");
                }
            }

            _output.WriteLine(
                $"Result: {targets.Count - failures.Count} passed, {failures.Count} failed.");

            if (mode == "record")
            {
                var outFile = Environment.GetEnvironmentVariable("GRAMMAR_VALIDATION_BASELINE_OUT");
                if (!string.IsNullOrWhiteSpace(outFile))
                {
                    File.WriteAllLines(outFile, failures);
                    _output.WriteLine($"Baseline written to {outFile} ({failures.Count} entries).");
                }
                else
                {
                    _output.WriteLine(
                        "GRAMMAR_VALIDATION_BASELINE_OUT not set - baseline content follows:");
                    foreach (var entry in failures)
                    {
                        _output.WriteLine($"  {entry}");
                    }
                }

                return;
            }

            if (mode == "changed")
            {
                var regressions = failures.Except(baseline, StringComparer.Ordinal).ToList();
                Assert.True(
                    regressions.Count == 0,
                    $"{regressions.Count} changed grammar(s) failed StepLexer/StepParser " +
                    $"validation:\n{string.Join("\n", regressions)}\n\n" +
                    "Files listed in " + BaselineFileName + " are exempt (pre-existing failures).");
                return;
            }

            // verify mode: the failing set must match the baseline exactly.
            var newFailures = failures.Except(baseline, StringComparer.Ordinal).ToList();
            var unexpectedlyFixed = baseline.Except(failures, StringComparer.Ordinal).ToList();
            Assert.True(
                newFailures.Count == 0 && unexpectedlyFixed.Count == 0,
                $"Grammar parse state diverges from {BaselineFileName}.\n" +
                $"Regressions ({newFailures.Count}):\n{string.Join("\n", newFailures)}\n" +
                $"Unexpectedly fixed ({unexpectedlyFixed.Count}):\n" +
                $"{string.Join("\n", unexpectedlyFixed)}\n\n" +
                "Re-run the workflow in 'record' mode and commit the regenerated " +
                BaselineFileName + " to update the recorded state.");
        }

        /// <summary>
        /// Loads a grammar file through <see cref="StepParserEngine"/> (which
        /// configures both StepLexer and StepParser) and applies structural
        /// checks. Returns the list of problems found; empty means valid.
        /// </summary>
        private static List<string> ValidateGrammar(string fullPath)
        {
            var errors = new List<string>();
            try
            {
                var content = File.ReadAllText(fullPath);
                using var engine = new StepParserEngine();
                engine.LoadGrammarFromContent(content, Path.GetFileName(fullPath));

                var grammar = engine.CurrentGrammar;
                if (grammar == null)
                {
                    errors.Add("grammar did not load (CurrentGrammar is null)");
                    return errors;
                }

                if (string.IsNullOrWhiteSpace(grammar.Name))
                {
                    errors.Add("missing or blank 'Grammar:' header");
                }

                if (grammar.TokenRules.Count == 0 && grammar.ProductionRules.Count == 0)
                {
                    errors.Add("grammar produced no token rules and no production rules");
                }
            }
            catch (Exception ex)
            {
                errors.Add($"exception while loading: {ex.Message}");
            }

            return errors;
        }

        private static List<string> DiscoverAllGrammarFiles(string root)
        {
            return Directory
                .EnumerateFiles(root, "*.grammar", SearchOption.AllDirectories)
                .Select(p => Path.GetRelativePath(root, p).Replace('\\', '/'))
                .OrderBy(p => p, StringComparer.Ordinal)
                .ToList();
        }

        private static List<string> LoadChangedFileList()
        {
            var raw = Environment.GetEnvironmentVariable("GRAMMAR_VALIDATION_FILES") ?? string.Empty;
            return raw
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(l => l.Trim().Replace('\\', '/'))
                .Where(l => l.Length > 0)
                .Distinct(StringComparer.Ordinal)
                .ToList();
        }

        private static HashSet<string> LoadBaseline(string root)
        {
            var path = Path.Combine(root, BaselineFileName);
            var result = new HashSet<string>(StringComparer.Ordinal);
            if (!File.Exists(path))
            {
                return result;
            }

            foreach (var line in File.ReadAllLines(path))
            {
                var trimmed = line.Trim();
                if (trimmed.Length == 0 || trimmed.StartsWith("#"))
                {
                    continue;
                }

                result.Add(trimmed.Replace('\\', '/'));
            }

            return result;
        }
    }
}
