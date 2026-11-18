using Xunit;
using DevelApp.StepParser;
using DevelApp.StepLexer;
using System;
using System.Linq;

namespace DevelApp.StepParser.Tests
{
    /// <summary>
    /// Tests for <see cref="RealTimeParserSession"/>, the incremental
    /// real-time parsing session for IDE and editor integration.
    /// </summary>
    public class RealTimeParserSessionTests
    {
        private static readonly string ListGrammar = TestGrammars.Get("test-grammars/step-parser-tests/RealTimeParserSessionTests/ListGrammar.grammar");

        private static readonly string LineGrammar = TestGrammars.Get("test-grammars/step-parser-tests/RealTimeParserSessionTests/LineGrammar.grammar");

        private static RealTimeParserSession CreateSession(string grammar, string source)
        {
            var session = new RealTimeParserSession(grammar, "test.txt");
            session.Initialize(source);
            return session;
        }

        [Fact]
        public void Initialize_LexesWholeInputWithBytePositions()
        {
            var session = CreateSession(ListGrammar, "abc;123;def");

            Assert.Equal(5, session.Tokens.Count);
            Assert.Equal(0, session.Version);

            Assert.Equal("IDENTIFIER", session.Tokens[0].Type);
            Assert.Equal("abc", session.Tokens[0].Value);
            Assert.Equal(0, session.Tokens[0].StartPosition);
            Assert.Equal(3, session.Tokens[0].Length);

            Assert.Equal("NUMBER", session.Tokens[2].Type);
            Assert.Equal("123", session.Tokens[2].Value);
            Assert.Equal(4, session.Tokens[2].StartPosition);
            Assert.Equal(3, session.Tokens[2].Length);

            Assert.Equal("def", session.Tokens[4].Value);
            Assert.Equal(8, session.Tokens[4].StartPosition);
        }

        [Fact]
        public void ApplyEdit_InMiddleOfToken_MergesWithInsertedText()
        {
            // The merge-at-boundary case: inserting a digit at the exact end
            // of a NUMBER token must re-lex that token so "123" becomes "1234".
            var session = CreateSession(ListGrammar, "123x7");
            Assert.Equal("123", session.Tokens[0].Value);

            session.ApplyEdit(3, 0, "4");

            Assert.Equal("1234", session.Tokens[0].Value);
            Assert.Equal("x7", session.Tokens[1].Value);
            Assert.Equal("1234x7", session.Text);
        }

        [Fact]
        public void ApplyEdit_ReplacesTokenContent()
        {
            var session = CreateSession(ListGrammar, "abc;123;def;ghi");
            session.ApplyEdit(4, 3, "9");

            Assert.Equal("abc;9;def;ghi", session.Text);
            Assert.Equal(7, session.Tokens.Count);
            Assert.Equal("9", session.Tokens[2].Value);
            Assert.Equal(4, session.Tokens[2].StartPosition);
            Assert.Equal(1, session.Tokens[2].Length);
        }

        [Fact]
        public void ApplyEdit_ReusesUnchangedTailTokens()
        {
            var session = CreateSession(ListGrammar, "abc;123;def;ghi");
            var oldDefToken = session.Tokens[4];
            var oldGhiToken = session.Tokens[session.Tokens.Count - 1];

            session.ApplyEdit(4, 3, "9");

            // Tokens after the edit must be reused (same object identity),
            // with positions shifted by the edit delta (-2).
            Assert.Same(oldDefToken, session.Tokens[4]);
            Assert.Same(oldGhiToken, session.Tokens[6]);
            Assert.Equal(6, oldDefToken.StartPosition);
            Assert.Equal(10, oldGhiToken.StartPosition);

            // Reused tokens keep their adjusted line/column location
            Assert.Equal(1, oldDefToken.Location.StartLine);
            Assert.Equal(7, oldDefToken.Location.StartColumn);
        }

        [Fact]
        public void ApplyEdit_AppendAtEnd_ExtendsTokenList()
        {
            var session = CreateSession(ListGrammar, "abc;123");

            session.ApplyEdit(7, 0, ";def");

            Assert.Equal("abc;123;def", session.Text);
            Assert.Equal(5, session.Tokens.Count);
            Assert.Equal("def", session.Tokens[4].Value);
            Assert.Equal(8, session.Tokens[4].StartPosition);
        }

