using System.Collections.Generic;
using System.Linq;
using System.Text;
using DevelApp.StepLexer;

namespace DevelApp.StepLexer.Tests
{
    /// <summary>
    /// End-to-end lexer tests for PCRE2 inline modifiers
    /// (<c>(?i)</c>, <c>(?x)</c>, <c>(?-i)</c>, ...), <c>(?#...)</c>
    /// comments, quoted literal sequences (<c>\Q...\E</c>) and the literal
    /// fallback for plain-text patterns.
    /// </summary>
    public class InlineModifierAndQuotedLiteralTests
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

        private static List<StepToken> LexSingleRule(string pattern, string input)
        {
            var lexer = new StepLexer();
            lexer.AddRule(new TokenRule("T", pattern));
            return RunToCompletion(lexer, Encoding.UTF8.GetBytes(input));
        }

        // ------------------------------------------------------------------
        // Inline modifiers
        // ------------------------------------------------------------------

        [Fact]
        public void InlineCaseInsensitiveModifier_MatchesUpperAndLowercase()
        {
            Assert.Single(LexSingleRule(@"/(?i)abc/", "ABCdef"));
            Assert.Single(LexSingleRule(@"/(?i)abc/", "abcdef"));
            Assert.Empty(LexSingleRule(@"/(?i)abc/", "xyz"));
        }

        [Fact]
        public void InlineCaseInsensitiveModifier_AppliesGlobally()
        {
            // The modifier may appear anywhere in the pattern and applies to
            // the whole pattern.
            Assert.Single(LexSingleRule(@"/ab(?i)c/", "aBC"));
        }

        [Fact]
        public void InlineModifierGroup_MultipleFlagsAreAccepted()
        {
            // (?im) - only 'i' has an observable effect here
            Assert.Single(LexSingleRule(@"/(?im)abc/", "AbC"));
        }

        [Fact]
        public void InlineModifierTurnOff_IsAccepted()
        {
            // (?-i) disables case-insensitive matching (a no-op from the
            // default case-sensitive state, but must parse and not crash)
            Assert.Single(LexSingleRule(@"/(?-i)abc/", "abc"));
            Assert.Empty(LexSingleRule(@"/(?-i)abc/", "ABC"));
        }

        [Fact]
        public void ExtendedMode_StripsUnescapedWhitespace()
        {
            // In extended mode, unescaped whitespace in the pattern is ignored
            Assert.Single(LexSingleRule("/(?x) a b c/", "abc"));
        }

        [Fact]
        public void ExtendedMode_StripsHashLineComments()
        {
            // In extended mode, # starts a comment running to the end of the line
            Assert.Single(LexSingleRule("/(?x) a#comment\nb/", "ab"));
        }

        [Fact]
        public void ExtendedMode_WhitespaceInsideCharacterClassIsPreserved()
        {
            // Whitespace inside [...] is significant even in extended mode;
            // if the space were stripped the pattern would stop matching.
            var tokens = LexSingleRule(@"/(?x)[ \t\r\n]+/", "  ");
            Assert.Single(tokens);
            Assert.Equal("  ", tokens[0].Value);
        }

        [Fact]
        public void CommentGroup_IsRemovedFromPattern()
        {
            Assert.Single(LexSingleRule(@"/ab(?# a comment )cd/", "abcd"));
            Assert.Empty(LexSingleRule(@"/ab(?# a comment )cd/", "abc"));
        }

        [Fact]
        public void ScopedModifierGroup_IsNotSupportedAndDoesNotMatch()
        {
            // Scoped groups like (?i:...) are left untouched; the remaining
            // metacharacters make the pattern unsupported, so it must not
            // produce a wrong literal match.
            Assert.Empty(LexSingleRule(@"/(?i:ab)/", "ab"));
        }

        [Fact]
        public void NonModifierGroup_DoesNotBreakLiteralFallback()
        {
            // A '(' that is not a modifier or comment group is a metacharacter;
            // the pattern must not match as plain text.
            Assert.Empty(LexSingleRule(@"/a(b/", "a(b"));
        }

        // ------------------------------------------------------------------
        // Quoted literals \Q...\E
        // ------------------------------------------------------------------

        [Fact]
        public void QuotedLiteral_MatchesMetacharactersLiterally()
        {
            var tokens = LexSingleRule(@"/\Qa.b*c\E/", "a.b*c");
            Assert.Single(tokens);
            Assert.Equal("a.b*c", tokens[0].Value);
            Assert.Empty(LexSingleRule(@"/\Qa.b*c\E/", "aXbXc"));
        }

        [Fact]
        public void QuotedLiteral_WithQuantifier_RepeatsGreedily()
        {
            var tokens = LexSingleRule(@"/\Qab\E+/", "ababab");
            Assert.Single(tokens);
            Assert.Equal("ababab", tokens[0].Value);

            var twoTokens = LexSingleRule(@"/\Qab\E{2}/", "ababab");
            Assert.Single(twoTokens);
            Assert.Equal("abab", twoTokens[0].Value);
        }

        [Fact]
        public void QuotedLiteral_UnterminatedRunsToEndOfPattern()
        {
            // PCRE2 semantics: an unterminated \Q quotes to the end of the pattern
            var tokens = LexSingleRule(@"/\Qa.c/", "a.c");
            Assert.Single(tokens);
            Assert.Equal("a.c", tokens[0].Value);
        }

        [Fact]
        public void QuotedLiteral_CombinesWithCaseInsensitiveModifier()
        {
            var tokens = LexSingleRule(@"/(?i)\QAB\E/", "ab");
            Assert.Single(tokens);
            Assert.Equal("ab", tokens[0].Value);
        }

        // ------------------------------------------------------------------
        // Literal fallback
        // ------------------------------------------------------------------

        [Fact]
        public void PlainTextPattern_MatchesLiterally()
        {
            var tokens = LexSingleRule("/abc/", "abcdef");
            Assert.Single(tokens);
            Assert.Equal("abc", tokens[0].Value);
        }

        [Fact]
        public void PatternWithMetacharacters_DoesNotProduceFalseLiteralMatch()
        {
            // /a.c/ contains the dot metacharacter; it must not silently
            // match the literal text "a.c" or anything else.
            Assert.Empty(LexSingleRule("/a.c/", "abc"));
            Assert.Empty(LexSingleRule("/a.c/", "a.c"));
        }

        [Fact]
        public void EmptyPattern_DoesNotMatch()
        {
            Assert.Empty(LexSingleRule("//", "abc"));
        }
    }
}
