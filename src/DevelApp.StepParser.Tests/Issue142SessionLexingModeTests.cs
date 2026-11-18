// Regression tests for issue #142: RealTimeParserSession always ran its
// private lexer in exploratory mode, so overlapping token rules (keyword vs
// identifier) multiplied the token stream with no way for consumers to select
// the greedy mode that StepLexer gained in #135/#137. The session now exposes
// a LexingMode toggle (default Exploratory, backward compatible).

using System.Linq;
using DevelApp.StepLexer;
using DevelApp.StepParser;
using Xunit;

namespace DevelApp.StepParser.Tests
{
    public class Issue142SessionLexingModeTests
    {
        // Both rules match "public" and "class" (6/5 chars) equally, so
        // exploratory mode forks a path per rule and emits duplicated tokens.
        private const string OverlappingGrammar =
            "Grammar: Issue142\n" +
            "<start> ::= <KEYWORD> | <IDENTIFIER>\n" +
            "<KEYWORD> ::= /public|class/\n" +
            "<IDENTIFIER> ::= /[a-zA-Z_][a-zA-Z0-9_]*/\n" +
            "<WS> ::= /[ \\t\\r\\n]+/ => { skip }\n";

        private static RealTimeParserSession CreateSession(string source, LexingMode mode)
        {
            var session = new RealTimeParserSession(OverlappingGrammar, "test.txt");
            session.LexingMode = mode;
            session.Initialize(source);
            return session;
        }

        [Fact]
        public void DefaultMode_IsExploratory_BackwardCompatible()
        {
            var session = new RealTimeParserSession(OverlappingGrammar, "test.txt");
            Assert.Equal(LexingMode.Exploratory, session.LexingMode);
        }

        [Fact]
        public void ExploratoryMode_UnionsOverlappingMatches()
        {
            // Pins the defect behavior the mode toggle exists for: "public"
            // matches both rules, so exploratory emits one token per path.
            var session = CreateSession("public", LexingMode.Exploratory);
            Assert.Equal(2, session.Tokens.Count);
        }

        [Fact]
        public void GreedyMode_EmitsEachOverlappingTokenOnce()
        {
            var session = CreateSession("public class x", LexingMode.GreedyLongestMatch);
            Assert.Equal(3, session.Tokens.Count);
            Assert.Equal("public", session.Tokens[0].Value);
            Assert.Equal("class", session.Tokens[1].Value);
            Assert.Equal("x", session.Tokens[2].Value);
        }

        [Fact]
        public void GreedyMode_StaysLinear_OnRepeatedOverlappingInput()
        {
            // The issue's repro: a 56-character snippet of overlapping
            // keyword/identifier words exhausted memory in exploratory mode.
            var snippet = string.Join(" ", Enumerable.Repeat("public class identifier", 8));
            var session = CreateSession(snippet, LexingMode.GreedyLongestMatch);
            Assert.Equal(24, session.Tokens.Count);
            Assert.All(session.Tokens, t => Assert.False(string.IsNullOrEmpty(t.Value)));
        }

        [Fact]
        public void GreedyMode_AppliesToIncrementalRelex_AfterApplyEdit()
        {
            var session = CreateSession("public x", LexingMode.GreedyLongestMatch);
            Assert.Equal(2, session.Tokens.Count);

            // Insert another overlapping keyword; the re-lex must stay greedy.
            session.ApplyEdit(7, 0, " class ");
            Assert.Equal(3, session.Tokens.Count);
            Assert.Equal("class", session.Tokens[1].Value);
        }
    }
}
