using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using DevelApp.StepParser;
using Xunit;
using Xunit.Abstractions;

namespace DevelApp.StepParser.Tests
{
    /// <summary>
    /// Input conformance validation for grammars in the consolidated grammar
    /// repository (https://github.com/DevelApp-ai/Minotaur-Grammars), building
    /// on the parse-validation harness in <see cref="GrammarRepositoryParseTests"/>
    /// (which proves that a grammar file loads) by additionally proving that
    /// a grammar accepts and rejects the inputs it declares as accepted and
    /// rejected (issue #194: a grammar that loads but vacuously accepts or
    /// rejects every input passes load validation while being unusable).
    ///
    /// Conformance cases are opt-in per grammar: a <c>Conformance.txt</c>
    /// file placed next to a grammar file declares the cases. Grammars
    /// without a <c>Conformance.txt</c> are not affected.
    ///
    /// Format (one case per line; <c>#</c> starts a comment; blank lines are
    /// ignored; the escapes \n, \t and \\ are honoured inside case inputs):
    /// <code>
    /// # Optional explicit grammar file (default: the only *.grammar sibling)
    /// grammar: CSharp10.grammar
    /// # Input that MUST lex to completion: some lexer path consumes the
    /// # entire input and every emitted token has a non-zero length.
    /// lex: class C { }
    /// # Input that MUST NOT lex to completion: the lexer reports LX1001
    /// # (no token rule matches at some position) or LX1002 (stalled).
    /// fail: `backticks are not C#`
    /// </code>
    ///
    /// Modes (mirroring <see cref="GrammarRepositoryParseTests"/>; the
    /// repository-facing entry point is gated on MINOTAUR_GRAMMARS_DIR, but
    /// the core logic in <see cref="ValidateConformanceRepository"/> is
    /// directly unit-tested so its behavior is pinned even in offline runs):
    ///
    ///  - "changed": validate the <c>Conformance.txt</c> files related to
    ///    the .grammar / Conformance.txt paths listed in
    ///    GRAMMAR_VALIDATION_FILES. This is the fast per-PR path used by
    ///    Minotaur-Grammars CI.
    ///
    ///  - "verify" (default): validate ALL Conformance.txt files in the
    ///    repository. Conformance has no baseline: an opt-in conformance
    ///    file must always pass.
    ///
    ///  - "none" / "record": skipped (conformance has no baseline to record).
    /// </summary>
    public class GrammarRepositoryConformanceTests
    {
        private const string ConformanceFileName = "Conformance.txt";

        private readonly ITestOutputHelper _output;

        public GrammarRepositoryConformanceTests(ITestOutputHelper output)
        {
            _output = output;
        }

        [Fact]
        public void GrammarRepository_GrammarsSatisfyDeclaredConformanceCases()
        {
            var dir = Environment.GetEnvironmentVariable("MINOTAUR_GRAMMARS_DIR");
            if (string.IsNullOrWhiteSpace(dir))
            {
                _output.WriteLine(
                    "MINOTAUR_GRAMMARS_DIR is not set - skipping grammar repository " +
                    "conformance validation (offline/dev mode).");
                return;
            }

            dir = Path.GetFullPath(dir);
            var mode = (Environment.GetEnvironmentVariable("GRAMMAR_VALIDATION_MODE") ?? "verify")
                .Trim().ToLowerInvariant();
            if (mode is "none" or "record")
            {
                _output.WriteLine($"GRAMMAR_VALIDATION_MODE={mode} - conformance validation skipped.");
                return;
            }

            var failures = ValidateConformanceRepository(
                dir, mode, Environment.GetEnvironmentVariable("GRAMMAR_VALIDATION_FILES"));

            Assert.True(
                failures.Count == 0,
                $"{failures.Count} conformance case(s) failed:\n{string.Join("\n", failures)}");
        }

        /// <summary>
        /// Core validation loop, shared by the repository-facing gate and the
        /// offline unit tests: validate the conformance files selected by
        /// <paramref name="mode"/> (see the class summary) below
        /// <paramref name="root"/> and return one failure line per problem;
        /// empty means everything passed.
        /// </summary>
        internal static List<string> ValidateConformanceRepository(
            string root, string mode, string? changedFilesRaw)
        {
            if (mode is "none" or "record")
            {
                return new List<string>();
            }

            var targets = mode == "changed"
                ? LoadChangedConformanceFiles(root, changedFilesRaw)
                : DiscoverConformanceFiles(root);

            var failures = new List<string>();
            foreach (var relativePath in targets)
            {
                var fileFailures = ValidateConformanceFile(root, relativePath);
                if (fileFailures.Count == 0)
                {
                    continue;
                }

                foreach (var error in fileFailures)
                {
                    failures.Add($"{relativePath}: {error}");
                }
            }

            return failures;
        }

