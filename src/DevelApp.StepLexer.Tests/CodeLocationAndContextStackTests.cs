using Xunit;
using DevelApp.StepLexer;
using System.Collections.Generic;
using System.Linq;

namespace DevelApp.StepLexer.Tests
{
    /// <summary>
    /// Tests for CodeLocation and ContextStack (surgical operation support types)
    /// </summary>
    public class CodeLocationAndContextStackTests
    {
        [Fact]
        public void CodeLocation_DefaultConstructor_HasDefaultValues()
        {
            // Arrange & Act
            var location = new CodeLocation();

            // Assert
            Assert.Equal(string.Empty, location.File);
            Assert.Equal(0, location.StartLine);
            Assert.Equal(0, location.StartColumn);
            Assert.Equal(0, location.EndLine);
            Assert.Equal(0, location.EndColumn);
            Assert.Equal(string.Empty, location.Context);
        }

        [Fact]
        public void CodeLocation_FullConstructor_SetsAllProperties()
        {
            // Arrange & Act
            var location = new CodeLocation("test.cs", 1, 2, 3, 4, "method");

            // Assert
            Assert.Equal("test.cs", location.File);
            Assert.Equal(1, location.StartLine);
            Assert.Equal(2, location.StartColumn);
            Assert.Equal(3, location.EndLine);
            Assert.Equal(4, location.EndColumn);
            Assert.Equal("method", location.Context);
        }

        [Fact]
        public void CodeLocation_FullConstructor_ContextDefaultsToEmpty()
        {
            // Arrange & Act
            var location = new CodeLocation("test.cs", 1, 1, 1, 1);

            // Assert
            Assert.Equal(string.Empty, location.Context);
        }

        [Fact]
        public void CodeLocation_ToString_FormatsFileAndPosition()
        {
            // Arrange
            var location = new CodeLocation("test.cs", 1, 2, 3, 4);

            // Act
            var result = location.ToString();

            // Assert
            Assert.Equal("[test.cs 1:2-3:4]", result);
        }

        [Fact]
        public void CodeLocation_Properties_AreSettable()
        {
            // Arrange
            var location = new CodeLocation();

            // Act
            location.File = "other.cs";
            location.StartLine = 10;
            location.StartColumn = 20;
            location.EndLine = 30;
            location.EndColumn = 40;
            location.Context = "class";

            // Assert
            Assert.Equal("other.cs", location.File);
            Assert.Equal(10, location.StartLine);
            Assert.Equal(20, location.StartColumn);
            Assert.Equal(30, location.EndLine);
            Assert.Equal(40, location.EndColumn);
            Assert.Equal("class", location.Context);
        }

        [Fact]
        public void CodeLocation_ImplementsICodeLocation()
        {
            // Arrange & Act
            ICodeLocation location = new CodeLocation("test.cs", 1, 1, 2, 2);

            // Assert
            Assert.Equal("test.cs", location.File);
            Assert.Equal(1, location.StartLine);
            Assert.Equal(2, location.EndLine);
        }

        [Fact]
        public void ContextStack_Empty_PopReturnsNull()
        {
            // Arrange
            var stack = new ContextStack();

            // Act
            var result = stack.Pop();

            // Assert
            Assert.Null(result);
        }

        [Fact]
        public void ContextStack_Empty_CurrentReturnsNull()
        {
            // Arrange
            var stack = new ContextStack();

            // Act
            var result = stack.Current();

            // Assert
            Assert.Null(result);
        }

        [Fact]
        public void ContextStack_Empty_DepthIsZero()
        {
            // Arrange
            var stack = new ContextStack();

            // Act & Assert
            Assert.Equal(0, stack.Depth());
        }

        [Fact]
        public void ContextStack_Empty_GetPathIsEmpty()
        {
            // Arrange
            var stack = new ContextStack();

            // Act
            var path = stack.GetPath();

            // Assert
            Assert.Empty(path);
        }

        [Fact]
        public void ContextStack_Empty_InScopeIsFalse()
        {
            // Arrange
            var stack = new ContextStack();

            // Act & Assert
            Assert.False(stack.InScope("method"));
            Assert.False(stack.Contains("method"));
        }

        [Fact]
        public void ContextStack_PushThenPop_ReturnsContextAndDecreasesDepth()
        {
            // Arrange
            var stack = new ContextStack();

            // Act
            stack.Push("method");
            var popped = stack.Pop();

            // Assert
            Assert.Equal("method", popped);
            Assert.Equal(0, stack.Depth());
            Assert.Null(stack.Current());
        }

        [Fact]
        public void ContextStack_PushMultiple_CurrentReturnsTop()
        {
            // Arrange
            var stack = new ContextStack();

            // Act
            stack.Push("class");
            stack.Push("method");

            // Assert
            Assert.Equal("method", stack.Current());
            Assert.Equal(2, stack.Depth());
        }

        [Fact]
        public void ContextStack_GetPath_ReturnsRootToCurrentOrder()
        {
            // Arrange
            var stack = new ContextStack();
            stack.Push("class");
            stack.Push("method");
            stack.Push("if");

            // Act
            var path = stack.GetPath();

            // Assert
            Assert.Equal(new[] { "class", "method", "if" }, path);
        }

        [Fact]
        public void ContextStack_InScope_FindsNestedContext()
        {
            // Arrange
            var stack = new ContextStack();
            stack.Push("class");
            stack.Push("method");

            // Act & Assert
            Assert.True(stack.InScope("class"));
            Assert.True(stack.InScope("method"));
            Assert.False(stack.InScope("if"));
        }

        [Fact]
        public void ContextStack_Contains_FindsAnyFrame()
        {
            // Arrange
            var stack = new ContextStack();
            stack.Push("class");
            stack.Push("method");

            // Act & Assert
            Assert.True(stack.Contains("class"));
            Assert.True(stack.Contains("method"));
            Assert.False(stack.Contains("statement"));
        }

        [Fact]
        public void ContextStack_PopUntilEmpty_ThenReturnsNull()
        {
            // Arrange
            var stack = new ContextStack();
            stack.Push("a");
            stack.Push("b");

            // Act
            Assert.Equal("b", stack.Pop());
            Assert.Equal("a", stack.Pop());

            // Assert
            Assert.Null(stack.Pop());
            Assert.Equal(0, stack.Depth());
        }

        [Fact]
        public void ContextStack_PushWithIdentifier_DoesNotAffectContextName()
        {
            // Arrange
            var stack = new ContextStack();

            // Act
            stack.Push("method", "MyMethod");

            // Assert
            Assert.Equal("method", stack.Current());
            Assert.Equal(1, stack.Depth());
        }

        [Fact]
        public void ContextStack_ImplementsIContextStack()
        {
            // Arrange & Act
            IContextStack stack = new ContextStack();
            stack.Push("class");

            // Assert
            Assert.Equal("class", stack.Current());
            Assert.True(stack.InScope("class"));
        }
    }
}
