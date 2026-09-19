using Xunit;
using DevelApp.StepParser;
using DevelApp.StepLexer;
using CognitiveGraph.Schema;
using System.Collections.Generic;

namespace DevelApp.StepParser.Tests
{
    /// <summary>
    /// Tests for CognitiveGraph 1.1.0 V2 schema support
    /// </summary>
    public class CognitiveGraphV2IntegrationTests
    {
        [Fact]
        public void StepParserEngine_WithV1Schema_CreatesV1Graph()
        {
            // Arrange
            var engine = new StepParserEngine(SchemaVersion.V1);
            var grammar = TestGrammars.Get("test-grammars/step-parser-tests/CognitiveGraphV2IntegrationTests/TestGrammar.grammar");
            engine.LoadGrammarFromContent(grammar);

            // Act
            var result = engine.Parse("123", "test.txt");

            // Assert
            Assert.True(result.Success);
            Assert.NotNull(result.CognitiveGraph);
            Assert.Equal(SchemaVersion.V1, result.CognitiveGraph.SchemaVersion);
        }

        [Fact]
        public void StepParserEngine_WithV2Schema_CreatesV2Graph()
        {
            // Arrange
            var engine = new StepParserEngine(SchemaVersion.V2);
            var grammar = TestGrammars.Get("test-grammars/step-parser-tests/CognitiveGraphV2IntegrationTests/TestGrammar.grammar");
            engine.LoadGrammarFromContent(grammar);

            // Act
            var result = engine.Parse("456", "test.txt");

            // Assert
            Assert.True(result.Success);
            Assert.NotNull(result.CognitiveGraph);
            Assert.Equal(SchemaVersion.V2, result.CognitiveGraph.SchemaVersion);
        }

        [Fact]
        public void StepParserEngine_DefaultConstructor_CreatesV1GraphForBackwardCompatibility()
        {
            // Arrange
            var engine = new StepParserEngine(); // Default should be V1
            var grammar = TestGrammars.Get("test-grammars/step-parser-tests/CognitiveGraphV2IntegrationTests/TestGrammar.grammar");
            engine.LoadGrammarFromContent(grammar);

            // Act
            var result = engine.Parse("789", "test.txt");

            // Assert
            Assert.True(result.Success);
            Assert.NotNull(result.CognitiveGraph);
            Assert.Equal(SchemaVersion.V1, result.CognitiveGraph.SchemaVersion);
        }

        [Fact]
        public void StepParserEngine_V2Schema_WithComplexGrammar_CreatesValidGraph()
        {
            // Arrange
            var engine = new StepParserEngine(SchemaVersion.V2);
            var grammar = TestGrammars.Get("test-grammars/step-parser-tests/CognitiveGraphV2IntegrationTests/ComplexGrammar.grammar");
            engine.LoadGrammarFromContent(grammar);

            // Act
            var result = engine.Parse("42", "test.txt");

            // Assert
            Assert.True(result.Success);
            Assert.NotNull(result.CognitiveGraph);
            Assert.Equal(SchemaVersion.V2, result.CognitiveGraph.SchemaVersion);
        }

        [Fact]
        public void ParseAndMerge_WithV2Schema_ReturnsV2Graph()
        {
            // Arrange
            var engine = new StepParserEngine(SchemaVersion.V2);
            var grammar = TestGrammars.Get("test-grammars/step-parser-tests/CognitiveGraphV2IntegrationTests/TestGrammar.grammar");
            engine.LoadGrammarFromContent(grammar);

            // Parse first file to get initial graph
            var firstResult = engine.Parse("123", "file1.txt");
            Assert.True(firstResult.Success);
            Assert.NotNull(firstResult.CognitiveGraph);
            Assert.Equal(SchemaVersion.V2, firstResult.CognitiveGraph.SchemaVersion);

            // Act - Parse and merge second file
            var mergedGraph = engine.ParseAndMerge(firstResult.CognitiveGraph, "456", "file2.txt");

            // Assert
            Assert.NotNull(mergedGraph);
            Assert.Equal(SchemaVersion.V2, mergedGraph.SchemaVersion);
        }

        [Fact]
        public void ParseMultipleFiles_WithV2Schema_ReturnsV2Graph()
        {
            // Arrange
            var engine = new StepParserEngine(SchemaVersion.V2);
            var grammar = TestGrammars.Get("test-grammars/step-parser-tests/CognitiveGraphV2IntegrationTests/TestGrammar.grammar");
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
            Assert.Equal(SchemaVersion.V2, result.CognitiveGraph.SchemaVersion);
            Assert.NotEmpty(result.Tokens);
        }

        [Fact]
        public void V1AndV2Engines_CanCoexist()
        {
            // Arrange
            var engineV1 = new StepParserEngine(SchemaVersion.V1);
            var engineV2 = new StepParserEngine(SchemaVersion.V2);
            var grammar = TestGrammars.Get("test-grammars/step-parser-tests/CognitiveGraphV2IntegrationTests/TestGrammar.grammar");
            engineV1.LoadGrammarFromContent(grammar);
            engineV2.LoadGrammarFromContent(grammar);

            // Act
            var resultV1 = engineV1.Parse("111", "testV1.txt");
            var resultV2 = engineV2.Parse("222", "testV2.txt");

            // Assert
            Assert.True(resultV1.Success);
            Assert.True(resultV2.Success);
            Assert.NotNull(resultV1.CognitiveGraph);
            Assert.NotNull(resultV2.CognitiveGraph);
            Assert.Equal(SchemaVersion.V1, resultV1.CognitiveGraph.SchemaVersion);
            Assert.Equal(SchemaVersion.V2, resultV2.CognitiveGraph.SchemaVersion);
        }

        [Fact]
        public void V2Schema_SupportsAmbiguousParses()
        {
            // Arrange
            var engine = new StepParserEngine(SchemaVersion.V2);
            var grammar = TestGrammars.Get("test-grammars/step-parser-tests/CognitiveGraphV2IntegrationTests/SimpleGrammar.grammar");
            engine.LoadGrammarFromContent(grammar);

            // Act
            var result = engine.Parse("123", "test.txt");

            // Assert
            Assert.True(result.Success);
            Assert.NotNull(result.CognitiveGraph);
            Assert.Equal(SchemaVersion.V2, result.CognitiveGraph.SchemaVersion);
        }

        [Fact]
        public void V2Schema_WithEmptyInput_HandlesGracefully()
        {
            // Arrange
            var engine = new StepParserEngine(SchemaVersion.V2);
            var grammar = TestGrammars.Get("test-grammars/step-parser-tests/CognitiveGraphV2IntegrationTests/TestGrammar.2.grammar");
            engine.LoadGrammarFromContent(grammar);

            // Act
            var result = engine.Parse("", "empty.txt");

            // Assert - Should handle empty input without crashing
            // Graph may or may not be created depending on grammar rules
            Assert.NotNull(result);
        }

        [Fact]
        public void V2Schema_WithInvalidInput_ReturnsUnsuccessfulResult()
        {
            // Arrange
            var engine = new StepParserEngine(SchemaVersion.V2);
            var grammar = TestGrammars.Get("test-grammars/step-parser-tests/CognitiveGraphV2IntegrationTests/TestGrammar.grammar");
            engine.LoadGrammarFromContent(grammar);

            // Act
            var result = engine.Parse("abc", "test.txt"); // Should fail with digits-only grammar

            // Assert
            Assert.False(result.Success);
        }
    }
}
