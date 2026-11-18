// Regression tests for issue #144: ApplyEdit used to re-lex from the edit
// offset to EOF (O(suffix)); now it lexes a bounded window and reuses the old
// token tail once the re-lexed stream realigns with it (safety run of
// type/value/context/position matches). These tests pin correctness (the
// merged stream must equal a full re-lex) and that the window actually
// engages (LastRelexedTokenCount stays small on a large document).

using System.Collections.Generic;
using System.Linq;
using DevelApp.StepParser;
using Xunit;

namespace DevelApp.StepParser.Tests
{
    public class Issue144WindowedRelexTests
    {
        private const string WordGrammar =
            "Grammar: Issue144\n" +
            "<start> ::= <WORD>*\n" +
            "<WORD> ::= /[a-zA-Z0-9]+/\n" +
            "<WS> ::= /[ \\t\\r\\n]+/ => { skip }\n";

        private static string BuildDoc(int lines)
        {
            var parts = new List<string>(lines);
            for (int i = 0; i < lines; i++)
            {
                parts.Add("word" + i);
            }
            return string.Join("\n", parts) + "\n";
        }

        private static void AssertSameTokens(RealTimeParserSession expected, RealTimeParserSession actual)
        {
            Assert.Equal(expected.Tokens.Count, actual.Tokens.Count);
            for (int i = 0; i < expected.Tokens.Count; i++)
            {
                Assert.Equal(expected.Tokens[i].Type, actual.Tokens[i].Type);
                Assert.Equal(expected.Tokens[i].Value, actual.Tokens[i].Value);
                Assert.Equal(expected.Tokens[i].StartPosition, actual.Tokens[i].StartPosition);
            }
        }

        [Fact]
        public void MidDocumentInsert_RelexesOnlyWindow_AndMatchesFullRelex()
        {
            var doc = BuildDoc(3000);
            var offset = doc.IndexOf("word1500", System.StringComparison.Ordinal);
            Assert.True(offset > 0);

            var session = new RealTimeParserSession(WordGrammar, "test.txt");
            session.Initialize(doc);
            var totalTokens = session.Tokens.Count;
            Assert.Equal(3000, totalTokens);

            session.ApplyEdit(offset + 4, 0, "x"); // "word1500" -> "wordx1500"
            var edited = doc.Substring(0, offset + 4) + "x" + doc.Substring(offset + 4);

            // Window engaged: far fewer tokens re-lexed than a full re-lex.
            Assert.True(session.LastRelexedTokenCount < 100,
                $"windowed re-lex should stop after realignment, re-lexed {session.LastRelexedTokenCount} of {totalTokens}");
            Assert.True(session.LastReuseRatio > 0.99, $"reuse ratio {session.LastReuseRatio}");

            // Correctness: identical to a from-scratch full lex of the edited text.
            var fresh = new RealTimeParserSession(WordGrammar, "test.txt");
            fresh.Initialize(edited);
            AssertSameTokens(fresh, session);
        }

        [Fact]
        public void MidDocumentDelete_MatchesFullRelex()
        {
            var doc = BuildDoc(3000);
            var offset = doc.IndexOf("word1500", System.StringComparison.Ordinal);

            var session = new RealTimeParserSession(WordGrammar, "test.txt");
            session.Initialize(doc);
            session.ApplyEdit(offset, 7, ""); // delete "word1500"
            var edited = doc.Substring(0, offset) + doc.Substring(offset + 7);

            Assert.True(session.LastRelexedTokenCount < 100);

            var fresh = new RealTimeParserSession(WordGrammar, "test.txt");
            fresh.Initialize(edited);
            AssertSameTokens(fresh, session);
        }

        [Fact]
        public void AppendAtEnd_MatchesFullRelex()
        {
            var doc = BuildDoc(3000);
            var session = new RealTimeParserSession(WordGrammar, "test.txt");
            session.Initialize(doc);
            session.ApplyEdit(doc.Length, 0, "tail");
            var edited = doc + "tail";

            var fresh = new RealTimeParserSession(WordGrammar, "test.txt");
            fresh.Initialize(edited);
            AssertSameTokens(fresh, session);
        }

        [Fact]
        public void ManySequentialEdits_StayCorrect()
        {
            var doc = BuildDoc(1500);
            var session = new RealTimeParserSession(WordGrammar, "test.txt");
            session.Initialize(doc);

            var current = doc;
            for (int i = 0; i < 25; i++)
            {
                var marker = "word" + (100 + i * 40);
                var at = current.IndexOf(marker, System.StringComparison.Ordinal);
                Assert.True(at > 0);
                current = current.Substring(0, at) + "zz" + current.Substring(at);
                session.ApplyEdit(at, 0, "zz");
            }

            var fresh = new RealTimeParserSession(WordGrammar, "test.txt");
            fresh.Initialize(current);
            AssertSameTokens(fresh, session);
        }
    }
}
