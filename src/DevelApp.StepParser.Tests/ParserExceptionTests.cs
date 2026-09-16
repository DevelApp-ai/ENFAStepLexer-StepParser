using Xunit;
using DevelApp.StepParser;
using System;

namespace DevelApp.StepParser.Tests
{
    /// <summary>
    /// Tests for the ENFA exception hierarchy in DevelApp.StepParser
    /// </summary>
    public class ParserExceptionTests
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
        public void ENFA_GrammarBuild_Exception_MessageOnly_PrefixesMessage()
        {
            // Arrange & Act
            var exception = new ENFA_GrammarBuild_Exception("bad grammar");

            // Assert
            Assert.StartsWith("bad grammar: ", exception.Message);
        }

        [Fact]
        public void ENFA_GrammarBuild_Exception_WithNonTerminalDetails_FormatsNonTerminalContext()
        {
            // Arrange & Act
            var exception = new ENFA_GrammarBuild_Exception("expression", "NUMBER +", "unexpected token");

            // Assert
            Assert.StartsWith("Non-Terminal expression [NUMBER +]: unexpected token: ", exception.Message);
        }

        [Fact]
        public void ENFA_GrammarBuild_Exception_WithInnerException_PreservesInner()
        {
            // Arrange
            var inner = new FormatException("root cause");

            // Act
            var exception = new ENFA_GrammarBuild_Exception("expression", "", "invalid", inner);

            // Assert
            Assert.StartsWith("Non-Terminal expression []: invalid: ", exception.Message);
            Assert.Same(inner, exception.InnerException);
        }

        [Fact]
        public void ENFA_GrammarBuild_Exception_DerivesFromENFA_Exception()
        {
            // Arrange & Act
            var exception = new ENFA_GrammarBuild_Exception("message");

            // Assert
            Assert.IsAssignableFrom<ENFA_Exception>(exception);
        }
    }
}
