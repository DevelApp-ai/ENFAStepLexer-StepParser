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

        [Fact]
        public void InternalStackOps_MatchStackSemantics()
        {
            // Arrange - exercise the O(1) internal accessors used by the
            // parser engine alongside the public Stack view
            var path = new ParserPath(1);
            path.PushSymbol(MakeRef(10, "A"));
            path.PushSymbol(MakeRef(20, "B"));
            path.PushSymbol(MakeRef(30, "C"));

            // Act
            var topFirst = path.StackTopFirst.Select(n => n.RuleName).ToList();
            var depth = path.StackDepth;
            var peeked = path.PeekSymbol();
            var popped = path.PopSymbol();

            // Assert - top-first enumeration, matching Stack<T> semantics
            Assert.Equal(new[] { "C", "B", "A" }, topFirst);
            Assert.Equal(3, depth);
            Assert.Equal("C", peeked.RuleName);
            Assert.Equal("C", popped.RuleName);
            Assert.Equal(2, path.StackDepth);

            // The public view stays consistent with the internal spine
            Assert.Equal(new[] { "B", "A" }, path.ParseStack.Select(n => n.RuleName));
        }

        [Fact]
        public void StackSignature_IsEqualForEqualStacksAndOrderSensitive()
        {
            // Arrange - two independently built stacks with identical contents
            var path1 = new ParserPath(1);
            path1.PushSymbol(MakeRef(10, "A"));
            path1.PushSymbol(MakeRef(20, "B"));

            var path2 = new ParserPath(2);
            path2.PushSymbol(MakeRef(110, "A"));
            path2.PushSymbol(MakeRef(120, "B"));

            // Same contents in a different order
            var path3 = new ParserPath(3);
            path3.PushSymbol(MakeRef(20, "B"));
            path3.PushSymbol(MakeRef(10, "A"));

            // Act
            var signature1 = path1.StackSignature;
            var signature2 = path2.StackSignature;
            var signature3 = path3.StackSignature;

            // Assert - identical sequences share a signature regardless of
            // node identity; different order yields a different signature
            Assert.Equal(signature1, signature2);
            Assert.NotEqual(signature1, signature3);
        }

        [Fact]
        public void StackSignature_TracksPushAndPopOperations()
        {
            // Arrange
            var path = new ParserPath(1);
            path.PushSymbol(MakeRef(10, "A"));
            var withA = path.StackSignature;
            path.PushSymbol(MakeRef(20, "B"));
            var withAB = path.StackSignature;
            path.PopSymbol();
            var afterPop = path.StackSignature;

            // Assert - pushing changes the signature, popping restores the
            // signature of the previous prefix
            Assert.NotEqual(withA, withAB);
            Assert.Equal(withA, afterPop);
        }

        [Fact]
        public void Clone_OfLargeStack_PreservesAllElements()
        {
            // Arrange - a deep stack built through internal ops; cloning must
            // stay correct regardless of structural sharing
            var path = new ParserPath(1);
            const int depth = 10_000;
            for (uint i = 0; i < depth; i++)
            {
                path.PushSymbol(MakeRef(i, "N" + i));
            }

            // Act
            var clone = path.Clone(2);

            // Assert
            Assert.Equal(depth, clone.StackDepth);
            Assert.Equal("N" + (depth - 1), clone.PeekSymbol().RuleName);
            Assert.Equal(depth, clone.ParseStack.Count);
            Assert.Equal("N" + (depth - 1), clone.ParseStack.Peek().RuleName);
            Assert.Equal("N0", clone.ParseStack.Last().RuleName);
        }

        [Fact]
        public void NodeOffsets_InternalAddMatchesListView()
        {
            // Arrange
            var path = new ParserPath(1);
            path.AddNodeOffset(10);
            path.AddNodeOffset(20);
            path.AddNodeOffset(30);

            // Act
            var clone = path.Clone(2);
            clone.AddNodeOffset(40);

            // Assert - oldest-first order is preserved, and the clone is
            // independent of the original
            Assert.Equal(new uint[] { 10, 20, 30 }, path.NodeOffsets);
            Assert.Equal(new uint[] { 10, 20, 30, 40 }, clone.NodeOffsets);
        }
    }
}
