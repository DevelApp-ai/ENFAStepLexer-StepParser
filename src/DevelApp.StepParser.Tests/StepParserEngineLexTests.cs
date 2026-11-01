// Pinned tests for the lex-only public API StepParserEngine.Lex, added so
// that grammar input-conformance harnesses (GrammarRepositoryConformanceTests,
// used by Minotaur-Grammars CI) can assert lexical acceptance/rejection
// without paying for the GLR syntactic phase. Before this API the harness
// called Parse, which on large ambiguous grammars (CSharp10: 459 ambiguous
// productions) took tens of seconds per case and exceeded the Minotaur
// grammars CI timeout (Minotaur-Grammars #197).
//
// Contract pinned here:
//   1. Lex on lexable input: Success=true, token stream identical to the
//      Phase-1 tokens Parse emits, no lexer diagnostics.
//   2. Lex on unlexable input: Success=false with LX1001, exactly like the
//      lexer half of Parse.
//   3. Lex never populates parser-side fields (CognitiveGraph,
//      ResolvedTokens, PathCount, AmbiguousParses): those belong to the
//      syntactic phase that Lex deliberately skips.

using System;
using System.Linq;
using DevelApp.StepParser;
using Xunit;

namespace DevelApp.StepParser.Tests
{
    /// <summary>
    /// Pins <see cref="StepParserEngine.Lex(string, string)"/> /
    /// <see cref="StepParserEngine.Lex(ReadOnlyMemory{byte}, string)"/>:
    /// a public Phase-1-only entry point that returns the same token stream
    /// and lexer diagnostics as <see cref="StepParserEngine.Parse(string, string)"/>
    /// but skips the GLR syntactic phase entirely.
    /// </summary>
    public class StepParserEngineLexTests
    {
        private const string SimpleGrammar =
            "Grammar: LexApiPin\n" +
            "<start> ::= <word>+\n" +
            "<word> ::= /[a-z]+/\n" +
            "<whitespace> ::= /[ \t\r\n]+/ => { skip }\n";

        private const string LexableInput = "abc def ghi";

        [Fact]
        public void Lex_MatchableInput_SucceedsWithTokensAndNoDiagnostics()
        {
            using var engine = new StepParserEngine();
            engine.LoadGrammarFromContent(SimpleGrammar);

            var result = engine.Lex(LexableInput, "pin.lex");

            Assert.True(result.Success, "input fully consumed by the lexer must report Success");
            Assert.True(result.Tokens.Count > 0, "lexing must produce tokens");
            var lexerDiagnostics = result.Diagnostics
                .Where(d => d.Code == DiagnosticCodes.LexerUnexpectedInput
                         || d.Code == DiagnosticCodes.LexerStalled)
                .ToList();
            Assert.Empty(lexerDiagnostics);
            Assert.All(result.Tokens, t => Assert.True(t.Length > 0, "no zero-length tokens may be emitted"));
        }

        [Fact]
        public void Lex_GarbageInput_FailsWithLx1001()
        {
            using var engine = new StepParserEngine();
            engine.LoadGrammarFromContent(SimpleGrammar);

            var result = engine.Lex("!!! @@@ ???", "pin.lex");

            Assert.False(result.Success, "input the token rules cannot consume must report failure");
            Assert.Contains(result.Diagnostics,
                d => d.Code == DiagnosticCodes.LexerUnexpectedInput);
        }

