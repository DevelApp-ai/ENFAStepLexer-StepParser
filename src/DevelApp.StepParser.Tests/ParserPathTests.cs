using System.Collections.Generic;
using System.Linq;
using DevelApp.StepLexer;
using Xunit;

namespace DevelApp.StepParser.Tests
{
    /// <summary>
    /// Tests for ParserPath cloning semantics used by the GLR path
    /// management in StepParser.
    /// </summary>
    public class ParserPathTests
    {
        private static GraphNodeRef MakeRef(uint offset, string ruleName)
        {
            return new GraphNodeRef(
                offset,
                (ushort)(offset + 1),
                100,
                ruleName,
                "value-" + offset,
                new CodeLocation("test.step", 1, (int)offset, 1, (int)offset));
        }

        [Fact]
        public void Clone_PreservesStackOrder()
        {
            // Arrange
            var path = new ParserPath(1);
            path.ParseStack.Push(MakeRef(10, "A"));
            path.ParseStack.Push(MakeRef(20, "B"));
            path.ParseStack.Push(MakeRef(30, "C"));

            // Act
            var clone = path.Clone(2);

            // Assert - Stack enumerates top to bottom in both paths
            Assert.Equal(new[] { "C", "B", "A" }, clone.ParseStack.Select(n => n.RuleName));
            Assert.Equal(new[] { "C", "B", "A" }, path.ParseStack.Select(n => n.RuleName));
            Assert.Equal(3, clone.ParseStack.Count);
        }

        [Fact]
        public void Clone_PopReturnsTopElementFirst()
        {
            // Arrange
            var path = new ParserPath(1);
            path.ParseStack.Push(MakeRef(10, "A"));
            path.ParseStack.Push(MakeRef(20, "B"));

            // Act
            var clone = path.Clone(2);
            var popped = clone.ParseStack.Pop();

            // Assert
            Assert.Equal("B", popped.RuleName);
            Assert.Single(clone.ParseStack);
            Assert.Equal(2, path.ParseStack.Count);
        }

        [Fact]
        public void Clone_IsIndependentOfOriginal()
        {
            // Arrange
            var path = new ParserPath(1);
            path.ParseStack.Push(MakeRef(10, "A"));
            var clone = path.Clone(2);

            // Act - mutate the clone only
            clone.ParseStack.Push(MakeRef(20, "B"));
            clone.NodeOffsets.Add(20);
            clone.TokenPosition = 5;

            // Assert
            Assert.Single(path.ParseStack);
            Assert.Empty(path.NodeOffsets);
            Assert.Equal(0, path.TokenPosition);
            Assert.Equal(2, clone.ParseStack.Count);
        }

        [Fact]
        public void Clone_CopiesPathMetadata()
        {
            // Arrange
            var path = new ParserPath(1)
            {
                TokenPosition = 7,
                CurrentState = "state-1",
                IsValid = false,
                Score = 2.5f
            };
            path.ParseStack.Push(MakeRef(10, "A"));
            path.NodeOffsets.Add(10);
            path.ActiveProductions.Add(new ProductionRule("rule", new List<string> { "A" }));
            path.State["key"] = "value";

            // Act
            var clone = path.Clone(42);

            // Assert
            Assert.Equal(42, clone.PathId);
            Assert.NotEqual(path.PathId, clone.PathId);
            Assert.Equal(7, clone.TokenPosition);
            Assert.Equal("state-1", clone.CurrentState);
            Assert.False(clone.IsValid);
            Assert.Equal(2.5f, clone.Score);
            Assert.Equal(path.NodeOffsets, clone.NodeOffsets);
            Assert.Single(clone.ActiveProductions);
            Assert.Equal("value", clone.State["key"]);
        }

        [Fact]
        public void Clone_OfEmptyPathYieldsEmptyStack()
        {
            // Arrange
            var path = new ParserPath(1);

            // Act
            var clone = path.Clone(2);

            // Assert
            Assert.Empty(clone.ParseStack);
            Assert.Empty(clone.NodeOffsets);
            Assert.Empty(clone.ActiveProductions);
            Assert.Empty(clone.State);
        }
    }
}
