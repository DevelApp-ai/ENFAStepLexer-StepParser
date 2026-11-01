// Pinned tests for the changed-file normalization shared by the grammar
// repository parse-validation harness (GrammarRepositoryParseTests).
//
// Minotaur-Grammars CI lists BOTH the changed .grammar files AND the changed
// Conformance.txt files in GRAMMAR_VALIDATION_FILES (so conformance-only
// changes run the input-conformance gate). The conformance harness filters
// for its own inputs; the parse harness must do the same or it tries to
// load Conformance.txt as a grammar and fails the CI gate with
// "missing or blank 'Grammar:' header" (seen on Minotaur-Grammars #197).

using System;
using System.Linq;
using Xunit;

namespace DevelApp.StepParser.Tests
{
    /// <summary>
    /// Pins <see cref="GrammarRepositoryParseTests.NormalizeChangedGrammarList"/>:
    /// the parse-validation gate consumes only .grammar entries from the
    /// changed-files list and ignores companion files (Conformance.txt).
    /// </summary>
    public class GrammarRepositoryParseHarnessTests
    {
        [Fact]
        public void CompanionFiles_AreIgnored_GrammarFilesKept()
        {
            // The exact mixed list Minotaur-Grammars CI produces for a PR
            // that changes both the grammar and its conformance cases.
            var raw = "programming-languages/csharp10/CSharp10.grammar\n" +
                      "programming-languages/csharp10/Conformance.txt\n";

            var targets = GrammarRepositoryParseTests.NormalizeChangedGrammarList(raw);

            Assert.Single(targets);
            Assert.Equal("programming-languages/csharp10/CSharp10.grammar", targets[0]);
        }

        [Fact]
        public void ConformanceOnlyChange_YieldsEmptyList()
        {
            // A conformance-only PR: no .grammar file changed. The parse
            // gate must see zero targets (and skip) instead of failing.
            var raw = "programming-languages/csharp10/Conformance.txt\n";

            var targets = GrammarRepositoryParseTests.NormalizeChangedGrammarList(raw);

            Assert.Empty(targets);
        }

        [Fact]
        public void NullOrEmptyOrWhitespaceOnly_YieldsEmptyList()
        {
            Assert.Empty(GrammarRepositoryParseTests.NormalizeChangedGrammarList(null));
            Assert.Empty(GrammarRepositoryParseTests.NormalizeChangedGrammarList(string.Empty));
            Assert.Empty(GrammarRepositoryParseTests.NormalizeChangedGrammarList(" \n\t\n"));
        }

        [Fact]
        public void BackslashPaths_AreSlashNormalized()
        {
            var targets = GrammarRepositoryParseTests.NormalizeChangedGrammarList(
                "test-grammars\\fixture.Grammar\r\n");

            Assert.Single(targets);
            Assert.Equal("test-grammars/fixture.Grammar", targets[0]);
        }

        [Fact]
        public void DuplicateEntries_AreDeduplicated()
        {
            var raw = "a/x.grammar\n" +
                      "a/x.grammar\n" +
                      "b/y.grammar\n";

            var targets = GrammarRepositoryParseTests.NormalizeChangedGrammarList(raw);

            Assert.Equal(2, targets.Count);
            Assert.Contains("a/x.grammar", targets);
            Assert.Contains("b/y.grammar", targets);
        }

        [Fact]
        public void GrammarExtensionMatch_IsCaseInsensitive()
        {
            var targets = GrammarRepositoryParseTests.NormalizeChangedGrammarList(
                "test-grammars/fixture.GRAMMAR\n");

            Assert.Single(targets);
        }
    }
}
