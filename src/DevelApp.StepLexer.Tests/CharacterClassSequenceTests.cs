using System.Collections.Generic;
using System.Linq;
using System.Text;
using DevelApp.StepLexer;
using Xunit;

namespace DevelApp.StepLexer.Tests
{
    /// <summary>
    /// Regression tests for the general character-class sequence matcher
    /// (issue #83, Cluster A): character classes ([...] / [^...] with
    /// ranges and escapes) and quantified atoms (+, *, ?, {m,n}, lazy
    /// variants), concatenated greedily with no backtracking.
    /// </summary>
    public class CharacterClassSequenceTests
    {
        private static List<StepToken> LexSingleRule(string pattern, string input)
        {
            var lexer = new StepLexer();
            lexer.AddRule(new TokenRule("T", pattern));
            return RunToCompletion(lexer, Encoding.UTF8.GetBytes(input));
        }

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
        public void SimpleCharacterClass_MatchesItsMembers()
        {
            var tokens = LexSingleRule("/[aeiou]+/", "aabbboo");
            Assert.Single(tokens);
            Assert.Equal("aa", tokens[0].Value);
        }

        [Fact]
        public void RangedCharacterClass_MatchesRange()
        {
            var tokens = LexSingleRule("/[0-9]+/", "12abc45");
            Assert.Single(tokens);
            Assert.Equal("12", tokens[0].Value);
        }

        [Fact]
        public void DanishLowercaseStem_MatchesDanishLetters()
        {
            // Danish.grammar: <noun-stem> ::= /[a-zæøå]+/
            var tokens = LexSingleRule("/[a-zæøå]+/", "pølse med brød");
            Assert.Single(tokens);
            Assert.Equal("pølse", tokens[0].Value);
        }

        [Fact]
        public void DanishProperNoun_MatchesCapitalizedStem()
        {
            // Danish.grammar: <proper-noun> ::= /[A-ZÆØÅ][a-zæøå]*/
            var tokens = LexSingleRule("/[A-ZÆØÅ][a-zæøå]*/", "København er");
            Assert.Single(tokens);
            Assert.Equal("København", tokens[0].Value);
        }

        [Fact]
        public void ConcatenatedAtoms_MatchInSequence()
        {
            // e.g. /[A-Z][a-z]*[0-9]?/
            var tokens = LexSingleRule("/[A-Z][a-z]*[0-9]?/", "Abc7 rest");
            Assert.Single(tokens);
            Assert.Equal("Abc7", tokens[0].Value);
        }

        [Fact]
        public void NegatedCharacterClass_MatchesUntilExcluded()
        {
            var tokens = LexSingleRule("/[^\"]*/", "abc\"def");
            Assert.Single(tokens);
            Assert.Equal("abc", tokens[0].Value);
        }

        [Fact]
        public void NegatedNewlineClass_StopsAtNewline()
        {
            var tokens = LexSingleRule("/[^\\n]*/", "line1\nline2");
            Assert.Single(tokens);
            Assert.Equal("line1", tokens[0].Value);
        }

        [Fact]
        public void DotAtom_MatchesAnyExceptNewline()
        {
            var tokens = LexSingleRule("/a.c/", "a.c");
            Assert.Single(tokens);
            Assert.Equal("a.c", tokens[0].Value);

            // '.' must not cross a newline
            var tokens2 = LexSingleRule("/a.c/", "a\nbc");
            Assert.Empty(tokens2);
        }

        [Fact]
        public void EscapedAtoms_MatchControlCharacters()
        {
            // e.g. ABNF.grammar: /\r?\n/
            var tokens = LexSingleRule("/\\r?\\n/", "\r\nrest");
            Assert.Single(tokens);
            Assert.Equal("\r\n", tokens[0].Value);

            var tokens2 = LexSingleRule("/\\r?\\n/", "\nrest");
            Assert.Single(tokens2);
            Assert.Equal("\n", tokens2[0].Value);
        }

        [Fact]
        public void HexEscape_MatchesCharacter()
        {
            var tokens = LexSingleRule("/[\\x41-\\x5A]+/", "ABCdef");
            Assert.Single(tokens);
            Assert.Equal("ABC", tokens[0].Value);
        }

