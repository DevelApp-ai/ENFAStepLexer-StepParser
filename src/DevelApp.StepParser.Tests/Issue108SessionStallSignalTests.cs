// Regression tests for issue #108: when no token rule matches at the current
// position, lexing used to stop silently - no error, no diagnostic, and the
// result only covered a prefix of the input. RealTimeParserSession now
// exposes LastUnmatchedOffset / LastLexConsumedAllInput so callers can detect
// the truncation (the engine-level Lex/Parse path reports LX1001).

using DevelApp.StepParser;
using Xunit;

namespace DevelApp.StepParser.Tests
{
    public class Issue108SessionStallSignalTests
    {
        private const string WordGrammar =
            "Grammar: Issue108\n" +
            "<start> ::= <WORD>*\n" +
            "<WORD> ::= /[a-zA-Z]+/\n" +
            "<WS> ::= /[ \\t\\r\\n]+/ => { skip }\n";

        [Fact]
        public void CleanInput_ConsumedAll_NoUnmatchedOffset()
        {
            var session = new RealTimeParserSession(WordGrammar, "test.txt");
            session.Initialize("abc def");
            Assert.True(session.LastLexConsumedAllInput);
            Assert.Equal(-1, session.LastUnmatchedOffset);
            Assert.Equal(2, session.Tokens.Count);
        }

        [Fact]
        public void UnmatchedInput_SignalsOffset_AndPrefixOnlyTokens()
        {
            // '\u20ac' (euro sign, 3 UTF-8 bytes) matches no rule: lexing
            // stalls at byte offset 4 in "abc € def".
            var session = new RealTimeParserSession(WordGrammar, "test.txt");
            session.Initialize("abc \u20ac def");

            Assert.False(session.LastLexConsumedAllInput);
            Assert.Equal(4, session.LastUnmatchedOffset);
            // Tokens cover only the prefix before the stall - but now the
            // caller can see that instead of getting a silent truncation.
            Assert.Single(session.Tokens);
            Assert.Equal("abc", session.Tokens[0].Value);
        }

        [Fact]
        public void UnmatchedInputAfterEdit_SignalsNewOffset()
        {
            var session = new RealTimeParserSession(WordGrammar, "test.txt");
            session.Initialize("abc def");
            Assert.True(session.LastLexConsumedAllInput);

            // Insert a euro sign at offset 4 ("abc " is 4 bytes).
            session.ApplyEdit(4, 0, "\u20ac ");
            Assert.False(session.LastLexConsumedAllInput);
            Assert.Equal(4, session.LastUnmatchedOffset);
        }

        [Fact]
        public void FixedInputAfterEdit_ClearsTheSignal()
        {
            var session = new RealTimeParserSession(WordGrammar, "test.txt");
            session.Initialize("abc \u20ac def");
            Assert.False(session.LastLexConsumedAllInput);

            // Delete the euro sign (bytes 4..7) plus following space.
            session.ApplyEdit(4, 4, "");
            Assert.True(session.LastLexConsumedAllInput);
            Assert.Equal(-1, session.LastUnmatchedOffset);
            Assert.Equal(2, session.Tokens.Count);
        }
    }
}