        [Fact]
        public void Lex_TokenStreamMatchesParsePhase1()
        {
            // Parse = Lex (Phase 1) + GLR (Phase 2): for any input the
            // emitted token stream must be identical between the two entry
            // points (shared RunLexingPhase), both when the input lexes and
            // when it does not. Success is deliberately NOT compared: for
            // Parse it means a complete parse; for Lex only that the lexer
            // consumed the input (a grammar can lex an input that its
            // productions cannot fully parse, which is exactly the
            // lex-only conformance use case).
            using var parseEngine = new StepParserEngine();
            parseEngine.LoadGrammarFromContent(SimpleGrammar);
            using var lexEngine = new StepParserEngine();
            lexEngine.LoadGrammarFromContent(SimpleGrammar);

            foreach (var input in new[] { LexableInput, "abc !!! def" })
            {
                var parsed = parseEngine.Parse(input, "pin.lex");
                var lexed = lexEngine.Lex(input, "pin.lex");

                Assert.Equal(
                    parsed.Tokens.Select(t => (t.Type, t.StartPosition, t.Length)),
                    lexed.Tokens.Select(t => (t.Type, t.StartPosition, t.Length)));

                // Lexer diagnostics must also match: LX1001/LX1002 come from
                // the shared Phase 1 (a parse can additionally carry parser
                // diagnostics, which the filter below tolerates).
                Assert.Equal(
                    parsed.Diagnostics
                        .Where(d => d.Code == DiagnosticCodes.LexerUnexpectedInput
                                 || d.Code == DiagnosticCodes.LexerStalled)
                        .Select(d => d.Code),
                    lexed.Diagnostics.Select(d => d.Code));
            }
        }

        [Fact]
        public void Lex_DoesNotPopulateParserSideResults()
        {
            using var engine = new StepParserEngine();
            engine.LoadGrammarFromContent(SimpleGrammar);

            var result = engine.Lex(LexableInput, "pin.lex");

            // The syntactic phase is skipped: parser-side fields stay unset.
            Assert.Null(result.CognitiveGraph);
            Assert.Empty(result.ResolvedTokens);
            Assert.Equal(0, result.PathCount);
            Assert.Empty(result.AmbiguousParses);
        }

        [Fact]
        public void Lex_StringOverload_MatchesByteOverload()
        {
            using var engine = new StepParserEngine();
            engine.LoadGrammarFromContent(SimpleGrammar);

            var fromString = engine.Lex(LexableInput, "pin.lex");
            var fromBytes = engine.Lex(
                System.Text.Encoding.UTF8.GetBytes(LexableInput), "pin.lex");

            Assert.Equal(fromString.Success, fromBytes.Success);
            Assert.Equal(
                fromString.Tokens.Select(t => (t.Type, t.StartPosition, t.Length)),
                fromBytes.Tokens.Select(t => (t.Type, t.StartPosition, t.Length)));
        }

        [Fact]
        public void Lex_LargeInput_CompletesWithoutSyntacticPhase()
        {
            // The reason this API exists: a lex-only pass over an input must
            // not run the GLR phase. This grammar is lexically ambiguous
            // (result.Tokens is the per-path union by design, issue #109),
            // so the token list grows multiplicatively with input length —
            // a level where Parse would also have to chew through the same
            // union in its GLR phase, while Lex handles it in milliseconds
            // and asserts the parser side never ran.
            // Words are letters only: the /[a-z]+/ token rule cannot match
            // digits, and the test asserts lexer coverage, not rejection.
            var words = Enumerable.Range(0, 12)
                .Select(i => new string((char)('a' + i % 26), 1 + i / 26));
            var input = string.Join(" ", words);

            using var engine = new StepParserEngine();
            engine.LoadGrammarFromContent(SimpleGrammar);

            var result = engine.Lex(input, "pin.lex");

            Assert.True(result.Success, "word list must lex to completion");
            Assert.DoesNotContain(result.Diagnostics,
                d => d.Code == DiagnosticCodes.LexerStalled);

            // Distinct token spans must cover the input exactly: word and
            // skip spans partition the input (the raw token list contains
            // per-path duplicates whose lengths would over-count).
            var distinctCoverage = result.Tokens
                .Select(t => (t.StartPosition, t.Length))
                .Distinct()
                .Sum(s => s.Length);
            Assert.Equal(input.Length, distinctCoverage);

            // The syntactic phase never ran.
            Assert.Null(result.CognitiveGraph);
            Assert.Empty(result.ResolvedTokens);
            Assert.Equal(0, result.PathCount);
        }
    }
}
