using Xunit;
using DevelApp.StepParser;
using DevelApp.StepLexer;
using System;
using System.Diagnostics;

namespace DevelApp.StepParser.Tests
{
    /// <summary>
    /// Tests for GLR parser stability and hang avoidance (TDS Section 4.1)
    /// </summary>
    public class GLRParserStabilityTests
    {
        [Fact]
        public void Parse_WithLeftRecursion_DoesNotHang()
        {
            // Arrange
            var engine = new StepParserEngine();
            var grammar = TestGrammars.Get("test-grammars/step-parser-tests/GLRParserStabilityTests/LeftRecursiveTest.grammar");
            engine.LoadGrammarFromContent(grammar);

            // Act - Set a timeout to detect hangs
            var sw = Stopwatch.StartNew();
            var result = engine.Parse("1+2+3", "test.txt");
            sw.Stop();

            // Assert - Should complete within reasonable time (not hang)
            Assert.True(sw.ElapsedMilliseconds < 5000, $"Parser took {sw.ElapsedMilliseconds}ms - possible hang");
        }

        [Fact]
        public void Parse_WithInfiniteLoop_TerminatesGracefully()
        {
            // Arrange
            var engine = new StepParserEngine();
            var grammar = TestGrammars.Get("test-grammars/step-parser-tests/GLRParserStabilityTests/InfiniteLoopTest.grammar");
            engine.LoadGrammarFromContent(grammar);

            // Act - Should terminate due to safety limits
            var sw = Stopwatch.StartNew();
            var result = engine.Parse("123", "test.txt");
            sw.Stop();

            // Assert - Should not hang indefinitely
            Assert.True(sw.ElapsedMilliseconds < 10000, "Parser should terminate with safety limits");
            // The parse may fail or succeed, but it should not hang
        }

        [Fact]
        public void Parse_WithNoProgress_DetectsAndTerminates()
        {
            // Arrange
            var engine = new StepParserEngine();
            var grammar = TestGrammars.Get("test-grammars/step-parser-tests/GLRParserStabilityTests/NoProgressTest.grammar");
            engine.LoadGrammarFromContent(grammar);

            // Act - Parse empty input which makes no progress
            var sw = Stopwatch.StartNew();
            var result = engine.Parse("", "test.txt");
            sw.Stop();

            // Assert - Should detect no progress and terminate quickly
            Assert.True(sw.ElapsedMilliseconds < 2000, "Parser should detect no progress quickly");
        }

        [Fact]
        public void Parse_WithAmbiguousGrammar_HandlesCorrectly()
        {
            // Arrange
            var engine = new StepParserEngine();
            var grammar = TestGrammars.Get("test-grammars/step-parser-tests/GLRParserStabilityTests/AmbiguousTest.grammar");
            engine.LoadGrammarFromContent(grammar);

            // Act
            var sw = Stopwatch.StartNew();
            var result = engine.Parse("1+2", "test.txt");
            sw.Stop();

            // Assert - Should handle ambiguity without hanging
            Assert.True(sw.ElapsedMilliseconds < 5000, "Ambiguous parse should not hang");
        }

        [Fact]
        public void Parse_WithComplexNesting_DoesNotExceedLimits()
        {
            // Arrange
            var engine = new StepParserEngine();
            var grammar = TestGrammars.Get("test-grammars/step-parser-tests/GLRParserStabilityTests/NestedTest.grammar");
            engine.LoadGrammarFromContent(grammar);

            // Act - Deeply nested expression
            var input = new string('(', 50) + "123" + new string(')', 50);
            var sw = Stopwatch.StartNew();
            var result = engine.Parse(input, "test.txt");
            sw.Stop();

            // Assert - Should handle within reasonable time
            Assert.True(sw.ElapsedMilliseconds < 10000, "Deeply nested parse should complete");
        }

        [Fact]
        public void Parse_WithLargeInput_CompletesWithinTimeout()
        {
            // Arrange
            var engine = new StepParserEngine();
            var grammar = TestGrammars.Get("test-grammars/step-parser-tests/GLRParserStabilityTests/LargeInputTest.grammar");
            engine.LoadGrammarFromContent(grammar);

            // Act - Large input
            var input = new string('9', 10000);
            var sw = Stopwatch.StartNew();
            var result = engine.Parse(input, "test.txt");
            sw.Stop();

            // Assert - Should complete within reasonable time
            Assert.True(sw.ElapsedMilliseconds < 30000, "Large input should be processed efficiently");
        }

