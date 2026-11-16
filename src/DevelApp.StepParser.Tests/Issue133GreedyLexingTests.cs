using System;
using System.Diagnostics;
using System.Linq;
using System.Text;
using DevelApp.StepLexer;
using DevelApp.StepParser;
using Xunit;

namespace DevelApp.StepParser.Tests
{
    /// <summary>
    /// Issue #133 (Gap 2 of #129): the deterministic greedy/longest-match
    /// lexing mode exposed on <see cref="StepParserEngine"/> as
    /// <see cref="StepParserEngine.LexingMode"/>. In greedy mode the lexer
    /// keeps only the longest match per position (priority tie-break), so
    /// file-scale input produces a single coherent token path in linear
    /// time; the default exploratory mode is unchanged.
    /// </summary>
    public class Issue133GreedyLexingTests
    {
        private const string Grammar = "Grammar: Issue133\n" +
                                       "<start> ::= <kw> <word>+\n" +
                                       "<kw (priority:10)> ::= \"if\"\n" +
                                       "<word> ::= /[a-z]+/\n" +
                                       "<ws> ::= /[ \\t\\r\\n]+/ => { skip }\n";

        [Fact]
        public void GreedyMode_Lex_KeywordWinsTieByPriority_SingleTokenPath()
        {
            using var engine = new StepParserEngine();
            engine.LoadGrammarFromContent(Grammar);
            engine.LexingMode = LexingMode.GreedyLongestMatch;

            var result = engine.Lex("if x y");

            Assert.True(result.Success, "greedy lexing must consume the whole input");
            Assert.Equal(new[] { "kw", "word", "word" },
                result.Tokens.Select(t => t.Type).ToArray());
            Assert.Equal(new[] { "if", "x", "y" },
                result.Tokens.Select(t => t.Value).ToArray());
        }

        [Fact]
        public void GreedyMode_Parse_StillDerivesTheGrammar()
        {
            using var engine = new StepParserEngine();
            engine.LoadGrammarFromContent(Grammar);
            engine.LexingMode = LexingMode.GreedyLongestMatch;

            var result = engine.Parse("if x y");

            Assert.True(result.Success, "greedy-mode token stream must parse");
            Assert.Equal(new[] { "kw", "word", "word" },
                result.Tokens.Select(t => t.Type).ToArray());
        }

        [Fact]
        public void ExploratoryDefault_Lex_UnionsTokensAcrossPaths()
        {
            // Default mode is unchanged: at "if" both the keyword literal
            // and the identifier match, and the per-path union keeps both
            // tokens (issues #109/#112 by design).
            using var engine = new StepParserEngine();
            engine.LoadGrammarFromContent(Grammar);

            var result = engine.Lex("if x");

            Assert.True(result.Success);
            Assert.Contains(result.Tokens, t => t.Type == "kw" && t.Value == "if");
            Assert.Contains(result.Tokens, t => t.Type == "word" && t.Value == "if");
            Assert.Contains(result.Tokens, t => t.Type == "word" && t.Value == "x");
        }

        [Fact]
        public void GreedyMode_Lex_TenKbInput_UnderOneSecond()
        {
            // Acceptance criterion of issue #133 / Minotaur-Grammars #194:
            // lex a 10 KB input in < 1 s.
            var sb = new StringBuilder();
            var i = 0;
            while (sb.Length < 10_240)
            {
                sb.Append("if x").Append(i).Append(" y").Append(i).Append('\n');
                i++;
            }

            using var engine = new StepParserEngine();
            engine.LoadGrammarFromContent(Grammar);
            engine.LexingMode = LexingMode.GreedyLongestMatch;

            var sw = Stopwatch.StartNew();
            var result = engine.Lex(sb.ToString());
            sw.Stop();

            Assert.True(result.Success);
            Assert.True(sw.ElapsedMilliseconds < 1_000,
                $"10 KB input lexed in {sw.ElapsedMilliseconds} ms; acceptance is < 1000 ms (issue #133)");
        }
    }
}