        /// <summary>
        /// Validate one Conformance.txt file: load its grammar once, run
        /// every declared case, and return the list of problems found;
        /// empty means valid.
        /// </summary>
        private static List<string> ValidateConformanceFile(string root, string relativePath)
        {
            var errors = new List<string>();
            var fullConformancePath = Path.Combine(root, relativePath);
            var grammarDirectory = Path.GetDirectoryName(fullConformancePath)!;

            string? grammarDirective;
            ConformanceCase[] cases;
            try
            {
                (grammarDirective, cases) = ParseConformanceFile(fullConformancePath);
            }
            catch (Exception ex)
            {
                errors.Add($"could not parse conformance file: {ex.Message}");
                return errors;
            }

            if (cases.Length == 0)
            {
                errors.Add("conformance file declares no cases");
                return errors;
            }

            var grammarFile = ResolveGrammarFile(grammarDirectory, grammarDirective);
            if (grammarFile == null)
            {
                errors.Add("cannot resolve the grammar file (use a 'grammar:' directive " +
                           "or keep exactly one .grammar file next to the conformance file)");
                return errors;
            }

            string grammarContent;
            try
            {
                grammarContent = File.ReadAllText(Path.Combine(grammarDirectory, grammarFile));
            }
            catch (Exception ex)
            {
                errors.Add($"cannot read grammar file '{grammarFile}': {ex.Message}");
                return errors;
            }

            try
            {
                using var engine = new StepParserEngine();
                engine.LoadGrammarFromContent(grammarContent, grammarFile);
                foreach (var testCase in cases)
                {
                    errors.AddRange(ValidateCase(engine, grammarFile, testCase));
                }
            }
            catch (Exception ex)
            {
                errors.Add($"grammar '{grammarFile}' failed to load: {ex.Message}");
            }

            return errors;
        }

        /// <summary>
        /// Run one conformance case against a loaded engine and return its
        /// problems; empty means the case passed.
        /// </summary>
        private static List<string> ValidateCase(
            StepParserEngine engine, string grammarFile, ConformanceCase testCase)
        {
            var errors = new List<string>();
            var caseLabel = $"line {testCase.LineNumber} {testCase.Kind} case " +
                            (testCase.Input.Length > 40 ? testCase.Input[..40] + "..." : testCase.Input);

            StepParsingResult result;
            try
            {
                result = engine.Parse(testCase.Input, $"conformance:{grammarFile}");
            }
            catch (Exception ex)
            {
                errors.Add($"{caseLabel}: threw {ex.GetType().Name}: {ex.Message}");
                return errors;
            }

            var lexerFailures = result.Diagnostics
                .Where(d => d.Code == DiagnosticCodes.LexerUnexpectedInput
                         || d.Code == DiagnosticCodes.LexerStalled)
                .ToList();
            var zeroLengthTokens = result.Tokens.Count(static t => t.Length == 0);

            if (testCase.Kind == ConformanceCaseKind.Lex)
            {
                if (lexerFailures.Count > 0)
                {
                    errors.Add($"{caseLabel}: input did not lex to completion " +
                                $"({string.Join("; ", lexerFailures.Select(f => f.Code + ": " + f.Message))})");
                }
                else if (result.Tokens.Count == 0)
                {
                    errors.Add($"{caseLabel}: lexed to completion but produced no tokens");
                }
                else if (zeroLengthTokens > 0)
                {
                    errors.Add($"{caseLabel}: {zeroLengthTokens} zero-length token(s) " +
                                "emitted (issue #111 class of vacuous acceptance)");
                }
            }
            else // Fail
            {
                if (lexerFailures.Count == 0 && zeroLengthTokens == 0)
                {
                    errors.Add($"{caseLabel}: input unexpectedly lexed to completion " +
                                "(it must be rejected by the token rules)");
                }
            }

            return errors;
        }

        /// <summary>
        /// Parse a Conformance.txt file into its explicit grammar directive
        /// (null when absent) and its cases. See the class summary for the
        /// supported format.
        /// </summary>
        private static (string? GrammarDirective, ConformanceCase[] Cases) ParseConformanceFile(
            string fullPath)
        {
            var cases = new List<ConformanceCase>();
            string? grammarDirective = null;

            var lines = File.ReadAllLines(fullPath);
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i].Trim();
                if (line.Length == 0 || line.StartsWith("#"))
                {
                    continue;
                }

