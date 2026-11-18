// Regression tests for issue #143: a literal '/' inside a token /regex/
// body (escaped '\/' or inside a [...] character class) must not truncate
// the rule. Previously the loader treated the first inner unescaped '/' as
// the end of the body; the truncated rule then never matched and no load-time
// error was reported.

using System.Linq;
using DevelApp.StepParser;
using Xunit;

namespace DevelApp.StepParser.Tests
{
    public class Issue143RegexSlashTests
    {
        private static (bool Success, System.Collections.Generic.IReadOnlyList<DevelApp.StepLexer.StepToken> Tokens) Parse(
            string grammar, string input)
        {
            using var engine = new StepParserEngine();
            engine.LoadGrammarFromContent(grammar);
            var result = engine.Parse(input);
            return (result.Success, result.Tokens);
        }

        [Fact]
        public void EscapedSlash_TokenRule_MatchesSlash()
        {
            // Exact repro from issue #143: <SLASH> ::= /\// never matched.
            var grammar = "Grammar: Issue143EscapedSlash\n" +
                          "<start> ::= <slash>\n" +
                          "<slash> ::= /\\//\n";

            var (success, tokens) = Parse(grammar, "/");
            Assert.True(success, "escaped slash \\/ must be honored in the regex body");
            var tok = Assert.Single(tokens);
            Assert.Equal("slash", tok.Type);
            Assert.Equal("/", tok.Value);
        }

        [Fact]
        public void SlashInsideCharClass_TokenRule_Matches()
        {
            var grammar = "Grammar: Issue143ClassSlash\n" +
                          "<start> ::= <op>\n" +
                          "<op> ::= /[a/b]/\n";

            foreach (var input in new[] { "a", "/", "b" })
            {
                var (success, tokens) = Parse(grammar, input);
                Assert.True(success, $"char class [/] must match '{input}'");
                Assert.Equal(input, Assert.Single(tokens).Value);
            }
        }

        [Fact]
        public void IssueRepro_OperatorRule_WithSlashClass_Matches()
        {
            // Second repro line from issue #143: /...|[/]|.../
            var grammar = "Grammar: Issue143Operator\n" +
                          "<start> ::= <op>\n" +
                          "<op> ::= /...|[/]|.../\n";

            var (successSlash, tokensSlash) = Parse(grammar, "/");
            Assert.True(successSlash, "'/' must match via the [/] alternative");
            Assert.Equal("/", Assert.Single(tokensSlash).Value);

            var (successDots, tokensDots) = Parse(grammar, "...");
            Assert.True(successDots, "'...' must match via the ... alternative");
            Assert.Equal("...", Assert.Single(tokensDots).Value);
        }

        [Fact]
        public void EscapedSlash_ProductionRegex_Matches()
        {
            // Regex written directly in a production RHS (materialized as a
            // lexer rule, issue #83) with an escaped slash.
            var grammar = "Grammar: Issue143ProdEscaped\n" +
                          "<start> ::= /a\\/b/\n";

            var (success, tokens) = Parse(grammar, "a/b");
            Assert.True(success, "production-embedded /a\\/b/ must match 'a/b'");
            Assert.Equal("a/b", Assert.Single(tokens).Value);
        }

        [Fact]
        public void SlashClass_ProductionRegex_Matches()
        {
            var grammar = "Grammar: Issue143ProdClass\n" +
                          "<start> ::= /x[/]y/\n";

            var (success, tokens) = Parse(grammar, "x/y");
            Assert.True(success, "production-embedded /x[/]y/ must match 'x/y'");
            Assert.Equal("x/y", Assert.Single(tokens).Value);
        }

        [Fact]
        public void EscapedBracketInClass_DoesNotEndClass()
        {
            // /[\]]/ : the class contains an escaped ']'.
            var grammar = "Grammar: Issue143EscapedBracket\n" +
                          "<start> ::= <c>\n" +
                          "<c> ::= /[\\]]/\n";

            var (success, tokens) = Parse(grammar, "]");
            Assert.True(success, "escaped ']' must stay inside the class");
            Assert.Equal("]", Assert.Single(tokens).Value);
        }

        [Fact]
        public void UnterminatedRegex_IsNotTreatedAsTokenRule()
        {
            var grammar = "Grammar: Issue143Unterminated\n" +
                          "<start> ::= <w>\n" +
                          "<w> ::= /abc\n"; // unterminated: not a token rule, must not throw

            using var engine = new StepParserEngine();
            engine.LoadGrammarFromContent(grammar);
            Assert.DoesNotContain(engine.CurrentGrammar!.TokenRules, r => r.Pattern.Contains("abc"));
        }

        [Fact]
        public void RegexFollowedByJunk_StaysAProduction_NotTruncatedTokenRule()
        {
            // /a/b/ : the first '/' after 'a' closes the regex; trailing 'b/'
            // means this is not a single-terminal token rule. It must not be
            // registered as the truncated rule '/a/'.
            var grammar = "Grammar: Issue143TrailingJunk\n" +
                          "<start> ::= /a/b/\n";

            using var engine = new StepParserEngine();
            engine.LoadGrammarFromContent(grammar);
            Assert.DoesNotContain(engine.CurrentGrammar!.TokenRules,
                r => r.Pattern == "/a/");
        }
    }
}
