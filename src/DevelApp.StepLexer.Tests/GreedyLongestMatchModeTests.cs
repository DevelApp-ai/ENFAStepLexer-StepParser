using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using DevelApp.StepLexer;
using Xunit;

namespace DevelApp.StepLexer.Tests
{
    /// <summary>
    /// Tests for the deterministic greedy/longest-match lexing mode
    /// (issue #133, Gap 2 of #129): at each position only the longest
    /// match is kept (ties broken by rule priority, then rule order), so
    /// exactly one lexer path survives every step and file-scale lexing
    /// stays linear with a single coherent token path. The default
    /// <see cref="LexingMode.Exploratory"/> mode keeps forking paths and
    /// is pinned unchanged.
    /// </summary>
    public class GreedyLongestMatchModeTests
    {
        private static (List<StepToken> Tokens, int MaxActivePaths, bool Completed) RunToCompletion(
            StepLexer lexer, byte[] input)
        {
            lexer.Initialize(input, "test.txt");
            var tokens = new List<StepToken>();
            var maxActivePaths = 0;
            var guard = 0;
            while (lexer.ActivePaths.Any(p => p.IsValid && p.Position < input.Length))
            {
                var result = lexer.Step();
                tokens.AddRange(result.NewTokens);
                maxActivePaths = Math.Max(maxActivePaths, lexer.ActivePaths.Count);
                if (++guard > 1_000_000)
                {
                    break;
                }
            }
            return (tokens, maxActivePaths, lexer.CompletedInput);
        }

        private static StepLexer CreateGreedy(params TokenRule[] rules)
        {
            var lexer = new StepLexer { Mode = LexingMode.GreedyLongestMatch };
            foreach (var rule in rules)
            {
                lexer.AddRule(rule);
            }
            return lexer;
        }

        [Fact]
        public void GreedyMode_SamePatternRules_CollapseToSinglePath_WithPriorityTieBreak()
        {
            // Danish-grammar shape: several token rules share the identical
            // pattern /[a-z]+/ (noun/adjective/verb stems). In exploratory
            // mode every word position forks a path per rule; greedy mode
            // keeps one path and the highest-priority rule wins the tie.
            var lexer = CreateGreedy(
                new TokenRule("noun-stem", "/[a-z]+/", "", 5),
                new TokenRule("adjective-stem", "/[a-z]+/", "", 4),
                new TokenRule("verb-stem", "/[a-z]+/", "", 3),
                new TokenRule("ws", "/[ \\t\\r\\n]+/") { IsSkippable = true });

            var (tokens, maxActivePaths, completed) =
                RunToCompletion(lexer, Encoding.UTF8.GetBytes("hej med dig"));

            Assert.True(completed);
            Assert.Equal(1, maxActivePaths);
            Assert.Equal(3, tokens.Count);
            Assert.All(tokens, t => Assert.Equal("noun-stem", t.Type));
            Assert.Equal(new[] { "hej", "med", "dig" }, tokens.Select(t => t.Value).ToArray());
        }

        [Fact]
        public void GreedyMode_LongestMatch_BeatsHigherPriority()
        {
            var lexer = CreateGreedy(
                new TokenRule("AB", "ab", "", 100),
                new TokenRule("IDENT", "/[a-z]+/"),
                new TokenRule("ws", "/[ \\t\\r\\n]+/") { IsSkippable = true });

            // "abcd": identifier (len 4) wins over the higher-priority
            // literal "ab" (len 2) - longest match first.
            var longer = RunToCompletion(lexer, Encoding.UTF8.GetBytes("abcd"));
            Assert.True(longer.Completed);
            Assert.Equal(1, longer.MaxActivePaths);
            var token = Assert.Single(longer.Tokens);
            Assert.Equal("IDENT", token.Type);
            Assert.Equal("abcd", token.Value);

            // "ab": equal length -> priority tie-break -> the literal wins.
            var tie = RunToCompletion(lexer, Encoding.UTF8.GetBytes("ab x"));
            Assert.Equal(2, tie.Tokens.Count);
            Assert.Equal("AB", tie.Tokens[0].Type);
            Assert.Equal("ab", tie.Tokens[0].Value);
            Assert.Equal("IDENT", tie.Tokens[1].Type);
            Assert.Equal("x", tie.Tokens[1].Value);
        }

        [Fact]
        public void GreedyMode_KeywordOverIdentifier_OnEqualLength()
        {
            var lexer = CreateGreedy(
                new TokenRule("KW", "if", "", 10),
                new TokenRule("IDENT", "/[a-z]+/", "", 1),
                new TokenRule("ws", "/[ \\t\\r\\n]+/") { IsSkippable = true });

            var (tokens, maxActivePaths, completed) =
                RunToCompletion(lexer, Encoding.UTF8.GetBytes("if ifx"));

            Assert.True(completed);
            Assert.Equal(1, maxActivePaths);
            Assert.Equal(2, tokens.Count);
            Assert.Equal("KW", tokens[0].Type);
            Assert.Equal("if", tokens[0].Value);
            // "ifx" is longer than the keyword -> identifier wins by length.
            Assert.Equal("IDENT", tokens[1].Type);
            Assert.Equal("ifx", tokens[1].Value);
        }

