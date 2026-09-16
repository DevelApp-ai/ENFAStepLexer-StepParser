using Xunit;
using DevelApp.StepLexer;
using System;
using System.Text;

namespace DevelApp.StepLexer.Tests
{
    /// <summary>
    /// Additional edge-case tests for UTF8Utils codepoint and hex escape handling
    /// </summary>
    public class UTF8UtilsExtendedTests
    {
        [Fact]
        public void GetNextCodepoint_Ascii_ReturnsSingleByte()
        {
            // Arrange
            var bytes = Encoding.UTF8.GetBytes("A");

            // Act
            var (codepoint, consumed) = UTF8Utils.GetNextCodepoint(bytes, 0);

            // Assert
            Assert.Equal((uint)65, codepoint);
            Assert.Equal(1, consumed);
        }

        [Fact]
        public void GetNextCodepoint_TwoByteSequence_ReturnsCodepoint()
        {
            // Arrange - U+00E9 (é) = C3 A9
            var bytes = new byte[] { 0xC3, 0xA9 };

            // Act
            var (codepoint, consumed) = UTF8Utils.GetNextCodepoint(bytes, 0);

            // Assert
            Assert.Equal((uint)0xE9, codepoint);
            Assert.Equal(2, consumed);
        }

        [Fact]
        public void GetNextCodepoint_ThreeByteSequence_ReturnsCodepoint()
        {
            // Arrange - U+20AC (€) = E2 82 AC
            var bytes = new byte[] { 0xE2, 0x82, 0xAC };

            // Act
            var (codepoint, consumed) = UTF8Utils.GetNextCodepoint(bytes, 0);

            // Assert
            Assert.Equal((uint)0x20AC, codepoint);
            Assert.Equal(3, consumed);
        }

        [Fact]
        public void GetNextCodepoint_FourByteSequence_ReturnsCodepoint()
        {
            // Arrange - U+1F600 = F0 9F 98 80
            var bytes = new byte[] { 0xF0, 0x9F, 0x98, 0x80 };

            // Act
            var (codepoint, consumed) = UTF8Utils.GetNextCodepoint(bytes, 0);

            // Assert
            Assert.Equal((uint)0x1F600, codepoint);
            Assert.Equal(4, consumed);
        }

        [Fact]
        public void GetNextCodepoint_PositionBeyondLength_ReturnsZero()
        {
            // Arrange
            var bytes = Encoding.UTF8.GetBytes("A");

            // Act
            var (codepoint, consumed) = UTF8Utils.GetNextCodepoint(bytes, 5);

            // Assert
            Assert.Equal((uint)0, codepoint);
            Assert.Equal(0, consumed);
        }

        [Fact]
        public void GetNextCodepoint_TruncatedTwoByteSequence_ReturnsReplacement()
        {
            // Arrange - lead byte with no continuation
            var bytes = new byte[] { 0xC3 };

            // Act
            var (codepoint, consumed) = UTF8Utils.GetNextCodepoint(bytes, 0);

            // Assert
            Assert.Equal((uint)0xFFFD, codepoint);
            Assert.Equal(1, consumed);
        }

        [Fact]
        public void GetNextCodepoint_InvalidTwoByteContinuation_ReturnsReplacement()
        {
            // Arrange - continuation byte is not 10xxxxxx
            var bytes = new byte[] { 0xC3, 0x41 };

            // Act
            var (codepoint, consumed) = UTF8Utils.GetNextCodepoint(bytes, 0);

            // Assert
            Assert.Equal((uint)0xFFFD, codepoint);
            Assert.Equal(1, consumed);
        }

        [Fact]
        public void GetNextCodepoint_TruncatedThreeByteSequence_ReturnsReplacement()
        {
            // Arrange
            var bytes = new byte[] { 0xE2, 0x82 };

            // Act
            var (codepoint, consumed) = UTF8Utils.GetNextCodepoint(bytes, 0);

            // Assert
            Assert.Equal((uint)0xFFFD, codepoint);
            Assert.Equal(1, consumed);
        }

