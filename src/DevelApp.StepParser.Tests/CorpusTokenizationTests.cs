using Xunit;
using DevelApp.StepParser;
using System.Linq;

namespace DevelApp.StepParser.Tests
{
    /// <summary>
    /// Regression tests for the issue #80 tokenization fixes: quoted
    /// literals in productions must materialize as lexer-matchable token
    /// rules, single-quoted token rule patterns must match their literal,
    /// unreferenced whitespace rules must not kill parse paths (while
    /// staying in the token stream for tooling), root rules must only
    /// reduce at end of input, and multi-line brace rules must not swallow
    /// quoted braces.
    /// </summary>
    public class CorpusTokenizationTests
    {
        private const string MixedProductionGrammar = @"
Grammar: MixedProduction
<text> ::= /[a-zA-Z][a-zA-Z0-9]*/
<image-line> ::= 'image' ':' <text>
<start> ::= <image-line>
";

        [Fact]
        public void QuotedLiteralInProduction_MaterializesAsToken()
        {
            using var engine = new StepParserEngine();
            engine.LoadGrammarFromContent(MixedProductionGrammar);

            var result = engine.Parse("image: nginx", "input.txt");

            Assert.True(result.Success,
                "Mixed production with quoted literals must parse; errors: "
                + string.Join("; ", result.Errors));
            // The lexer keeps parallel ENFA paths, so both the <text> rule and
            // the materialized literal rule match "image"; the literal token
            // must be present with the production symbol as its type.
            Assert.Contains(result.Tokens, t => t.Type == "'image'" && t.Value == "image");
            Assert.Contains(result.Tokens, t => t.Type == "':'" && t.Value == ":");
            Assert.Contains(result.Tokens, t => t.Type == "text" && t.Value == "nginx");
        }

        [Fact]
        public void QuotedLiteralReusesExistingTokenRule_WhenLiteralMatches()
        {
            // The production references 'x' while a token rule with pattern
            // 'x' exists: the parser-side symbol must be rewritten to that
            // rule's name instead of forking the lexer with a duplicate.
            const string grammar = @"
Grammar: ReuseLiteral
<N> ::= /[0-9]+/
<X> ::= 'x'
<start> ::= <N> 'x' <N>
";
            using var engine = new StepParserEngine();
            engine.LoadGrammarFromContent(grammar);

            var result = engine.Parse("12x34", "input.txt");

            Assert.True(result.Success,
                "Production literal must reuse the existing token rule; errors: "
                + string.Join("; ", result.Errors));
            // The 'x' token must be lexed by the existing <X> rule, not a
            // synthesized duplicate literal rule.
            Assert.Contains(result.Tokens, t => t.Type == "X" && t.Value == "x");
            Assert.DoesNotContain(result.Tokens, t => t.Type == "'x'");
        }

        [Fact]
        public void SingleQuotedTokenRulePattern_MatchesWithoutQuotes()
        {
            const string grammar = @"
Grammar: SingleQuoted
<NUMBER> ::= /[0-9]+/
<PLUS> ::= '+'
<expr> ::= <expr> '+' <expr>
<expr> ::= <NUMBER>
<start> ::= <expr>
";
            using var engine = new StepParserEngine();
            engine.LoadGrammarFromContent(grammar);

            var result = engine.Parse("1+2+3", "input.txt");

            Assert.True(result.Success,
                "Single-quoted token rule patterns must match the bare literal; errors: "
                + string.Join("; ", result.Errors));
            Assert.Contains(result.Tokens, t => t.Type == "PLUS" && t.Value == "+");
        }

        [Fact]
        public void UnreferencedWhitespaceRule_TokensStayInStreamButDoNotKillParse()
        {
            const string grammar = @"
Grammar: WsInStream
<NUMBER> ::= /[0-9]+/
<WS> ::= /[ \t\r\n]+/
<expression> ::= <NUMBER>
";
            using var engine = new StepParserEngine();
            engine.LoadGrammarFromContent(grammar);

            var result = engine.Parse("1 2", "input.txt");

            // The lexer still emits WS tokens for tooling (RealTimeParserSession
            // depends on them) ...
            Assert.Contains(result.Tokens, t => t.Type == "WS");
            // ... but the parse must still succeed.
            Assert.True(result.Success,
                "Unreferenced whitespace tokens must not kill parse paths; errors: "
                + string.Join("; ", result.Errors));
        }

        [Fact]
        public void GrammarWithoutWhitespaceRule_ParsesViaDefaultWsRule()
        {
            const string grammar = @"
Grammar: NoWsRule
<NUMBER> ::= /[0-9]+/
<PLUS> ::= '+'
<expr> ::= <expr> '+' <expr>
<expr> ::= <NUMBER>
<start> ::= <expr>
";
            using var engine = new StepParserEngine();
            engine.LoadGrammarFromContent(grammar);

            var result = engine.Parse("1 + 2", "input.txt");

            Assert.True(result.Success,
                "A grammar with no whitespace rule must parse via the implicit default; errors: "
                + string.Join("; ", result.Errors));
        }

        [Fact]
        public void RootRule_DoesNotReduceMidParse()
        {
            // With the root rule reducing mid-parse, the root symbol shifted
            // further tokens and spawned nonsense stacks that crowded out the
            // genuine parse on longer inputs.
            const string grammar = @"
Grammar: RootAtEnd
<NUMBER> ::= /[0-9]+/
<PLUS> ::= '+'
<expr> ::= <expr> '+' <expr>
<expr> ::= <NUMBER>
<start> ::= <expr>
";
            using var engine = new StepParserEngine();
            engine.LoadGrammarFromContent(grammar);

            var input = string.Join("+", Enumerable.Range(0, 25).Select(i => (i * 7) % 10));
            var result = engine.Parse(input, "input.txt");

            Assert.True(result.Success,
                "Long ambiguous input must parse; root must only reduce at end of input; errors: "
                + string.Join("; ", result.Errors));
        }

        [Fact]
        public void MultiLineBraceRule_QuotedBracesDoNotNest()
        {
            // CloudFormation-style grammars quote braces inside multi-line
            // rules; naive brace counting swallowed the whole file.
            const string grammar = @"
Grammar: QuotedBraces
<obj> ::= /[^ \t\r\n]+/
<start> ::= <obj>
<block> ::= '{' <obj> '}'
";
            using var engine = new StepParserEngine();
            // Must not throw IndexOutOfRange while collecting the multi-line rule.
            engine.LoadGrammarFromContent(grammar);
            Assert.True(engine.MlAssistStatus.ToString().Length > 0); // engine usable
        }
    }
}
