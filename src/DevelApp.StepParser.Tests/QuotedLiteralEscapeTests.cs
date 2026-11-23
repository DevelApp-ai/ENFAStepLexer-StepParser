using Xunit;
using DevelApp.StepParser;
using System.Linq;

namespace DevelApp.StepParser.Tests
{
    /// <summary>
    /// Issue #173: backslash-escaped quotes inside quoted literals in grammar
    /// files must be honored by SplitAlternatives, ParseRightHandSide and
    /// CountUnquotedChar — \" does not close a "..." literal, \' does not close
    /// a '...' literal.
    /// </summary>
    public class QuotedLiteralEscapeTests
    {
        [Fact]
        public void EscapedDoubleQuote_InsideDoubleQuotedLiteral_IsOneSymbol()
        {
            var loader = new GrammarLoader();
            var content = "Grammar: T\n<a> ::= \"say \\\"hi\\\"\" \"!\"\n<start> ::= <a>\n";

            var grammar = loader.ParseGrammarContent(content, "t.grammar");

            var rule = Assert.Single(grammar.ProductionRules, r => r.Name == "a");
            Assert.Equal(new[] { "\"say \\\"hi\\\"\"", "\"!\"" }, rule.RightHandSide);
        }

        [Fact]
        public void EscapedPipe_InsideLiteral_DoesNotSplitAlternatives()
        {
            var loader = new GrammarLoader();
            var content = "Grammar: T\n<a> ::= \"x\\\"|y\" | \"z\"\n<start> ::= <a>\n";

            var grammar = loader.ParseGrammarContent(content, "t.grammar");

            var rules = grammar.ProductionRules.Where(r => r.Name == "a").ToList();
            Assert.Equal(2, rules.Count);
            Assert.Equal(new[] { "\"x\\\"|y\"" }, rules[0].RightHandSide);
            Assert.Equal(new[] { "\"z\"" }, rules[1].RightHandSide);
        }

        [Fact]
        public void EscapedSingleQuote_InsideSingleQuotedLiteral_IsOneSymbol()
        {
            var loader = new GrammarLoader();
            var content = "Grammar: T\n<a> ::= 'it\\'s'\n<start> ::= <a>\n";

            var grammar = loader.ParseGrammarContent(content, "t.grammar");

            var rule = Assert.Single(grammar.ProductionRules, r => r.Name == "a");
            Assert.Equal(new[] { "'it\\'s'" }, rule.RightHandSide);
        }

        [Fact]
        public void PlainLiterals_Alternation_Unchanged()
        {
            var loader = new GrammarLoader();
            var content = "Grammar: T\n<a> ::= \"x\" | \"y\" | 'z'\n<start> ::= <a>\n";

            var grammar = loader.ParseGrammarContent(content, "t.grammar");

            var rules = grammar.ProductionRules.Where(r => r.Name == "a").ToList();
            Assert.Equal(3, rules.Count);
            Assert.Equal(new[] { "\"x\"" }, rules[0].RightHandSide);
            Assert.Equal(new[] { "\"y\"" }, rules[1].RightHandSide);
            Assert.Equal(new[] { "'z'" }, rules[2].RightHandSide);
        }

        [Fact]
        public void NonTerminalAndBareWord_StillParsed()
        {
            var loader = new GrammarLoader();
            var content = "Grammar: T\n<a> ::= <b> WORD \"lit\"\n<b> ::= \"x\"\n<start> ::= <a>\n";

            var grammar = loader.ParseGrammarContent(content, "t.grammar");

            var rule = Assert.Single(grammar.ProductionRules, r => r.Name == "a");
            Assert.Equal(new[] { "b", "WORD", "\"lit\"" }, rule.RightHandSide);
        }
    }
}
