using System;
using System.Linq;
using DevelApp.StepParser;
using DevelApp.StepLexer;
using Xunit;

namespace DevelApp.StepParser.Tests
{
    /// <summary>
    /// Engine-level guard rails for the learned token-rule prioritization
    /// prototype (issue #74). The ML-assist gate
    /// (<see cref="MlAssistFeature.LearnedRulePrioritization"/>) is the
    /// only way the prioritizer becomes active: by default the engine
    /// parses bit-identically to the non-ML behavior, and enabling the
    /// feature changes only the rule evaluation ORDER — never the token
    /// stream or the parse result.
    /// </summary>
    public class LearnedRulePrioritizationEngineTests : IDisposable
    {
        private const string Grammar = @"
Grammar: PrioDemo
<KW_A> ::= 'alpha'
<KW_B> ::= 'beta'
<KW_C> ::= 'gamma'
<OP_PLUS> ::= '+'
<OP_MINUS> ::= '-'
<NUM> ::= /[0-9]+/
<term> ::= <NUM>
<term> ::= <KW_A>
<term> ::= <KW_B>
<term> ::= <KW_C>
<sum> ::= <term>
<sum> ::= <sum> '+' <term>
<start> ::= <sum>
";

        public LearnedRulePrioritizationEngineTests()
        {
            MlAssistOptions.ResetForTest();
        }

        public void Dispose()
        {
            MlAssistOptions.ResetForTest();
        }

        private static StepParsingResult ParseWithEngine(string input)
        {
            using var engine = new StepParserEngine();
            engine.LoadGrammarFromContent(Grammar, "PrioDemo.grammar");
            return engine.Parse(input, "input.txt");
        }

        [Fact]
        public void Prioritization_DefaultsOff_AndEngineIgnoresPrioritizer()
        {
            using var engine = new StepParserEngine();
            engine.LoadGrammarFromContent(Grammar, "PrioDemo.grammar");

            // A prioritizer is assigned but the feature is NOT enabled: the
            // engine must ignore it entirely (gate check happens per parse).
            engine.RulePrioritizer = LearnedRulePrioritizer.Default;
            var input = "12+34-56";

            var gated = engine.Parse(input, "input.txt");
            Assert.True(gated.Success, string.Join("; ", gated.Errors));

            using var plainEngine = new StepParserEngine();
            plainEngine.LoadGrammarFromContent(Grammar, "PrioDemo.grammar");
            var baseline = plainEngine.Parse(input, "input.txt");

            Assert.Equal(
                string.Join("|", baseline.Tokens.Select(t => $"{t.Type}:{t.Value}@{t.StartPosition}")),
                string.Join("|", gated.Tokens.Select(t => $"{t.Type}:{t.Value}@{t.StartPosition}")));
        }

        [Fact]
        public void Prioritization_Enabled_PreservesTokensAndParseResult()
        {
            var input = "12+34-56";
            var baseline = ParseWithEngine(input);

            MlAssistOptions.Enable(MlAssistFeature.LearnedRulePrioritization, "0.1.0");
            using var engine = new StepParserEngine();
            engine.LoadGrammarFromContent(Grammar, "PrioDemo.grammar");
            engine.RulePrioritizer = LearnedRulePrioritizer.Default;

            Assert.Contains("LearnedRulePrioritization=0.1.0", engine.MlAssistStatus);

            var prioritized = engine.Parse(input, "input.txt");
            Assert.Equal(baseline.Success, prioritized.Success);
            Assert.Equal(
                string.Join("|", baseline.Tokens.Select(t => $"{t.Type}:{t.Value}@{t.StartPosition}+{t.Length}")),
                string.Join("|", prioritized.Tokens.Select(t => $"{t.Type}:{t.Value}@{t.StartPosition}+{t.Length}")));
            Assert.Equal(baseline.PathCount, prioritized.PathCount);
        }

        [Fact]
        public void Prioritization_DisableAll_OverridesTheFeatureFlag()
        {
            MlAssistOptions.Enable(MlAssistFeature.LearnedRulePrioritization, "0.1.0");
            MlAssistOptions.DisableAll = true;

            using var engine = new StepParserEngine();
            engine.LoadGrammarFromContent(Grammar, "PrioDemo.grammar");
            engine.RulePrioritizer = LearnedRulePrioritizer.Default;

            // The parse succeeds (the gate blocks the prioritizer); the
            // status reports the master switch.
            Assert.Contains("disabled", engine.MlAssistStatus);
            var result = engine.Parse("12+34", "input.txt");
            Assert.True(result.Success, string.Join("; ", result.Errors));
        }
    }
}
