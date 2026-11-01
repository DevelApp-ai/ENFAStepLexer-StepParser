using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace DevelApp.StepParser.Tests
{
    /// <summary>
    /// Offline unit tests pinning the behavior of the conformance harness in
    /// <see cref="GrammarRepositoryConformanceTests"/> (issue #194). The
    /// repository-facing gate skips when MINOTAUR_GRAMMARS_DIR is not set
    /// (CI on this repository) and runs in Minotaur-Grammars CI; these tests
    /// exercise the same core logic against temporary fixtures so the
    /// harness itself cannot silently regress:
    ///  - a satisfied conformance file passes;
    ///  - a lex case the grammar cannot consume fails;
    ///  - a fail case the grammar accepts fails;
    ///  - the changed-file mapping selects the right conformance file;
    ///  - the grammar: directive resolves the grammar file;
    ///  - malformed case lines are reported, not silently ignored;
    ///  - \n escapes inside case inputs are honoured.
    /// </summary>
    public class GrammarRepositoryConformanceHarnessTests : IDisposable
    {
        private readonly string _root;

        public GrammarRepositoryConformanceHarnessTests()
        {
            _root = Path.Combine(
                Path.GetTempPath(),
                "enfa-conformance-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(_root, recursive: true);
            }
            catch (IOException)
            {
                // Best-effort cleanup; temp files are harmless leftovers.
            }
        }

        private void WriteGrammar(string relativeDir, string fileName, string content)
        {
            var dir = Path.Combine(_root, relativeDir);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, fileName), content);
        }

        private void WriteConformance(string relativeDir, string content)
        {
            var dir = Path.Combine(_root, relativeDir);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "Conformance.txt"), content);
        }

        private const string WordGrammar =
            "Grammar: FixtureWords\n<word> ::= /[a-z]+/\n<start> ::= <word>+\n";

        private const string AToZGrammar =
            "Grammar: FixtureLetters\n<letter> ::= /[a-z]/\n<start> ::= <letter>+\n";

        [Fact]
        public void SatisfiedConformanceFilePassesInVerifyMode()
        {
            WriteGrammar("good", "FixtureWords.grammar", WordGrammar);
            WriteConformance("good", "lex: hello world\nfail: 123\n");

            var failures = GrammarRepositoryConformanceTests.ValidateConformanceRepository(
                _root, "verify", changedFilesRaw: null);

            Assert.True(failures.Count == 0, string.Join("\n", failures));
        }

        [Fact]
        public void LexCaseTheGrammarCannotConsumeIsReported()
        {
            // The grammar has no rule for digits, so "abc123" cannot lex to
            // completion; the declared lex case must fail validation.
            WriteGrammar("bad-lex", "FixtureWords.grammar", WordGrammar);
            WriteConformance("bad-lex", "lex: abc123\n");

            var failures = GrammarRepositoryConformanceTests.ValidateConformanceRepository(
                _root, "verify", changedFilesRaw: null);

            Assert.Contains(failures, static f =>
                f.Contains("bad-lex/Conformance.txt") &&
                f.Contains("did not lex to completion"));
        }

        [Fact]
        public void FailCaseTheGrammarAcceptsIsReported()
        {
            // "hello" lexes fine with the word grammar, so declaring it as a
            // fail case is wrong and must fail validation.
            WriteGrammar("bad-fail", "FixtureWords.grammar", WordGrammar);
            WriteConformance("bad-fail", "fail: hello\n");

            var failures = GrammarRepositoryConformanceTests.ValidateConformanceRepository(
                _root, "verify", changedFilesRaw: null);

            Assert.Contains(failures, static f =>
                f.Contains("bad-fail/Conformance.txt") &&
                f.Contains("unexpectedly lexed to completion"));
        }

        [Fact]
        public void ChangedModeValidatesTheSiblingConformanceFile()
        {
            WriteGrammar("changed-a", "FixtureWords.grammar", WordGrammar);
            WriteConformance("changed-a", "fail: this fail case is wrong because hello lexes\nlex: hello\n");
            WriteGrammar("changed-b", "Other.grammar", AToZGrammar);
            // changed-b has no conformance file: nothing to validate.

            var failures = GrammarRepositoryConformanceTests.ValidateConformanceRepository(
                _root, "changed", "changed-a/FixtureWords.grammar\nchanged-b/Other.grammar");

            // Only changed-a has a conformance file, and its fail case is
            // wrong, so exactly that file must be reported.
            Assert.Contains(failures, static f =>
                f.Contains("changed-a/Conformance.txt") &&
                f.Contains("unexpectedly lexed to completion"));
            Assert.DoesNotContain(failures, static f => f.Contains("changed-b"));
        }

        [Fact]
        public void ChangedModeAcceptsConformanceTxtPathsDirectly()
        {
            WriteGrammar("changed-c", "FixtureWords.grammar", WordGrammar);
            WriteConformance("changed-c", "lex: hello world\nfail: 123\n");

            var failures = GrammarRepositoryConformanceTests.ValidateConformanceRepository(
                _root, "changed", "changed-c/Conformance.txt");

            Assert.True(failures.Count == 0, string.Join("\n", failures));
        }

        [Fact]
        public void GrammarDirectiveResolvesGrammarAmongSiblings()
        {
            WriteGrammar("directive", "First.grammar", AToZGrammar);
            WriteGrammar("directive", "FixtureWords.grammar", WordGrammar);
            WriteConformance("directive",
                "grammar: FixtureWords.grammar\nlex: hello world\nfail: 123\n");

            var failures = GrammarRepositoryConformanceTests.ValidateConformanceRepository(
                _root, "verify", changedFilesRaw: null);

            Assert.True(failures.Count == 0, string.Join("\n", failures));
        }

        [Fact]
        public void AmbiguousDirectoryWithoutDirectiveIsReported()
        {
            WriteGrammar("ambiguous", "First.grammar", WordGrammar);
            WriteGrammar("ambiguous", "Second.grammar", AToZGrammar);
            WriteConformance("ambiguous", "lex: hello\n");

            var failures = GrammarRepositoryConformanceTests.ValidateConformanceRepository(
                _root, "verify", changedFilesRaw: null);

            Assert.Contains(failures, static f =>
                f.Contains("ambiguous/Conformance.txt") &&
                f.Contains("cannot resolve the grammar file"));
        }

        [Fact]
        public void MalformedCaseLineIsReportedNotIgnored()
        {
            WriteGrammar("malformed", "FixtureWords.grammar", WordGrammar);
            WriteConformance("malformed", "lex: hello\nbogus line here\n");

            var failures = GrammarRepositoryConformanceTests.ValidateConformanceRepository(
                _root, "verify", changedFilesRaw: null);

            Assert.Contains(failures, static f =>
                f.Contains("malformed/Conformance.txt") &&
                f.Contains("could not parse conformance file"));
        }

        [Fact]
        public void NewlineEscapeInsideCaseInputIsHonoured()
        {
            // The input is "alpha\nbeta": the newline is a real LF and must
            // be consumed by the engine's default whitespace rule, proving
            // the \n escape reached the lexer input verbatim.
            WriteGrammar("escapes", "FixtureWords.grammar", WordGrammar);
            WriteConformance("escapes", "lex: alpha\\nbeta\nfail: 123\n");

            var failures = GrammarRepositoryConformanceTests.ValidateConformanceRepository(
                _root, "verify", changedFilesRaw: null);

            Assert.True(failures.Count == 0, string.Join("\n", failures));
        }

        [Fact]
        public void EmptyConformanceFileIsReported()
        {
            WriteGrammar("empty", "FixtureWords.grammar", WordGrammar);
            WriteConformance("empty", "# only comments\n");

            var failures = GrammarRepositoryConformanceTests.ValidateConformanceRepository(
                _root, "verify", changedFilesRaw: null);

            Assert.Contains(failures, static f =>
                f.Contains("empty/Conformance.txt") &&
                f.Contains("declares no cases"));
        }

        [Fact]
        public void NoneAndRecordModesSkipConformanceValidation()
        {
            WriteGrammar("skipped", "FixtureWords.grammar", WordGrammar);
            WriteConformance("skipped", "fail: this would fail\n");

            Assert.True(GrammarRepositoryConformanceTests.ValidateConformanceRepository(
                _root, "none", changedFilesRaw: null).Count == 0);
            Assert.True(GrammarRepositoryConformanceTests.ValidateConformanceRepository(
                _root, "record", changedFilesRaw: null).Count == 0);
        }
    }
}