        [Fact]
        public void ApplyEdit_MultiLineEdit_AdjustsLineAndColumnOfReusedTokens()
        {
            var session = CreateSession(LineGrammar, "abc\ndef\nghi");
            var oldGhiToken = session.Tokens[4];

            // Insert text at the start of line 2
            session.ApplyEdit(4, 0, "X");

            Assert.Equal("abc\nXdef\nghi", session.Text);

            // The token on the edited line was re-lexed and merged
            Assert.Equal("Xdef", session.Tokens[2].Value);

            // The reused 'ghi' token keeps its line but stays on line 3
            Assert.Same(oldGhiToken, session.Tokens[4]);
            Assert.Equal(3, oldGhiToken.Location.StartLine);
            Assert.Equal(1, oldGhiToken.Location.StartColumn);
            Assert.Equal(9, oldGhiToken.StartPosition);
        }

        [Fact]
        public void ApplyEdit_MultiLineInsertion_ShiftsLaterLines()
        {
            var session = CreateSession(LineGrammar, "abc\ndef\nghi");
            var oldGhiToken = session.Tokens[4];

            // Insert a whole new line (including its newline) after line 1
            session.ApplyEdit(4, 0, "xyz\n");

            Assert.Equal("abc\nxyz\ndef\nghi", session.Text);

            // The reused 'ghi' token moves down one line
            Assert.Same(oldGhiToken, session.Tokens[6]);
            Assert.Equal(4, oldGhiToken.Location.StartLine);
            Assert.Equal(1, oldGhiToken.Location.StartColumn);
            Assert.Equal(12, oldGhiToken.StartPosition);
        }

        [Fact]
        public void ApplyEdit_IncrementsVersionAndFiresEvent()
        {
            var session = CreateSession(ListGrammar, "abc;123");
            Assert.Equal(0, session.Version);

            var eventCount = 0;
            session.Reparsed += (s, e) => eventCount++;

            session.ApplyEdit(3, 0, ";x");
            Assert.Equal(1, session.Version);
            Assert.Equal(1, eventCount);

            session.ApplyEdit(0, 0, "y");
            Assert.Equal(2, session.Version);
            Assert.Equal(2, eventCount);
        }

        [Fact]
        public void Initialize_FiresReparsedEvent()
        {
            var session = new RealTimeParserSession(ListGrammar, "test.txt");
            var eventCount = 0;
            session.Reparsed += (s, e) => eventCount++;

            session.Initialize("abc");

            Assert.Equal(1, eventCount);
            Assert.Equal(0, session.Version);
        }

        [Fact]
        public void ApplyEdit_OutOfBounds_Throws()
        {
            var session = CreateSession(ListGrammar, "abc");

            Assert.Throws<ArgumentOutOfRangeException>(() => session.ApplyEdit(-1, 0, "x"));
            Assert.Throws<ArgumentOutOfRangeException>(() => session.ApplyEdit(4, 0, "x"));
            Assert.Throws<ArgumentOutOfRangeException>(() => session.ApplyEdit(1, 5, "x"));
        }

        [Fact]
        public void GetTokenAt_ReturnsContainingToken()
        {
            var session = CreateSession(ListGrammar, "abc;123;def");

            Assert.Same(session.Tokens[0], session.GetTokenAt(0));
            Assert.Same(session.Tokens[0], session.GetTokenAt(2));
            Assert.Same(session.Tokens[2], session.GetTokenAt(5));
            Assert.Null(session.GetTokenAt(15));
        }

        [Fact]
        public void GetTokensInRange_ReturnsOverlappingTokens()
        {
            var session = CreateSession(ListGrammar, "abc;123;def");

            var inRange = session.GetTokensInRange(4, 8).ToList();
            Assert.Equal(2, inRange.Count);
            Assert.Contains(session.Tokens[2], inRange);
            Assert.Contains(session.Tokens[3], inRange);
        }

        [Fact]
        public void Session_FromEngineWithLoadedGrammar_Works()
        {
            var engine = new StepParserEngine();
            engine.LoadGrammarFromContent(ListGrammar);

            var session = new RealTimeParserSession(engine, "test.txt");
            session.Initialize("abc;123");
            session.ApplyEdit(3, 0, ";x");

            Assert.Equal("abc;x;123", session.Text);
            Assert.Equal(5, session.Tokens.Count);
        }

        [Fact]
        public void Session_FromEngineWithoutGrammar_ThrowsOnInitialize()
        {
            var engine = new StepParserEngine();
            var session = new RealTimeParserSession(engine, "test.txt");

            Assert.Throws<InvalidOperationException>(() => session.Initialize("abc"));
        }