        [Fact]
        public void ShorthandClass_MatchesDigits()
        {
            var tokens = LexSingleRule("/\\d+/", "42x");
            Assert.Single(tokens);
            Assert.Equal("42", tokens[0].Value);
        }

        [Fact]
        public void ShorthandNegatedClass_MatchesNonDigits()
        {
            var tokens = LexSingleRule("/\\D+/", "abc123");
            Assert.Single(tokens);
            Assert.Equal("abc", tokens[0].Value);
        }

        [Fact]
        public void BoundedQuantifier_MatchesExactly()
        {
            var tokens = LexSingleRule("/a{2,3}/", "aaaa");
            Assert.Single(tokens);
            Assert.Equal("aaa", tokens[0].Value);

            var tooFew = LexSingleRule("/a{2,3}/", "a");
            Assert.Empty(tooFew);
        }

        [Fact]
        public void ExactQuantifier_MatchesExactCount()
        {
            var tokens = LexSingleRule("/[0-9]{3}/", "1234");
            Assert.Single(tokens);
            Assert.Equal("123", tokens[0].Value);
        }

        [Fact]
        public void OpenEndedQuantifier_MatchesAtLeastMin()
        {
            var tokens = LexSingleRule("/x{2,}/", "xxx");
            Assert.Single(tokens);
            Assert.Equal("xxx", tokens[0].Value);

            var tooFew = LexSingleRule("/x{2,}/", "x");
            Assert.Empty(tooFew);
        }

        [Fact]
        public void OptionalAtom_MatchesOnceOrNotAtAll()
        {
            var with = LexSingleRule("/colou?r/", "colour");
            Assert.Single(with);
            Assert.Equal("colour", with[0].Value);

            var without = LexSingleRule("/colou?r/", "color");
            Assert.Single(without);
            Assert.Equal("color", without[0].Value);
        }

        [Fact]
        public void StarQuantifier_RequiresAtLeastOneByteOverall()
        {
            // Zero-width overall matches are rejected (lexer progress rule).
            var tokens = LexSingleRule("/a*/", "bbb");
            Assert.Empty(tokens);
        }

        [Fact]
        public void LiteralCharactersInSequence_MatchInOrder()
        {
            var tokens = LexSingleRule("/[a-z]{3}abc/", "xyzabcq");
            Assert.Single(tokens);
            Assert.Equal("xyzabc", tokens[0].Value);
        }

        [Fact]
        public void GreedyClassWithoutBacktracking_DoesNotSplitForLaterLiteral()
        {
            // Greedy, no backtracking: /[a-z]+abc/ consumes all of "xyzabcq"
            // with the class and then cannot match the literal "abc", so the
            // pattern does not match at all (documented no-backtracking
            // semantics; PCRE would backtrack, this engine does not).
            Assert.Empty(LexSingleRule("/[a-z]+abc/", "xyzabcq"));
        }

        [Fact]
        public void UnicodeSupplementaryPlaneAtom_Matches()
        {
            // Emoji (U+1F600) as a literal atom
            var tokens = LexSingleRule("/\U0001F600+/", "\U0001F600\U0001F600x");
            Assert.Single(tokens);
            Assert.Equal("\U0001F600\U0001F600", tokens[0].Value);
        }

        [Fact]
        public void UnsupportedGroupSyntax_StillDoesNotMatch()
        {
            // Groups and alternation remain unsupported (no backtracking).
            Assert.Empty(LexSingleRule("/(ab)+/", "abab"));
            Assert.Empty(LexSingleRule("/a|b/", "a"));
        }

        [Fact]
        public void CaseInsensitiveModifier_AppliesToClasses()
        {
            var tokens = LexSingleRule("/(?i)[a-z]+/", "ABCdef");
            Assert.Single(tokens);
            Assert.Equal("ABCdef", tokens[0].Value);
        }

        [Fact]
        public void CommentRulePattern_MatchesHashToEndOfLine()
        {
            // The default comment rule synthesized by the parser engine for
            // corpora with '#' comment lines (issue #83, Cluster B).
            var tokens = LexSingleRule("/#[^\\r\\n]*/", "# a comment\nnext");
            Assert.Single(tokens);
            Assert.Equal("# a comment", tokens[0].Value);
        }
    }
}
