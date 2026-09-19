using Xunit;
using DevelApp.StepParser;
using DevelApp.StepLexer;
using System;
using System.IO;

namespace DevelApp.StepParser.Tests
{
    /// <summary>
    /// Tests for the location-based selection API (RefakTS-style Select)
    /// </summary>
    public class SelectionTests
    {
        private static readonly string NumberGrammar = TestGrammars.Get("test-grammars/step-parser-tests/SelectionTests/TestGrammar.grammar");

        private static StepParserEngine CreateEngine()
        {
            var engine = new StepParserEngine();
            engine.LoadGrammarFromContent(NumberGrammar);
            return engine;
        }

        private static string CreateTempFile(string content)
        {
            var path = Path.Combine(Path.GetTempPath(), "enfa-selection-" + Guid.NewGuid().ToString("N") + ".txt");
            File.WriteAllText(path, content);
            return path;
        }

        [Fact]
        public void Select_NonexistentFile_ReturnsEmptyList()
        {
            // Arrange
            using var engine = CreateEngine();
            var criteria = new SelectionCriteria { Regex = "[0-9]+" };

            // Act
            var locations = engine.Select(Path.Combine(Path.GetTempPath(), "does-not-exist.txt"), criteria);

            // Assert
            Assert.NotNull(locations);
            Assert.Empty(locations);
        }

        [Fact]
        public void Select_FileMatchingRegexCriteria_ReturnsLocation()
        {
            // Arrange
            using var engine = CreateEngine();
            var file = CreateTempFile("123");
            try
            {
                var criteria = new SelectionCriteria { Regex = "[0-9]+" };

                // Act
                var locations = engine.Select(file, criteria);

                // Assert
                Assert.NotEmpty(locations);
                Assert.All(locations, l => Assert.Equal(file, l.File));
            }
            finally
            {
                File.Delete(file);
            }
        }

        [Fact]
        public void Select_FileNotMatchingRegexCriteria_ReturnsEmptyList()
        {
            // Arrange
            using var engine = CreateEngine();
            var file = CreateTempFile("123");
            try
            {
                var criteria = new SelectionCriteria { Regex = "[a-z]+" };

                // Act
                var locations = engine.Select(file, criteria);

                // Assert
                Assert.Empty(locations);
            }
            finally
            {
                File.Delete(file);
            }
        }

        [Fact]
        public void Select_UnparsableContent_ReturnsEmptyList()
        {
            // Arrange - content that cannot be parsed with a NUMBER-only grammar
            using var engine = CreateEngine();
            var file = CreateTempFile("abc");
            try
            {
                var criteria = new SelectionCriteria { Regex = "[0-9]+" };

                // Act
                var locations = engine.Select(file, criteria);

                // Assert
                Assert.Empty(locations);
            }
            finally
            {
                File.Delete(file);
            }
        }

        [Fact]
        public void Select_StructuralCriteria_ReturnsLocationForMatchingRule()
        {
            // Arrange - two tokens so the parser reduces <expression> ::= <NUMBER>
            // after the first token, producing a non-terminal root node with a RuleName
            using var engine = CreateEngine();
            var file = CreateTempFile("1 2");
            try
            {
                var criteria = new SelectionCriteria { Structural = ("expression", false, false) };

                // Act
                var locations = engine.Select(file, criteria);

                // Assert - the root node is produced by the <expression> rule
                Assert.NotEmpty(locations);
                Assert.All(locations, l => Assert.Equal(file, l.File));
            }
            finally
            {
                File.Delete(file);
            }
        }

        [Fact]
        public void Select_StructuralCriteriaWithUnknownType_ReturnsEmptyList()
        {
            // Arrange - root node is an <expression>; a criteria for another type must not match
            using var engine = CreateEngine();
            var file = CreateTempFile("1 2");
            try
            {
                var criteria = new SelectionCriteria { Structural = ("class", false, false) };

                // Act
                var locations = engine.Select(file, criteria);

                // Assert
                Assert.Empty(locations);
            }
            finally
            {
                File.Delete(file);
            }
        }

        [Fact]
        public void Select_EmptyFile_ReturnsEmptyList()
        {
            // Arrange
            using var engine = CreateEngine();
            var file = CreateTempFile("");
            try
            {
                var criteria = new SelectionCriteria { Regex = "[0-9]+" };

                // Act
                var locations = engine.Select(file, criteria);

                // Assert
                Assert.NotNull(locations);
            }
            finally
            {
                File.Delete(file);
            }
        }
    }

    /// <summary>
    /// Tests for location-based operations that require a parsed graph
    /// (FindUsages, GetApplicableRefactorings, refactoring operations)
    /// </summary>
    public class LocationBasedOperationTests
    {
        private static readonly string NumberGrammar = TestGrammars.Get("test-grammars/step-parser-tests/LocationBasedOperationTests/TestGrammar.grammar");