        [Fact]
        public void GetNextCodepoint_InvalidThreeByteContinuation_ReturnsReplacement()
        {
            // Arrange - third byte is not a continuation byte
            var bytes = new byte[] { 0xE2, 0x82, 0x41 };

            // Act
            var (codepoint, consumed) = UTF8Utils.GetNextCodepoint(bytes, 0);

            // Assert
            Assert.Equal((uint)0xFFFD, codepoint);
            Assert.Equal(1, consumed);
        }

        [Fact]
        public void GetNextCodepoint_TruncatedFourByteSequence_ReturnsReplacement()
        {
            // Arrange
            var bytes = new byte[] { 0xF0, 0x9F, 0x98 };

            // Act
            var (codepoint, consumed) = UTF8Utils.GetNextCodepoint(bytes, 0);

            // Assert
            Assert.Equal((uint)0xFFFD, codepoint);
            Assert.Equal(1, consumed);
        }

        [Fact]
        public void GetNextCodepoint_InvalidFourByteContinuation_ReturnsReplacement()
        {
            // Arrange - fourth byte is not a continuation byte
            var bytes = new byte[] { 0xF0, 0x9F, 0x98, 0x41 };

            // Act
            var (codepoint, consumed) = UTF8Utils.GetNextCodepoint(bytes, 0);

            // Assert
            Assert.Equal((uint)0xFFFD, codepoint);
            Assert.Equal(1, consumed);
        }

        [Fact]
        public void GetNextCodepoint_InvalidLeadByte_ReturnsReplacement()
        {
            // Arrange - 0xFF is never a valid UTF-8 lead byte
            var bytes = new byte[] { 0xFF, 0x41 };

            // Act
            var (codepoint, consumed) = UTF8Utils.GetNextCodepoint(bytes, 0);

            // Assert
            Assert.Equal((uint)0xFFFD, codepoint);
            Assert.Equal(1, consumed);
        }

        [Fact]
        public void ParseHexEscape_StandardFormat_ReturnsCodepoint()
        {
            // Arrange - \x41 = 'A'
            var bytes = Encoding.UTF8.GetBytes("\\x41");

            // Act
            var (codepoint, consumed) = UTF8Utils.ParseHexEscape(bytes, 0);

            // Assert
            Assert.Equal((uint)0x41, codepoint);
            Assert.Equal(4, consumed);
        }

        [Fact]
        public void ParseHexEscape_BracedFormat_ReturnsCodepoint()
        {
            // Arrange - \x{1F600}
            var bytes = Encoding.UTF8.GetBytes("\\x{1F600}");

            // Act
            var (codepoint, consumed) = UTF8Utils.ParseHexEscape(bytes, 0);

            // Assert
            Assert.Equal((uint)0x1F600, codepoint);
            Assert.Equal(9, consumed);
        }

        [Fact]
        public void ParseHexEscape_BracedShortFormat_ReturnsCodepoint()
        {
            // Arrange - \x{41}
            var bytes = Encoding.UTF8.GetBytes("\\x{41}");

            // Act
            var (codepoint, consumed) = UTF8Utils.ParseHexEscape(bytes, 0);

            // Assert
            Assert.Equal((uint)0x41, codepoint);
            Assert.Equal(6, consumed);
        }

        [Theory]
        [InlineData("\\xZZ")]       // invalid hex digits
        [InlineData("\\x4")]        // incomplete standard escape
        [InlineData("\\x{}")]       // empty braces
        [InlineData("\\x{G1}")]     // invalid digit in braces
        [InlineData("\\x{41")]      // unterminated braces
        [InlineData("\\x{1234567}")] // more than six hex digits
        [InlineData("nothex")]      // not a hex escape at all
        [InlineData("\\y41")]       // wrong escape letter
        public void ParseHexEscape_InvalidInput_ReturnsZero(string input)
        {
            // Arrange
            var bytes = Encoding.UTF8.GetBytes(input);

            // Act
            var (codepoint, consumed) = UTF8Utils.ParseHexEscape(bytes, 0);

            // Assert
            Assert.Equal((uint)0, codepoint);
            Assert.Equal(0, consumed);
        }