        [Fact]
        public void Parse_MultipleInvocations_RemainStable()
        {
            // Arrange
            var engine = new StepParserEngine();
            var grammar = TestGrammars.Get("test-grammars/step-parser-tests/GLRParserStabilityTests/StabilityTest.grammar");
            engine.LoadGrammarFromContent(grammar);

            // Act - Multiple parse invocations
            for (int i = 0; i < 10; i++)
            {
                var sw = Stopwatch.StartNew();
                var result = engine.Parse(i.ToString(), "test.txt");
                sw.Stop();

                // Assert - Each invocation should complete quickly
                Assert.True(sw.ElapsedMilliseconds < 1000, $"Iteration {i} took too long");
            }
        }

        [Fact]
        public void Parse_WithMalformedInput_TerminatesGracefully()
        {
            // Arrange
            var engine = new StepParserEngine();
            var grammar = TestGrammars.Get("test-grammars/step-parser-tests/GLRParserStabilityTests/MalformedTest.grammar");
            engine.LoadGrammarFromContent(grammar);

            // Act - Malformed input that doesn't match grammar
            var sw = Stopwatch.StartNew();
            var result = engine.Parse("abc+++def", "test.txt");
            sw.Stop();

            // Assert - Should terminate gracefully even with malformed input
            Assert.True(sw.ElapsedMilliseconds < 5000, "Malformed input should not cause hang");
            Assert.False(result.Success); // Expected to fail
        }
    }

    /// <summary>
    /// Tests for semantic action handler system (TDS Section 5.2)
    /// </summary>
    public class SemanticActionHandlerTests
    {
        [Fact]
        public void RegisterActionHandler_CustomHandler_IsRegistered()
        {
            // Arrange
            var engine = new StepParserEngine();
            var customHandler = new DefaultSemanticActionHandler();

            // Act
            engine.RegisterActionHandler(customHandler);
            var retrieved = engine.GetActionHandler("default");

            // Assert
            Assert.NotNull(retrieved);
            Assert.Equal("default", retrieved.Name);
        }

        [Fact]
        public void GetActionHandler_WithNullName_ReturnsDefault()
        {
            // Arrange
            var engine = new StepParserEngine();

            // Act
            var handler = engine.GetActionHandler(null);

            // Assert
            Assert.NotNull(handler);
            Assert.Equal("default", handler.Name);
        }

        [Fact]
        public void GetActionHandler_WithUnknownName_ReturnsDefault()
        {
            // Arrange
            var engine = new StepParserEngine();

            // Act
            var handler = engine.GetActionHandler("nonexistent");

            // Assert
            Assert.NotNull(handler);
            Assert.Equal("default", handler.Name);
        }

        [Fact]
        public void ActionContext_Properties_InitializeCorrectly()
        {
            // Arrange & Act
            var context = new ActionContext
            {
                ActionText = "test action",
                RuleName = "test_rule",
                Location = new CodeLocation { StartLine = 1, StartColumn = 5 }
            };

            // Assert
            Assert.Equal("test action", context.ActionText);
            Assert.Equal("test_rule", context.RuleName);
            Assert.Equal(1, context.Location.StartLine);
            Assert.NotNull(context.Properties);
        }

        [Fact]
        public void DefaultSemanticActionHandler_Execute_CompletesSuccessfully()
        {
            // Arrange
            var handler = new DefaultSemanticActionHandler();
            var context = new ActionContext { ActionText = "test" };

            // Act
            handler.Execute(context);

            // Assert
            Assert.True(context.Properties.ContainsKey("ActionExecuted"));
            Assert.True((bool)context.Properties["ActionExecuted"]);
        }

        [Fact]
        public void RoslynSemanticActionHandler_Execute_MarksAsExecuted()
        {
            // Arrange
            var handler = new RoslynSemanticActionHandler();
            var context = new ActionContext { ActionText = "var x = 1;" };

            // Act
            handler.Execute(context);

            // Assert
            Assert.True(context.Properties.ContainsKey("ActionExecuted"));
            Assert.Equal("csharp_script", context.Properties["ActionHandler"]);
        }

        [Fact]
        public void RoslynSemanticActionHandler_Validate_RejectsEmptyAction()
        {
            // Arrange
            var handler = new RoslynSemanticActionHandler();

            // Act
            var error = handler.Validate("");

            // Assert
            Assert.NotNull(error);
            Assert.Contains("empty", error.ToLower());
        }
    }
}
