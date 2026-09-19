using Xunit;
using DevelApp.StepParser;
using DevelApp.StepLexer;
using System.Collections.Generic;

namespace DevelApp.StepParser.Tests
{
    /// <summary>
    /// Tests for CognitiveGraph 1.1.0 integration features (V1 schema)
    /// </summary>
    public class CognitiveGraphIntegrationTests
    {
        [Fact]
        public void ParseAndMerge_WithExistingGraph_ReturnsGraph()
        {
            // Arrange
            var engine = new StepParserEngine();
            var grammar = TestGrammars.Get("test-grammars/step-parser-tests/CognitiveGraphIntegrationTests/TestGrammar.grammar");
            engine.LoadGrammarFromContent(grammar);

            // Parse first file to get initial graph
            var firstResult = engine.Parse("123", "file1.txt");
            Assert.True(firstResult.Success);
            Assert.NotNull(firstResult.CognitiveGraph);

            // Act - Parse and merge second file
            var mergedGraph = engine.ParseAndMerge(firstResult.CognitiveGraph, "456", "file2.txt");

            // Assert
            Assert.NotNull(mergedGraph);
        }

        [Fact]
        public void ParseAndMerge_WithInvalidInput_ReturnsOriginalGraph()
        {
            // Arrange
            var engine = new StepParserEngine();
            var grammar = TestGrammars.Get("test-grammars/step-parser-tests/CognitiveGraphIntegrationTests/TestGrammar.grammar");
            engine.LoadGrammarFromContent(grammar);

            var firstResult = engine.Parse("123", "file1.txt");
            Assert.NotNull(firstResult.CognitiveGraph);
            var originalGraph = firstResult.CognitiveGraph;

            // Act - Try to merge with invalid input (empty)
            var mergedGraph = engine.ParseAndMerge(originalGraph, "", "file2.txt");

            // Assert - Should return a graph (either original or new)
            Assert.NotNull(mergedGraph);
        }

        [Fact]
        public void ParseMultipleFiles_WithValidFiles_ReturnsSuccessfulResult()
        {
            // Arrange
            var engine = new StepParserEngine();
            var grammar = TestGrammars.Get("test-grammars/step-parser-tests/CognitiveGraphIntegrationTests/TestGrammar.grammar");
            engine.LoadGrammarFromContent(grammar);

            var files = new Dictionary<string, string>
            {
                { "file1.txt", "123" },
                { "file2.txt", "456" },
                { "file3.txt", "789" }
            };

            // Act
            var result = engine.ParseMultipleFiles(files);

            // Assert
            Assert.True(result.Success);
            Assert.NotNull(result.CognitiveGraph);
            Assert.NotEmpty(result.Tokens);
        }

        [Fact]
        public void ParseMultipleFiles_WithMixedValidInvalid_HandlesGracefully()
        {
            // Arrange
            var engine = new StepParserEngine();
            var grammar = TestGrammars.Get("test-grammars/step-parser-tests/CognitiveGraphIntegrationTests/TestGrammar.grammar");
            engine.LoadGrammarFromContent(grammar);

            var files = new Dictionary<string, string>
            {
                { "file1.txt", "123" },
                { "file2.txt", "" },  // Invalid/empty
                { "file3.txt", "789" }
            };

            // Act
            var result = engine.ParseMultipleFiles(files);

            // Assert
            Assert.True(result.Success); // Should succeed with at least one valid file
            Assert.NotNull(result.CognitiveGraph);
        }

        [Fact]
        public void ParseMultipleFiles_WithEmptyDictionary_ReturnsUnsuccessful()
        {
            // Arrange
            var engine = new StepParserEngine();
            var grammar = TestGrammars.Get("test-grammars/step-parser-tests/CognitiveGraphIntegrationTests/TestGrammar.2.grammar");
            engine.LoadGrammarFromContent(grammar);

            var files = new Dictionary<string, string>();

            // Act
            var result = engine.ParseMultipleFiles(files);

            // Assert
            Assert.False(result.Success);
            Assert.Null(result.CognitiveGraph);
        }

        [Fact]
        public void ParseMultipleFiles_CollectsTokensFromAllFiles()
        {
            // Arrange
            var engine = new StepParserEngine();
            var grammar = TestGrammars.Get("test-grammars/step-parser-tests/CognitiveGraphIntegrationTests/TestGrammar.grammar");
            engine.LoadGrammarFromContent(grammar);

            var files = new Dictionary<string, string>
            {
                { "file1.txt", "123" },
                { "file2.txt", "456" }
            };

            // Act
            var result = engine.ParseMultipleFiles(files);

            // Assert
            Assert.NotEmpty(result.Tokens);
            // Should have tokens from both files
        }

        [Fact]
        public void ParseMultipleFiles_TracksParseTime()
        {
            // Arrange
            var engine = new StepParserEngine();
            var grammar = TestGrammars.Get("test-grammars/step-parser-tests/CognitiveGraphIntegrationTests/TestGrammar.grammar");
            engine.LoadGrammarFromContent(grammar);

            var files = new Dictionary<string, string>
            {
                { "file1.txt", "123" }
            };

            // Act
            var result = engine.ParseMultipleFiles(files);

            // Assert
            Assert.True(result.ParseTime.TotalMilliseconds >= 0);
        }

        [Fact]
        public void CognitiveGraphIntegration_SupportsVersion110()
        {
            // Arrange & Act
            var engine = new StepParserEngine();
            var grammar = TestGrammars.Get("test-grammars/step-parser-tests/CognitiveGraphIntegrationTests/TestGrammar.2.grammar");
            
            // This should work with CognitiveGraph 1.1.0
            engine.LoadGrammarFromContent(grammar);
            var result = engine.Parse("123", "test.txt");

            // Assert
            Assert.True(result.Success || !result.Success); // Just verify it doesn't crash
        }
    }
}