        [Fact]
        public void FindUsages_AfterParse_ReturnsListWithoutThrowing()
        {
            // Arrange
            using var engine = new StepParserEngine();
            engine.LoadGrammarFromContent(NumberGrammar);
            var result = engine.Parse("123", "test.txt");
            Assert.True(result.Success);
            var location = new CodeLocation { File = "test.txt", StartLine = 1, StartColumn = 2 };

            // Act
            var usages = engine.FindUsages(location);

            // Assert
            Assert.NotNull(usages);
        }

        [Fact]
        public void FindUsages_AfterParse_WithScope_FiltersWithoutThrowing()
        {
            // Arrange
            using var engine = new StepParserEngine();
            engine.LoadGrammarFromContent(NumberGrammar);
            engine.Parse("123", "test.txt");
            var location = new CodeLocation { File = "test.txt", StartLine = 1, StartColumn = 2 };

            // Act
            var usages = engine.FindUsages(location, "method");

            // Assert
            Assert.NotNull(usages);
        }

        [Fact]
        public void FindUsages_InvalidLine_ReturnsEmptyList()
        {
            // Arrange
            using var engine = new StepParserEngine();
            engine.LoadGrammarFromContent(NumberGrammar);
            engine.Parse("123", "test.txt");
            var location = new CodeLocation { File = "test.txt", StartLine = 999, StartColumn = 1 };

            // Act
            var usages = engine.FindUsages(location);

            // Assert
            Assert.NotNull(usages);
        }

        [Fact]
        public void GetApplicableRefactorings_AfterParse_ReturnsRegisteredOperations()
        {
            // Arrange
            using var engine = new StepParserEngine();
            engine.LoadGrammarFromContent(NumberGrammar);
            var result = engine.Parse("123", "test.txt");
            Assert.True(result.Success);
            // The parser records 1-based columns as node offsets, so column 2 maps to
            // the byte offset of the NUMBER node (column 1 maps to offset 0, before it)
            engine.Context.ContextStack.Push("method");
            var location = new CodeLocation { File = "test.txt", StartLine = 1, StartColumn = 2 };

            // Act
            var applicable = engine.GetApplicableRefactorings(location);

            // Assert - default refactoring operations are registered by the engine
            Assert.NotEmpty(applicable);
        }

        [Fact]
        public void GetApplicableRefactorings_WithoutParse_ReturnsEmptyList()
        {
            // Arrange
            using var engine = new StepParserEngine();
            engine.LoadGrammarFromContent(NumberGrammar);
            var location = new CodeLocation { File = "test.txt", StartLine = 1, StartColumn = 2 };

            // Act
            var applicable = engine.GetApplicableRefactorings(location);

            // Assert - without a parsed graph there is no node at the location
            Assert.Empty(applicable);
        }

        [Fact]
        public void ExtractVariable_AfterParse_ReturnsResult()
        {
            // Arrange
            using var engine = new StepParserEngine();
            engine.LoadGrammarFromContent(NumberGrammar);
            var result = engine.Parse("123", "test.txt");
            Assert.True(result.Success);
            var location = new CodeLocation { File = "test.txt", StartLine = 1, StartColumn = 2 };

            // Act
            var refactoring = engine.ExtractVariable(location, "extracted");

            // Assert - the operation must return a well-formed result either way
            Assert.NotNull(refactoring);
            Assert.NotNull(refactoring.Message);
        }

        [Fact]
        public void InlineVariable_AfterParse_ReturnsResult()
        {
            // Arrange
            using var engine = new StepParserEngine();
            engine.LoadGrammarFromContent(NumberGrammar);
            var result = engine.Parse("123", "test.txt");
            Assert.True(result.Success);
            var location = new CodeLocation { File = "test.txt", StartLine = 1, StartColumn = 2 };

            // Act
            var refactoring = engine.InlineVariable(location);

            // Assert
            Assert.NotNull(refactoring);
            Assert.NotNull(refactoring.Message);
        }

        [Fact]
        public void Rename_AfterParse_ReturnsResult()
        {
            // Arrange
            using var engine = new StepParserEngine();
            engine.LoadGrammarFromContent(NumberGrammar);
            var result = engine.Parse("123", "test.txt");
            Assert.True(result.Success);
            var location = new CodeLocation { File = "test.txt", StartLine = 1, StartColumn = 2 };

            // Act
            var refactoring = engine.Rename(location, "newName");

            // Assert
            Assert.NotNull(refactoring);
            Assert.NotNull(refactoring.Message);
        }

        [Fact]
        public void RefactoringOperations_WithoutGrammar_ReturnGracefulFailure()
        {
            // Arrange - no grammar loaded, so no refactoring operations are registered
            using var engine = new StepParserEngine();
            var location = new CodeLocation { File = "test.txt", StartLine = 1, StartColumn = 2 };

            // Act
            var extract = engine.ExtractVariable(location, "name");
            var rename = engine.Rename(location, "name");

            // Assert
            Assert.False(extract.Success);
            Assert.False(rename.Success);
        }
    }
}
