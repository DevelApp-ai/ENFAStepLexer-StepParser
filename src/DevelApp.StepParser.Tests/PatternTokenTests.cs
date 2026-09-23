using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Xunit;
using DevelApp.StepParser;
using DevelApp.StepLexer;

namespace DevelApp.StepParser.Tests
{
    /// <summary>
    /// Tests for the standard pattern tokens (issue #65): metavariable
    /// (<c>$[A-Z_][A-Z0-9_]*</c>) and ellipsis (<c>...</c>) token rules that
    /// can be injected into any grammar and its lexer.
    /// </summary>
    public class PatternTokenTests
    {
        private const string SimpleGrammar = @"
Grammar: SimpleLang
TokenSplitter: Space
FormatType: EBNF

<WS> ::= /[ \t\r\n]+/ => { skip(); }
<IDENTIFIER> ::= /[a-zA-Z][a-zA-Z0-9]*/
<NUMBER> ::= /[0-9]+/

<expr> ::= <term> ;
<term> ::= IDENTIFIER | NUMBER ;
";

        [Fact]
        public void Lexer_WithPatternTokens_TokenizesMetavariableAndEllipsis()
        {
            var lexer = new DevelApp.StepLexer.StepLexer();
            AddCommonRules(lexer);
            foreach (var rule in PatternTokens.CreatePatternTokenRules())
            {
                lexer.AddRule(rule);
            }

            var tokens = Tokenize(lexer, "ExecuteAction ( ... , $DATA , ... )");

            var types = tokens.Select(t => t.Type).ToList();
            Assert.Equal(
                new List<string> { "IDENTIFIER", "LPAREN", "ELLIPSIS", "COMMA", "METAVARIABLE", "COMMA", "ELLIPSIS", "RPAREN" },
                types);

            var metavariable = tokens.Single(t => t.Type == PatternTokens.MetavariableTokenName);
            Assert.Equal("$DATA", metavariable.Value);

            var ellipsis = tokens.First(t => t.Type == PatternTokens.EllipsisTokenName);
            Assert.Equal("...", ellipsis.Value);
        }

        [Fact]
        public void Lexer_WithoutPatternTokens_DoesNotMatchMetavariable()
        {
            var lexer = new DevelApp.StepLexer.StepLexer();
            AddCommonRules(lexer);

            // Without the pattern tokens there is no rule matching '$' or the
            // '...' sequence, so the lexer cannot consume the input.
            var tokens = Tokenize(lexer, "checkout");

            Assert.Equal(new List<string> { "IDENTIFIER" }, tokens.Select(t => t.Type).ToList());
        }

        [Fact]
        public void Lexer_WithoutPatternTokens_DoesNotMatchEllipsis()
        {
            var lexer = new DevelApp.StepLexer.StepLexer();
            AddCommonRules(lexer);

            var tokens = Tokenize(lexer, "checkout");

            Assert.DoesNotContain(tokens, t => t.Type == PatternTokens.EllipsisTokenName);
        }

        [Fact]
        public void MetavariableRule_MatchesDollarAndUppercaseIdentifier()
        {
            var lexer = new DevelApp.StepLexer.StepLexer();
            lexer.AddRule(PatternTokens.CreateMetavariableRule());

            var tokens = Tokenize(lexer, "$DATA_SOURCE");

            var token = Assert.Single(tokens);
            Assert.Equal(PatternTokens.MetavariableTokenName, token.Type);
            Assert.Equal("$DATA_SOURCE", token.Value);
        }

        [Fact]
        public void MetavariableRule_RejectsLowercaseStart()
        {
            var lexer = new DevelApp.StepLexer.StepLexer();
            lexer.AddRule(PatternTokens.CreateMetavariableRule());

            // '$data' does not start with an uppercase letter or underscore,
            // so the metavariable rule must not match it.
            var tokens = Tokenize(lexer, "$data");

            Assert.Empty(tokens);
        }

        [Fact]
        public void EllipsisRule_MatchesExactlyThreeDots()
        {
            var lexer = new DevelApp.StepLexer.StepLexer();
            lexer.AddRule(PatternTokens.CreateEllipsisRule());

            var tokens = Tokenize(lexer, "...");

            var token = Assert.Single(tokens);
            Assert.Equal(PatternTokens.EllipsisTokenName, token.Type);
            Assert.Equal("...", token.Value);
        }