                if (line.StartsWith("grammar:", StringComparison.OrdinalIgnoreCase))
                {
                    grammarDirective = line["grammar:".Length..].Trim();
                    continue;
                }

                const string LexPrefix = "lex:";
                const string FailPrefix = "fail:";
                ConformanceCaseKind kind;
                string input;
                if (line.StartsWith(LexPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    kind = ConformanceCaseKind.Lex;
                    input = line[LexPrefix.Length..].TrimStart();
                }
                else if (line.StartsWith(FailPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    kind = ConformanceCaseKind.Fail;
                    input = line[FailPrefix.Length..].TrimStart();
                }
                else
                {
                    throw new InvalidDataException(
                        $"unrecognized line {i + 1}: '{line}' (expected 'lex:', 'fail:', " +
                        "'grammar:' or '#')");
                }

                cases.Add(new ConformanceCase(kind, Unescape(input), i + 1));
            }

            return (grammarDirective, cases.ToArray());
        }

        /// <summary>
        /// Resolve the grammar file a conformance file belongs to: an
        /// explicit 'grammar:' directive wins; otherwise the directory must
        /// contain exactly one .grammar file.
        /// </summary>
        private static string? ResolveGrammarFile(string grammarDirectory, string? grammarDirective)
        {
            if (grammarDirective != null)
            {
                return File.Exists(Path.Combine(grammarDirectory, grammarDirective))
                    ? grammarDirective
                    : null;
            }

            var grammars = Directory.Exists(grammarDirectory)
                ? Directory.GetFiles(grammarDirectory, "*.grammar")
                    .Select(Path.GetFileName)
                    .Where(static n => n != null)
                    .Select(static n => n!)
                    .ToList()
                : new List<string>();
            return grammars.Count == 1 ? grammars[0] : null;
        }

        /// <summary>
        /// Honour the \n, \t and \\ escapes inside a conformance case input.
        /// </summary>
        private static string Unescape(string input)
        {
            var sb = new StringBuilder(input.Length);
            for (var i = 0; i < input.Length; i++)
            {
                if (input[i] == '\\' && i + 1 < input.Length)
                {
                    switch (input[i + 1])
                    {
                        case 'n': sb.Append('\n'); i++; continue;
                        case 't': sb.Append('\t'); i++; continue;
                        case '\\': sb.Append('\\'); i++; continue;
                    }
                }

                sb.Append(input[i]);
            }

            return sb.ToString();
        }

        /// <summary>
        /// Find all Conformance.txt files below <paramref name="root"/>,
        /// as repository-relative paths.
        /// </summary>
        private static List<string> DiscoverConformanceFiles(string root)
        {
            return Directory
                .EnumerateFiles(root, ConformanceFileName, SearchOption.AllDirectories)
                .Select(p => Path.GetRelativePath(root, p).Replace('\\', '/'))
                .OrderBy(p => p, StringComparer.Ordinal)
                .ToList();
        }

        /// <summary>
        /// Map the changed .grammar / Conformance.txt paths listed in
        /// <paramref name="changedFilesRaw"/> to the conformance files
        /// affected by them (the sibling Conformance.txt of a changed
        /// grammar, or the changed conformance file itself).
        /// </summary>
        private static List<string> LoadChangedConformanceFiles(string root, string? changedFilesRaw)
        {
            var raw = changedFilesRaw ?? string.Empty;
            var changed = raw
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(l => l.Trim().Replace('\\', '/'))
                .Where(l => l.Length > 0)
                .Distinct(StringComparer.Ordinal)
                .ToList();

            var targets = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var entry in changed)
            {
                var fileName = Path.GetFileName(entry);
                if (string.Equals(fileName, ConformanceFileName, StringComparison.OrdinalIgnoreCase))
                {
                    targets.Add(entry);
                }
                else if (entry.EndsWith(".grammar", StringComparison.OrdinalIgnoreCase))
                {
                    var sibling = Path.Combine(
                        Path.GetDirectoryName(entry)!,
                        ConformanceFileName).Replace('\\', '/');
                    if (File.Exists(Path.Combine(root, sibling)))
                    {
                        targets.Add(sibling);
                    }
                }
            }

            return targets.ToList();
        }

        private readonly struct ConformanceCase
        {
            public ConformanceCase(ConformanceCaseKind kind, string input, int lineNumber)
            {
                Kind = kind;
                Input = input;
                LineNumber = lineNumber;
            }

            public ConformanceCaseKind Kind { get; }

            public string Input { get; }

            public int LineNumber { get; }
        }

        private enum ConformanceCaseKind
        {
            Lex,
            Fail
        }
    }
}