        [Fact]
        public void ParseHexEscape_TruncatedPrefix_ReturnsZero()
        {
            // Arrange - only "\x" present
            var bytes = Encoding.UTF8.GetBytes("\\x");

            // Act
            var (codepoint, consumed) = UTF8Utils.ParseHexEscape(bytes, 0);

            // Assert
            Assert.Equal((uint)0, codepoint);
            Assert.Equal(0, consumed);
        }

        [Theory]
        [InlineData((byte)'0', 0)]
        [InlineData((byte)'9', 9)]
        [InlineData((byte)'A', 10)]
        [InlineData((byte)'F', 15)]
        [InlineData((byte)'a', 10)]
        [InlineData((byte)'f', 15)]
        [InlineData((byte)'g', -1)]
        [InlineData((byte)'G', -1)]
        [InlineData((byte)'/', -1)]
        [InlineData((byte)':', -1)]
        [InlineData((byte)'@', -1)]
        [InlineData((byte)'[', -1)]
        [InlineData((byte)'`', -1)]
        [InlineData((byte)'{', -1)]
        public void HexDigitToValue_MapsHexDigits(byte digit, int expected)
        {
            // Act
            var value = UTF8Utils.HexDigitToValue(digit);

            // Assert
            Assert.Equal(expected, value);
        }

        [Fact]
        public void IsAsciiChar_MatchingByte_ReturnsTrue()
        {
            // Arrange
            var bytes = Encoding.UTF8.GetBytes("x");

            // Act & Assert
            Assert.True(UTF8Utils.IsAsciiChar(bytes, 0, 'x'));
            Assert.False(UTF8Utils.IsAsciiChar(bytes, 0, 'y'));
        }

        [Fact]
        public void IsAsciiChar_PositionOutOfRange_ReturnsFalse()
        {
            // Arrange
            var bytes = Encoding.UTF8.GetBytes("x");

            // Act & Assert
            Assert.False(UTF8Utils.IsAsciiChar(bytes, 1, 'x'));
            Assert.False(UTF8Utils.IsAsciiChar(bytes, 99, 'x'));
        }

        [Fact]
        public void IsAsciiChar_NonAsciiCharacter_ReturnsFalse()
        {
            // Arrange
            var bytes = Encoding.UTF8.GetBytes("x");

            // Act & Assert - é is above 127
            Assert.False(UTF8Utils.IsAsciiChar(bytes, 0, 'é'));
        }

        [Fact]
        public void MatchesAsciiPattern_MatchingBytes_ReturnsTrue()
        {
            // Arrange
            var bytes = Encoding.UTF8.GetBytes("hello world");

            // Act & Assert
            Assert.True(UTF8Utils.MatchesAsciiPattern(bytes, 6, Encoding.UTF8.GetBytes("world")));
            Assert.False(UTF8Utils.MatchesAsciiPattern(bytes, 6, Encoding.UTF8.GetBytes("worlD")));
        }

        [Fact]
        public void MatchesAsciiPattern_PatternBeyondEnd_ReturnsFalse()
        {
            // Arrange
            var bytes = Encoding.UTF8.GetBytes("hi");

            // Act & Assert
            Assert.False(UTF8Utils.MatchesAsciiPattern(bytes, 1, Encoding.UTF8.GetBytes("hello")));
        }

        [Fact]
        public void MatchesAsciiPattern_EmptyPattern_ReturnsTrue()
        {
            // Arrange
            var bytes = Encoding.UTF8.GetBytes("hi");

            // Act & Assert
            Assert.True(UTF8Utils.MatchesAsciiPattern(bytes, 0, ReadOnlySpan<byte>.Empty));
        }
    }
}