        [Fact]
        public void ExploratoryMode_Default_StillForksPathsOnAmbiguity()
        {
            // The default mode is unchanged: a keyword literal and an
            // identifier both matching forks two paths.
            var lexer = new StepLexer { Mode = LexingMode.Exploratory };
            lexer.AddRule(new TokenRule("KW", "if", "", 10));
            lexer.AddRule(new TokenRule("IDENT", "/[a-z]+/", "", 1));

            lexer.Initialize(Encoding.UTF8.GetBytes("ifx"), "test.txt");
            var step = lexer.Step();

            Assert.Equal(2, step.NewTokens.Count);
            Assert.Equal(2, lexer.ActivePaths.Count);
        }

        [Fact]
        public void GreedyMode_SmallAmbiguousInput_StaysSinglePath_WhereExploratoryForks()
        {
            TokenRule[] Rules() => new[]
            {
                new TokenRule("KW", "if", "", 10),
                new TokenRule("IDENT", "/[a-z]+/", "", 1),
                new TokenRule("NUM", "/[0-9]+/", "", 1),
                new TokenRule("ws", "/[ \\t\\r\\n]+/") { IsSkippable = true },
            };

            var input = Encoding.UTF8.GetBytes("if ifx 12 if");

            var greedy = RunToCompletion(CreateGreedy(Rules()), input);
            Assert.Equal(1, greedy.MaxActivePaths);
            Assert.Equal(4, greedy.Tokens.Count);

            var exploratoryLexer = new StepLexer { Mode = LexingMode.Exploratory };
            foreach (var rule in Rules())
            {
                exploratoryLexer.AddRule(rule);
            }
            var exploratory = RunToCompletion(exploratoryLexer, input);
            Assert.True(exploratory.MaxActivePaths > 1,
                "exploratory mode must keep forking paths on ambiguity");
            Assert.True(exploratory.Tokens.Count > greedy.Tokens.Count,
                "exploratory mode unions tokens across paths, greedy mode emits one per position");
        }

        private static TokenRule[] CreateCSharpLikeRules()
        {
            var rules = new List<TokenRule>
            {
                new TokenRule("ws", "/[ \\t\\r\\n]+/") { IsSkippable = true },
                new TokenRule("line-comment", @"/\/\/[^\n]*/") { IsSkippable = true },
                new TokenRule("block-comment", @"/\/\*([^*]|\*+[^*/])*\*+\//") { IsSkippable = true },
                new TokenRule("string", @"""([^""\\\n]|\\.)*"""),
                new TokenRule("identifier", "/[a-zA-Z_][a-zA-Z0-9_]*/", "", 1),
                new TokenRule("number", "/[0-9]+/", "", 1),
            };
            foreach (var kw in new[] { "if", "else", "return", "class", "void", "int", "var", "new", "await" })
            {
                rules.Add(new TokenRule("kw-" + kw, "\"" + kw + "\"", "", 10));
            }
            foreach (var p in new[] { ";", "{", "}", "(", ")", "=", ",", ".", "<", ">", "+", "-", "*", "%" })
            {
                rules.Add(new TokenRule("p-" + p, "\"" + p + "\"", "", 5));
            }
            // Division operator (lowest priority; comments win by length).
            rules.Add(new TokenRule("p-div", "/", "", 0));
            return rules.ToArray();
        }

        private static string BuildCSharpLikeInput(int targetBytes)
        {
            var sb = new StringBuilder();
            var i = 0;
            while (sb.Length < targetBytes)
            {
                sb.Append("class C").Append(i)
                  .Append(" { void M() { if (x >= ").Append(i)
                  .Append(") { return \"s").Append(i).Append("\"; } } } // note ").Append(i)
                  .Append(" /* block */\n");
                i++;
            }
            return sb.ToString();
        }

        [Fact]
        public void GreedyMode_TenKbFile_LexesInUnderOneSecond_WithSingleCoherentPath()
        {
            // Acceptance criterion of issue #133 / Minotaur-Grammars #194:
            // lex a 10 KB real file in < 1 s with a single coherent token path.
            var lexer = CreateGreedy(CreateCSharpLikeRules());
            var input = Encoding.UTF8.GetBytes(BuildCSharpLikeInput(10_240));
            Assert.True(input.Length >= 10_240);

            var sw = Stopwatch.StartNew();
            var (tokens, maxActivePaths, completed) = RunToCompletion(lexer, input);
            sw.Stop();

            Assert.True(completed,
                $"greedy mode must consume the whole input; stalled at byte {lexer.LastNoMatchPosition}: " +
                $"'{Encoding.UTF8.GetString(input, Math.Max(0, lexer.LastNoMatchPosition - 10),
                    Math.Min(30, input.Length - Math.Max(0, lexer.LastNoMatchPosition)))}'");
            Assert.Equal(1, maxActivePaths);
            Assert.True(tokens.Count > 1_000, $"expected many tokens, got {tokens.Count}");
            Assert.True(sw.ElapsedMilliseconds < 1_000,
                $"10 KB input lexed in {sw.ElapsedMilliseconds} ms; acceptance is < 1000 ms (issue #133)");
        }
    }
}
