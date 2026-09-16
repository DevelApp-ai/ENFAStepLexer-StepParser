using Xunit;
using DevelApp.StepLexer;
using System;
using System.Text;

namespace DevelApp.StepLexer.Tests
{
    /// <summary>
    /// Tests for the allocation-free UTF8StringBuilder
    /// </summary>
    public class UTF8StringBuilderTests
    {
        [Fact]
        public void NewBuilder_IsEmptyAndNotFull()
        {
            // Arrange
            Span<byte> buffer = stackalloc byte[8];

            // Act
            var builder = new UTF8StringBuilder(buffer);

            // Assert
            Assert.Equal(0, builder.Length);
            Assert.False(builder.IsFull);
            Assert.True(builder.AsSpan().IsEmpty);
        }

        [Fact]
        public void Append_Byte_IncreasesLength()
        {
            // Arrange
            Span<byte> buffer = stackalloc byte[8];
            var builder = new UTF8StringBuilder(buffer);

            // Act
            builder.Append((byte)'a');
            builder.Append((byte)'b');

            // Assert
            Assert.Equal(2, builder.Length);
            Assert.Equal(Encoding.UTF8.GetBytes("ab").AsSpan().ToArray(), builder.AsSpan().ToArray());
        }

        [Fact]
        public void Append_Span_AppendsAllBytes()
        {
            // Arrange
            Span<byte> buffer = stackalloc byte[16];
            var builder = new UTF8StringBuilder(buffer);
            builder.Append((byte)'x');

            // Act
            builder.Append(Encoding.UTF8.GetBytes("yz"));

            // Assert
            Assert.Equal(3, builder.Length);
            Assert.Equal(Encoding.UTF8.GetBytes("xyz").AsSpan().ToArray(), builder.AsSpan().ToArray());
        }

        [Fact]
        public void Append_EmptySpan_DoesNothing()
        {
            // Arrange
            Span<byte> buffer = stackalloc byte[4];
            var builder = new UTF8StringBuilder(buffer);

            // Act
            builder.Append(ReadOnlySpan<byte>.Empty);

            // Assert
            Assert.Equal(0, builder.Length);
        }

        [Fact]
        public void Append_BeyondCapacity_TruncatesSafely()
        {
            // Arrange
            Span<byte> buffer = stackalloc byte[4];
            var builder = new UTF8StringBuilder(buffer);

            // Act - append more than the buffer can hold
            builder.Append((byte)'a');
            builder.Append((byte)'b');
            builder.Append((byte)'c');
            builder.Append((byte)'d');
            builder.Append((byte)'e');
            builder.Append((byte)'f');

            // Assert - only the first four bytes are kept
            Assert.Equal(4, builder.Length);
            Assert.True(builder.IsFull);
            Assert.Equal(Encoding.UTF8.GetBytes("abcd").AsSpan().ToArray(), builder.AsSpan().ToArray());
        }

        [Fact]
        public void Append_SpanBeyondCapacity_TruncatesSafely()
        {
            // Arrange
            Span<byte> buffer = stackalloc byte[3];
            var builder = new UTF8StringBuilder(buffer);
            builder.Append((byte)'a');

            // Act
            builder.Append(Encoding.UTF8.GetBytes("bcdef"));

            // Assert
            Assert.Equal(3, builder.Length);
            Assert.True(builder.IsFull);
            Assert.Equal(Encoding.UTF8.GetBytes("abc").AsSpan().ToArray(), builder.AsSpan().ToArray());
        }

        [Fact]
        public void Append_MultiByteUtf8_IsPreservedByteForByte()
        {
            // Arrange
            var utf8Text = Encoding.UTF8.GetBytes("héllo €");
            Span<byte> buffer = stackalloc byte[32];
            var builder = new UTF8StringBuilder(buffer);

            // Act
            builder.Append(utf8Text);

            // Assert
            Assert.Equal(utf8Text.Length, builder.Length);
            Assert.Equal(utf8Text.AsSpan().ToArray(), builder.AsSpan().ToArray());
        }

        [Fact]
        public void Append_IntoZeroLengthBuffer_IsNoOp()
        {
            // Arrange
            Span<byte> buffer = stackalloc byte[0];
            var builder = new UTF8StringBuilder(buffer);

            // Act
            builder.Append((byte)'a');
            builder.Append(Encoding.UTF8.GetBytes("bc"));

            // Assert
            Assert.Equal(0, builder.Length);
            Assert.True(builder.IsFull);
        }

        [Fact]
        public void AsSpan_ReflectsOnlyWrittenBytes()
        {
            // Arrange
            Span<byte> buffer = stackalloc byte[8];
            buffer.Fill(0xFF);
            var builder = new UTF8StringBuilder(buffer);

            // Act
            builder.Append((byte)'o');
            builder.Append((byte)'k');

            // Assert - unwritten buffer bytes must not be exposed
            Assert.Equal(2, builder.AsSpan().Length);
            Assert.Equal((byte)'o', builder.AsSpan()[0]);
            Assert.Equal((byte)'k', builder.AsSpan()[1]);
        }
    }
}