        [Fact]
        public void Parse_ReturnsFullParseResult()
        {
            var grammar = TestGrammars.Get("test-grammars/step-parser-tests/RealTimeParserSessionTests/ExpressionGrammar.grammar");
            var session = new RealTimeParserSession(grammar, "test.txt");
            session.Initialize("123");

            var result = session.Parse();
            Assert.True(result.Success);

            session.ApplyEdit(3, 0, "4");
            Assert.Equal("1234", session.Text);

            var updated = session.Parse();
            Assert.True(updated.Success);
        }

        [Fact]
        public void ApplyEdit_DeleteAll_ProducesEmptyTokenList()
        {
            var session = CreateSession(ListGrammar, "abc;123");

            session.ApplyEdit(0, 7, "");

            Assert.Equal(string.Empty, session.Text);
            Assert.Empty(session.Tokens);
        }

        [Fact]
        public void ApplyEdit_StopsRelexAtTailRealignment_MatchesFullLex()
        {
            // 40 identifier tokens with 39 separators: the unchanged
            // suffix is far bigger than the realignment safety window.
            var source = string.Join(";", Enumerable.Range(0, 40).Select(i => "tok" + i));
            var session = CreateSession(ListGrammar, source);
            Assert.Equal(79, session.Tokens.Count);
            var oldLastToken = session.Tokens[session.Tokens.Count - 1];

            // Insert a digit at the start of the second token
            // ("tok0;tok1;tok2..." -> "tok0;9tok1;tok2...").
            session.ApplyEdit(5, 0, "9");

            // Windowed re-lex (issue #144): only the edit vicinity plus
            // the safety window was lexed, not the whole suffix.
            Assert.True(session.LastRelexTokenCount > 0, "the edit must lex some tokens");
            Assert.True(
                session.LastRelexTokenCount < session.Tokens.Count / 2,
                $"expected early stop, lexed {session.LastRelexTokenCount} of {session.Tokens.Count} tokens");

            // The realigned suffix was spliced by identity.
            Assert.True(session.LastReusedTokenCount > 0);
            Assert.Same(oldLastToken, session.Tokens[session.Tokens.Count - 1]);

            // Token-for-token agreement with a full Initialize lex of the
            // final text: types, values, byte positions and lengths.
            var reference = CreateSession(ListGrammar, session.Text);
            Assert.Equal(reference.Tokens.Count, session.Tokens.Count);
            for (int i = 0; i < reference.Tokens.Count; i++)
            {
                Assert.Equal(reference.Tokens[i].Type, session.Tokens[i].Type);
                Assert.Equal(reference.Tokens[i].Value, session.Tokens[i].Value);
                Assert.Equal(reference.Tokens[i].StartPosition, session.Tokens[i].StartPosition);
                Assert.Equal(reference.Tokens[i].Length, session.Tokens[i].Length);
            }
        }

        [Fact]
        public void ApplyEdit_WindowedRelex_InsertsLine_ShiftsSplicedTailLocations()
        {
            // 20 lines, one token per line plus newline tokens.
            var source = string.Join("\n", Enumerable.Range(0, 20).Select(i => "line" + i));
            var session = CreateSession(LineGrammar, source);
            Assert.Equal(39, session.Tokens.Count);
            var oldLastLineToken = session.Tokens[session.Tokens.Count - 1];
            Assert.Equal("line19", oldLastLineToken.Value);

            // Insert a whole new line after "line0\n" (offset 6).
            session.ApplyEdit(6, 0, "inserted\n");

            // Early stop again.
            Assert.True(session.LastRelexTokenCount < session.Tokens.Count / 2);

            // The last line's token survives by identity with shifted
            // byte position and line/column location.
            Assert.Same(oldLastLineToken, session.Tokens[session.Tokens.Count - 1]);

            // Token-for-token agreement with a full Initialize lex of the
            // final text, including locations on the spliced tail.
            var reference = CreateSession(LineGrammar, session.Text);
            Assert.Equal(reference.Tokens.Count, session.Tokens.Count);
            for (int i = 0; i < reference.Tokens.Count; i++)
            {
                Assert.Equal(reference.Tokens[i].Type, session.Tokens[i].Type);
                Assert.Equal(reference.Tokens[i].Value, session.Tokens[i].Value);
                if (i >= reference.Tokens.Count - 3)
                {
                    Assert.Equal(reference.Tokens[i].StartPosition, session.Tokens[i].StartPosition);
                    Assert.Equal(reference.Tokens[i].Location.StartLine, session.Tokens[i].Location.StartLine);
                    Assert.Equal(reference.Tokens[i].Location.StartColumn, session.Tokens[i].Location.StartColumn);
                }
            }
        }
    }
}
