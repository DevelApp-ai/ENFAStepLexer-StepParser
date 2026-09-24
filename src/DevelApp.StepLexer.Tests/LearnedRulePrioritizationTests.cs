using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using DevelApp.StepLexer;
using Xunit;

namespace DevelApp.StepLexer.Tests
{
    /// <summary>
    /// Tests for the learned token-rule prioritization prototype (issue
    /// #74, candidate approach 1 of the #58 ML plan). The contract:
    ///  - Ordering only: the emitted token stream is identical with and
    ///    without the prioritizer.
    ///  - The wasted-attempts metric (failures before the first match)
    ///    improves when the likely rule is evaluated first.
    ///  - The model is deterministic and validates its artifact shape.
    /// </summary>
    public class LearnedRulePrioritizationTests
    {
        private const string Input = "42 17 900 5";

        private static StepLexer CreateLexer(out List<TokenRule> rules)
        {
            var lexer = new StepLexer();

            // Declaration order is deliberately hostile: the rule that
            // matches most positions (<NUM>) is declared LAST, after a
            // batch of literal rules that never match the digits input.
            rules = new List<TokenRule>
            {
                new TokenRule("KW_A", "'alpha'"),
                new TokenRule("KW_B", "'beta'"),
                new TokenRule("KW_C", "'gamma'"),
                new TokenRule("KW_D", "'delta'"),
                new TokenRule("OP_PLUS", "'+'"),
                new TokenRule("OP_MINUS", "'-'"),
                new TokenRule("OP_MUL", "'*'"),
                new TokenRule("OP_DIV", "'/'"),
                new TokenRule("NUM", "/[0-9]+/")
            };

            foreach (var rule in rules)
            {
                lexer.AddRule(rule);
            }

            lexer.Initialize(Encoding.UTF8.GetBytes(Input), "test");
            return lexer;
        }

        private static List<StepToken> LexAll(StepLexer lexer)
        {
            var tokens = new List<StepToken>();
            for (var i = 0; i < 1000; i++)
            {
                var step = lexer.Step();
                tokens.AddRange(step.NewTokens);
                if (step.IsComplete)
                {
                    break;
                }
            }

            return tokens;
        }

        private static string Describe(List<StepToken> tokens) =>
            string.Join("|", tokens.Select(t => $"{t.Type}:{t.Value}@{t.StartPosition}+{t.Length}"));

        [Fact]
        public void Prioritizer_ProducesIdenticalTokens_AndReducesWastedAttempts()
        {
            // Baseline: declaration order.
            var baselineLexer = CreateLexer(out _);
            RuleMatchDiagnostics.Reset();
            var baselineTokens = LexAll(baselineLexer);
            var baselineAttempts = RuleMatchDiagnostics.TotalMatchAttempts;
            var baselineWasted = RuleMatchDiagnostics.WastedMatchAttempts;
            Assert.True(baselineTokens.Count > 0);
            Assert.True(baselineWasted > 0, "hostile declaration order should waste attempts");

            // Prioritized: model predicts the digit rule matches first.
            var prioritizedLexer = CreateLexer(out _);
            prioritizedLexer.RulePrioritizer = LearnedRulePrioritizer.Default;
            RuleMatchDiagnostics.Reset();
            var prioritizedTokens = LexAll(prioritizedLexer);
            var prioritizedAttempts = RuleMatchDiagnostics.TotalMatchAttempts;
            var prioritizedWasted = RuleMatchDiagnostics.WastedMatchAttempts;

            // Ordering only: same tokens, same attempt count, fewer wasted
            // attempts before the first match.
            Assert.Equal(Describe(baselineTokens), Describe(prioritizedTokens));
            Assert.Equal(baselineAttempts, prioritizedAttempts);
            Assert.True(prioritizedWasted < baselineWasted,
                $"prioritized wasted {prioritizedWasted} should be < baseline {baselineWasted}");
        }

        [Fact]
        public void Prioritizer_IsDeterministicAcrossRuns()
        {
            var first = Describe(LexWithPrioritizer());
            var second = Describe(LexWithPrioritizer());
            Assert.Equal(first, second);
        }

        [Fact]
        public void Prioritizer_DoesNotChangeAmbiguousMatches()
        {
            // A rule set with overlapping matches (multi-path ambiguity):
            // the SET of matches per position must be identical, only the
            // evaluation order may differ.
            var lexer = new StepLexer();
            lexer.AddRule(new TokenRule("IDENT", "/[a-zA-Z][a-zA-Z0-9]*/"));
            lexer.AddRule(new TokenRule("IF", "'if'"));
            lexer.AddRule(new TokenRule("IFI", "'ifi'"));
            lexer.Initialize(Encoding.UTF8.GetBytes("ifi if"), "amb");

            var baseline = LexAll(lexer);
            Assert.True(baseline.Count > 0);

            var lexer2 = new StepLexer();
            lexer2.AddRule(new TokenRule("IDENT", "/[a-zA-Z][a-zA-Z0-9]*/"));
            lexer2.AddRule(new TokenRule("IF", "'if'"));
            lexer2.AddRule(new TokenRule("IFI", "'ifi'"));
            lexer2.Initialize(Encoding.UTF8.GetBytes("ifi if"), "amb");
            lexer2.RulePrioritizer = LearnedRulePrioritizer.Default;

            Assert.Equal(Describe(baseline), Describe(LexAll(lexer2)));
        }

        [Fact]
        public void Model_ValidatesVersionAndWeights()
        {
            Assert.Equal("0.1.0", LearnedRulePrioritizer.Default.ModelVersion);

            Assert.Throws<ArgumentException>(
                () => new LearnedRulePrioritizer(" ", new double[LearnedRulePrioritizer.FeatureCount]));
            Assert.Throws<ArgumentException>(
                () => new LearnedRulePrioritizer("1.0.0", new double[3]));

            // Probabilities are in (0,1) and deterministic.
            var rule = new TokenRule("NUM", "/[0-9]+/");
            var p1 = LearnedRulePrioritizer.Default.PredictMatchProbability(rule, (byte)'7', "");
            var p2 = LearnedRulePrioritizer.Default.PredictMatchProbability(rule, (byte)'7', "");
            Assert.Equal(p1, p2);
            Assert.InRange(p1, 0.0, 1.0);
        }

        private static List<StepToken> LexWithPrioritizer()
        {
            var lexer = CreateLexer(out _);
            lexer.RulePrioritizer = LearnedRulePrioritizer.Default;
            return LexAll(lexer);
        }
    }
}
