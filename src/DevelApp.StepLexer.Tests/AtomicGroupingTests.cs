using System.Collections.Generic;
using System.Linq;
using System.Text;
using DevelApp.StepLexer;

namespace DevelApp.StepLexer.Tests
{
    /// <summary>
    /// End-to-end lexer tests for atomic grouping (<c>(?&gt;...)</c>) and
    /// possessive quantifier (<c>++</c>, <c>*+</c>, <c>?+</c>) support.
    /// The step lexer is a forward-parsing matcher that never backtracks,
    /// so these constructs are normalized to their greedy equivalents during
    /// pattern preprocessing (see docs/atomic-grouping-evaluation.md).
    /// </summary>
    public class AtomicGroupingTests
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

        [Fact]
        public void AtomicGroup_WithLiteralContent_MatchesLiterally()
        {
            var tokens = LexSingleRule(@"/(?>abc)/", "abcd");
            Assert.Single(tokens);
            Assert.Equal("abc", tokens[0].Value);
        }

        [Fact]
        public void AtomicGroup_WithLiteralContent_DoesNotMatchOtherText()
        {
            Assert.Empty(LexSingleRule(@"/(?>abc)/", "xyz"));
        }

        [Fact]
        public void AtomicGroup_CombinesWithInlineModifiers()
        {
            Assert.Single(LexSingleRule(@"/(?i)(?>ABC)/", "abc"));
            Assert.Single(LexSingleRule(@"/(?i)(?>ABC)/", "ABC"));
            Assert.Empty(LexSingleRule(@"/(?i)(?>ABC)/", "xyz"));
        }

        [Fact]
        public void AtomicGroup_CombinesWithCommentGroups()
        {
            Assert.Single(LexSingleRule(@"/(?# lead)(?>ab)(?# tail)/", "ab"));
        }

        [Fact]
        public void AtomicGroup_WithUnsupportedContents_RemainsConservative()
        {
            // Alternation inside the group is not supported by the simplified
            // matcher: the pattern must not match rather than match wrongly.
            Assert.Empty(LexSingleRule(@"/(?>a|b)/", "a"));

            // Nested groups inside the group are equally unsupported.
            Assert.Empty(LexSingleRule(@"/(?>a(b)c)/", "abc"));
        }

        [Fact]
        public void AtomicGroup_MultipleGroupsAreUnwrapped()
        {
            Assert.Single(LexSingleRule(@"/(?>a)(?>bc)/", "abc"));
        }

        [Fact]
        public void PossessivePlus_OnQuotedLiteral_BehavesGreedily()
        {
            var tokens = LexSingleRule(@"/\Qab\E++/", "ababab");
            Assert.Single(tokens);
            Assert.Equal("ababab", tokens[0].Value);
        }

        [Fact]
        public void PossessiveStar_OnQuotedLiteral_BehavesGreedily()
        {
            var tokens = LexSingleRule(@"/\Qab\E*+/", "abab");
            Assert.Single(tokens);
            Assert.Equal("abab", tokens[0].Value);
        }

        [Fact]
        public void PossessiveQuestion_OnQuotedLiteral_MatchesOnce()
        {
            var tokens = LexSingleRule(@"/\Qab\E?+/", "ab");
            Assert.Single(tokens);
            Assert.Equal("ab", tokens[0].Value);
        }

        [Fact]
        public void PossessiveMarker_DoesNotAffectPlainText()
        {
            // A '+' that is not a possessive marker (no preceding quantifier)
            // is preserved; the pattern still contains a metacharacter and
            // therefore conservatively does not match.
            Assert.Empty(LexSingleRule(@"/ab+/", "abbb"));
            Assert.Empty(LexSingleRule(@"/ab+?/", "abbb"));
        }

        [Fact]
        public void PossessiveMarker_InsideQuotedLiteral_IsLiteralText()
        {
            // '+' inside \Q...\E is literal text, not a possessive marker
            var tokens = LexSingleRule(@"/\Qa+\E/", "a+");
            Assert.Single(tokens);
            Assert.Equal("a+", tokens[0].Value);
        }

        [Fact]
        public void PossessiveMarker_InsideCharacterClass_IsLiteralText()
        {
            // '+' inside a character class is literal text; the class itself
            // is not supported by the simplified matcher (conservative
            // no-match) but must not crash or hang.
            Assert.Empty(LexSingleRule(@"/[a+b]++/", "ab"));
        }
    }
}
