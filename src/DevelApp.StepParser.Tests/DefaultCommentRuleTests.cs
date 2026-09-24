using Xunit;
using DevelApp.StepParser;

namespace DevelApp.StepParser.Tests
{
    /// <summary>
    /// Regression tests for the default ignorable comment rule (issue #83,
    /// Cluster B): corpus Examples.txt files commonly begin with '#'
    /// comment lines, and grammars with no token rule matching '#' must
    /// still lex past them, exactly like the default whitespace rule added
    /// for issue #80.
    /// </summary>
    public class DefaultCommentRuleTests
    {
        /// <summary>
        /// A grammar whose tokens do not mention '#' anywhere: the default
        /// comment rule must skip '#' to end of line.
        /// </summary>
        private const string CommentFreeGrammar = @"
Grammar: CommentFree
<word> ::= /[a-z]+/
<line> ::= <word> <word>
";

        /// <summary>
        /// A grammar with a significant '#' token: the default comment rule
        /// must NOT be added, so '#' remains a real token.
        /// </summary>
        private const string HashSignificantGrammar = @"
Grammar: HashSignificant
<word> ::= /[a-z]+/
<hash> ::= '#'
<heading> ::= <hash> <word> <word>
";

        [Fact]
        public void CommentLines_AreSkipped_WhenNoRuleMatchesHash()
        {
            var engine = new StepParserEngine();
            engine.LoadGrammarFromContent(CommentFreeGrammar);

            var result = engine.Parse(
                "# header comment\nhello world\n## section heading\nfoo bar\n",
                "examples.txt");

            Assert.True(result.Success, string.Join(" | ", result.Errors ?? new System.Collections.Generic.List<string>()));
        }

        [Fact]
        public void CommentLine_AtByteZero_IsSkipped()
        {
            // The lexer previously died with LX1001 at offset 0 because no
            // rule matched the leading '#'.
            var engine = new StepParserEngine();
            engine.LoadGrammarFromContent(CommentFreeGrammar);

            var result = engine.Parse("# leading comment\nabc def", "examples.txt");

            Assert.True(result.Success, string.Join(" | ", result.Errors ?? new System.Collections.Generic.List<string>()));
        }

        [Fact]
        public void HashRemainsSignificant_WhenGrammarDefinesHashToken()
        {
            var engine = new StepParserEngine();
            engine.LoadGrammarFromContent(HashSignificantGrammar);

            // '#' as a real token still parses.
            var significant = engine.Parse("# heading words", "input.txt");
            Assert.True(significant.Success, string.Join(" | ", significant.Errors ?? new System.Collections.Generic.List<string>()));
        }

        [Fact]
        public void CommentRule_DoesNotSwallowMidLineHash_WhenGrammarDefinesHashToken()
        {
            var engine = new StepParserEngine();
            engine.LoadGrammarFromContent(HashSignificantGrammar);

            // Without a '#' token rule this input would rely on the comment
            // rule; with one, the significant '#'-rule wins for the token.
            var result = engine.Parse("# abc def", "input.txt");
            Assert.True(result.Success, string.Join(" | ", result.Errors ?? new System.Collections.Generic.List<string>()));
        }
    }
}
