using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using DevelApp.StepLexer;
using Xunit;

namespace DevelApp.StepLexer.Tests
{
    /// <summary>
    /// Regression tests for the UTF-8-native token pipeline (issue #87):
    /// tokens produced by the lexer carry their value as a
    /// <see cref="ZeroCopyStringView"/> over the UTF-8 input bytes instead
    /// of a per-match decoded string. The view must be observable before
    /// any decode, <see cref="StepToken.Value"/> must decode identically
    /// (including multi-byte Danish text), and the string constructor must
    /// keep its pre-existing behaviour (no view).
    /// </summary>
    public class Utf8NativeTokenTests
    {
        private static List<StepToken> RunToCompletion(StepLexer lexer, byte[] input)
        {
            lexer.Initialize(input, "test.txt");
            var tokens = new List<StepToken>();
            var guard = 0;
            while (lexer.ActivePaths.Any(p => p.IsValid && p.Position < input.Length))
            {
                var result = lexer.Step();
                tokens.AddRange(result.NewTokens);
                if (++guard > 1000)
                {
                    break;
                }
            }
            return tokens;
        }

        [Fact]
        public void LexedToken_ExposesZeroCopyViewBeforeDecode()
        {
            var lexer = new StepLexer();
            lexer.AddRule(new TokenRule("KW", "'alpha'"));

            var tokens = RunToCompletion(lexer, Encoding.UTF8.GetBytes("alpha"));

            var token = Assert.Single(tokens);
            Assert.True(token.TryGetValueView(out var view),
                "A lexer-produced token must expose its zero-copy UTF-8 view (issue #87).");
            Assert.Equal("alpha", view.ToString());
            Assert.Equal(Encoding.UTF8.GetBytes("alpha").Length, view.Length);
        }

        [Fact]
        public void LexedToken_Value_DecodesDanishMultibyteCorrectly()
        {
            var lexer = new StepLexer();
            lexer.AddRule(new TokenRule("DA", "'æøå'"));

            var tokens = RunToCompletion(lexer, Encoding.UTF8.GetBytes("æøå"));

            var token = Assert.Single(tokens);
            Assert.Equal("æøå", token.Value);
            // æ, ø and å are each two bytes in UTF-8: token spans are
            // byte spans, so Length must be 6, not the UTF-16 count 3.
            Assert.Equal(6, token.Length);
            Assert.Equal(6, Encoding.UTF8.GetByteCount("æøå"));
        }

        [Fact]
        public void LexedToken_ValueAccess_IsStable_AndClearsView()
        {
            var lexer = new StepLexer();
            lexer.AddRule(new TokenRule("KW", "'alpha'"));

            var tokens = RunToCompletion(lexer, Encoding.UTF8.GetBytes("alpha"));
            var token = Assert.Single(tokens);

            Assert.True(token.TryGetValueView(out _));
            Assert.Equal("alpha", token.Value);
            Assert.Equal("alpha", token.Value);
            // Materializing the value consumes the view; a later access
            // must still return the same string from the cached field.
            Assert.False(token.TryGetValueView(out _));
            Assert.Equal("alpha", token.Value);
        }

        [Fact]
        public void Value_Setter_OverridesViewBackedValue()
        {
            var lexer = new StepLexer();
            lexer.AddRule(new TokenRule("KW", "'alpha'"));

            var tokens = RunToCompletion(lexer, Encoding.UTF8.GetBytes("alpha"));
            var token = Assert.Single(tokens);
            Assert.True(token.TryGetValueView(out _));

            token.Value = "replaced";

            Assert.Equal("replaced", token.Value);
            Assert.False(token.TryGetValueView(out _));
        }

        [Fact]
        public void StringConstructedToken_HasNoView()
        {
            var token = new StepToken("TEST", "plain", new CodeLocation(), "ctx");

            Assert.False(token.TryGetValueView(out _),
                "String-constructed tokens carry no view; only lexer-produced tokens do.");
            Assert.Equal("plain", token.Value);
        }
    }
}
