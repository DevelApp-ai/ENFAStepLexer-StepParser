using Xunit;
using DevelApp.StepLexer;
using System;

namespace DevelApp.StepLexer.Tests
{
    /// <summary>
    /// Tests for the ENFA exception hierarchy in DevelApp.StepLexer
    /// </summary>
    public class ExceptionTests
    {
        [Fact]
        public void ENFA_Exception_SingleArgument_PrefixesMessageWithCallerInfo()
        {
            // Arrange & Act
            var exception = new ENFA_Exception("something failed");

            // Assert - format is "{message}: {caller} ({file}: {line})"
            Assert.StartsWith("something failed: ", exception.Message);
            Assert.Contains(nameof(ENFA_Exception_SingleArgument_PrefixesMessageWithCallerInfo), exception.Message);
        }

        [Fact]
        public void ENFA_Exception_WithInnerException_PreservesInner()
        {
            // Arrange
            var inner = new InvalidOperationException("root cause");

            // Act
            var exception = new ENFA_Exception("wrapper", inner);

            // Assert
            Assert.StartsWith("wrapper: ", exception.Message);
            Assert.Same(inner, exception.InnerException);
        }

        [Fact]
        public void ENFA_Exception_IsRegularException()
        {
            // Arrange & Act
            var exception = new ENFA_Exception("message");

            // Assert
            Assert.IsAssignableFrom<Exception>(exception);
            Assert.Equal("message", exception.Message.Split(':')[0]);
        }

        [Fact]
        public void ENFA_RegexBuild_Exception_MessageOnly_PrefixesMessage()
        {
            // Arrange & Act
            var exception = new ENFA_RegexBuild_Exception("bad pattern");

            // Assert
            Assert.StartsWith("bad pattern: ", exception.Message);
        }

        [Fact]
        public void ENFA_RegexBuild_Exception_WithTerminalDetails_FormatsTerminalContext()
        {
            // Arrange & Act
            var exception = new ENFA_RegexBuild_Exception("NUMBER", "/[0-9", "unterminated character class");

            // Assert
            Assert.StartsWith("Terminal NUMBER [/[0-9]: unterminated character class: ", exception.Message);
        }

        [Fact]
        public void ENFA_RegexBuild_Exception_WithInnerException_PreservesInner()
        {
            // Arrange
            var inner = new FormatException("root cause");

            // Act
            var exception = new ENFA_RegexBuild_Exception("IDENTIFIER", "abc", "invalid", inner);

            // Assert
            Assert.StartsWith("Terminal IDENTIFIER [abc]: invalid: ", exception.Message);
            Assert.Same(inner, exception.InnerException);
        }

        [Fact]
        public void ENFA_RegexBuild_Exception_DerivesFromENFA_Exception()
        {
            // Arrange & Act
            var exception = new ENFA_RegexBuild_Exception("message");

            // Assert
            Assert.IsAssignableFrom<ENFA_Exception>(exception);
        }
    }
}