        [Fact]
        public void Engine_EnablePatternTokens_InjectsTokensIntoLoadedGrammar()
        {
            using var engine = new StepParserEngine();
            engine.LoadGrammarFromContent(SimpleGrammar);

            engine.EnablePatternTokens();

            Assert.Contains(engine.CurrentGrammar!.TokenRules, r => r.Name == PatternTokens.MetavariableTokenName);
            Assert.Contains(engine.CurrentGrammar!.TokenRules, r => r.Name == PatternTokens.EllipsisTokenName);
        }

        [Fact]
        public void Engine_EnablePatternTokens_Twice_DoesNotDuplicate()
        {
            using var engine = new StepParserEngine();
            engine.LoadGrammarFromContent(SimpleGrammar);

            engine.EnablePatternTokens();
            engine.EnablePatternTokens();

            Assert.Single(engine.CurrentGrammar!.TokenRules, r => r.Name == PatternTokens.MetavariableTokenName);
            Assert.Single(engine.CurrentGrammar!.TokenRules, r => r.Name == PatternTokens.EllipsisTokenName);
        }

        [Fact]
        public void Engine_EnablePatternTokens_WithoutGrammar_Throws()
        {
            using var engine = new StepParserEngine();

            Assert.Throws<InvalidOperationException>(() => engine.EnablePatternTokens());
        }

        [Fact]
        public void AddPatternTokens_IsIdempotent()
        {
            var grammar = new GrammarDefinition { Name = "G" };

            PatternTokens.AddPatternTokens(grammar);
            PatternTokens.AddPatternTokens(grammar);

            Assert.Single(grammar.TokenRules, r => r.Name == PatternTokens.MetavariableTokenName);
            Assert.Single(grammar.TokenRules, r => r.Name == PatternTokens.EllipsisTokenName);
        }

        [Fact]
        public void AddPatternTokens_GrammarOwnRuleWins()
        {
            var grammar = new GrammarDefinition { Name = "G" };
            var ownRule = new TokenRule(PatternTokens.MetavariableTokenName, @"/\$[A-Z_][A-Z0-9_]*/", priority: 500);
            grammar.TokenRules.Add(ownRule);

            PatternTokens.AddPatternTokens(grammar);

            Assert.Same(ownRule, grammar.TokenRules.Single(r => r.Name == PatternTokens.MetavariableTokenName));
            Assert.Equal(500, grammar.TokenRules.Single(r => r.Name == PatternTokens.MetavariableTokenName).Priority);
        }

        [Fact]
        public void CreatePatternTokenOverlay_ContainsBothRules()
        {
            var overlay = PatternTokens.CreatePatternTokenOverlay();

            Assert.NotNull(overlay);
            Assert.Contains(overlay.TokenRules, r => r.Name == PatternTokens.MetavariableTokenName);
            Assert.Contains(overlay.TokenRules, r => r.Name == PatternTokens.EllipsisTokenName);
        }

        private static void AddCommonRules(DevelApp.StepLexer.StepLexer lexer)
        {
            lexer.AddRule(new TokenRule("WS", @"/[ \t\r\n]+/", priority: 0) { IsSkippable = true });
            lexer.AddRule(new TokenRule("IDENTIFIER", "/[a-zA-Z][a-zA-Z0-9]*/"));
            lexer.AddRule(new TokenRule("NUMBER", "/[0-9]+/"));
            lexer.AddRule(new TokenRule("COMMA", ","));
            lexer.AddRule(new TokenRule("LPAREN", "("));
            lexer.AddRule(new TokenRule("RPAREN", ")"));
        }

        private static List<StepToken> Tokenize(DevelApp.StepLexer.StepLexer lexer, string input)
        {
            var bytes = Encoding.UTF8.GetBytes(input);
            lexer.Initialize(bytes, "test-input");
            var tokens = new List<StepToken>();
            var guard = 0;
            while (lexer.ActivePaths.Any(p => p.IsValid && p.Position < bytes.Length))
            {
                var step = lexer.Step();
                tokens.AddRange(step.NewTokens);
                if (++guard > 100_000)
                {
                    break;
                }
            }

            return tokens;
        }
    }
}
