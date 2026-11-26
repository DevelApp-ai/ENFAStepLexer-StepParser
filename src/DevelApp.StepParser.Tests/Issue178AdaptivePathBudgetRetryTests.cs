using System.Linq;
using System.Text;
using DevelApp.StepParser;
using Xunit;

namespace DevelApp.StepParser.Tests
{
    /// <summary>
    /// Issue #178: at the historical parser path budget of 10 a parse that
    /// dies solely from exhausting the shared parse-step budget surfaced
    /// as a silent Success=false with zero errors, and large real-language
    /// grammars (full CSharp10: 2220 rules) rejected every valid source.
    /// The engine now (a) reports the silent exhaustion explicitly with a
    /// PS2004 diagnostic, (b) exposes ParserPathBudget for pinning, and
    /// (c) offers an opt-in EnableAdaptivePathBudgetRetry rule-scaled
    /// retry pass. The retry stays opt-in because ambiguity-shaped
    /// grammars (the issue #157 wide-branching morphology corpus) depend
    /// on the small cap to prune their per-token path explosion.
    /// </summary>
    public class Issue178AdaptivePathBudgetRetryTests
    {
        // Above the AdaptivePathBudgetRuleThreshold of 256 so the retry
        // machinery is engaged, but small enough to parse in milliseconds.
        private const int VocabularySize = 300;

        private static string BuildGrammar()
        {
            var sb = new StringBuilder();
            sb.AppendLine("Grammar: AdaptiveRetry178");
            sb.AppendLine();
            for (int i = 0; i < VocabularySize; i++)
            {
                sb.AppendLine($"<W{i:D3}> ::= 'morph{i:D3}'");
            }
            sb.AppendLine("<WORD_ROOT> ::= /[a-z]+/");
            sb.AppendLine("<WS> ::= /[ ]+/");
            sb.AppendLine();
            sb.AppendLine("<text> ::= <words>");
            sb.AppendLine("<words> ::= <words> <word>");
            sb.AppendLine("<words> ::= <word>");
            for (int i = 0; i < VocabularySize; i++)
            {
                sb.AppendLine(i == 0
                    ? "<word> ::= <W000>"
                    : $"        | <W{i:D3}>");
            }

            return sb.ToString();
        }

        private static string BuildInput(int wordCount)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < wordCount; i++)
            {
                if (i > 0)
                {
                    sb.Append(' ');
                }
                sb.Append($"morph{i % VocabularySize:D3}");
            }

            return sb.ToString();
        }

        private static StepParserEngine BuildLoadedEngine()
        {
            var engine = new StepParserEngine();
            engine.LoadGrammarFromContent(BuildGrammar(), "adaptive-retry-178");
            return engine;
        }

        /// <summary>
        /// The grammar must be above the adaptive threshold for the retry
        /// machinery to be eligible at all.
        /// </summary>
        [Fact]
        public void GrammarExceedsAdaptiveThreshold()
        {
            using var engine = BuildLoadedEngine();
            Assert.True(engine.InternalParser.RuleCount > 256,
                $"expected the test grammar above the 256-rule threshold, got {engine.InternalParser.RuleCount}");
        }

        /// <summary>
        /// Default configuration (no pin, retry off): the retry budget
        /// must stay null so behavior matches the historical engine.
        /// </summary>
        [Fact]
        public void RetryBudget_OffByDefault_IsNull()
        {
            using var engine = BuildLoadedEngine();
            Assert.Null(engine.RetryPathBudget);
        }

        /// <summary>
        /// A user-pinned budget always wins and disables the retry pass,
        /// flag or not.
        /// </summary>
        [Fact]
        public void RetryBudget_PinnedBudget_DisablesRetry()
        {
            using var engine = BuildLoadedEngine();
            engine.ParserPathBudget = 10;
            engine.EnableAdaptivePathBudgetRetry = true;
            Assert.Equal(10, engine.EffectivePathBudget);
            Assert.Null(engine.RetryPathBudget);
        }

        /// <summary>
        /// Opt-in on an above-threshold grammar: the retry budget scales
        /// at AdaptivePathBudgetPerRule per production rule and clamps
        /// to AdaptivePathBudgetMax.
        /// </summary>
        [Fact]
        public void RetryBudget_OptInLargeGrammar_ScalesAndClamps()
        {
            using var engine = BuildLoadedEngine();
            engine.EnableAdaptivePathBudgetRetry = true;

            var rules = engine.InternalParser.RuleCount;
            var budget = engine.RetryPathBudget;
            Assert.NotNull(budget);
            Assert.Equal((int)System.Math.Min(rules * 0.9, StepParserEngine.AdaptivePathBudgetMax), budget.Value);
        }

        /// <summary>
        /// First pass stays at the historical cap even when the retry is
        /// opted in: everything that parses today keeps identical
        /// behavior.
        /// </summary>
        [Fact]
        public void FirstPass_AlwaysUsesHistoricalCap()
        {
            using var engine = BuildLoadedEngine();
            engine.EnableAdaptivePathBudgetRetry = true;
            Assert.Equal(10, engine.EffectivePathBudget);
        }

        /// <summary>
        /// The core regression of the silent failure: a parse that dies by
        /// exhausting the parse-step budget (previously Success=false with
        /// zero errors and zero diagnostics) must now report an explicit
        /// PS2004 diagnostic, at defaults and with the retry off.
        /// </summary>
        [Fact]
        public void Parse_StepBudgetExhaustion_ReportsPS2004()
        {
            using var engine = BuildLoadedEngine();
            var result = engine.Parse(BuildInput(300), "adaptive-retry-178.txt");

            // The wide-branching grammar reliably exhausts the parse-step
            // budget at the historical cap, so the explicit reporting is
            // observable without depending on which paths the pruner cuts.
            var diagnostic = result.Diagnostics.FirstOrDefault(d => d.Code == DiagnosticCodes.ParserStepBudgetExhausted);
            Assert.NotNull(diagnostic);
            Assert.False(result.Success);
            Assert.True(result.Errors.Count > 0,
                "exhaustion must no longer be silent: at least one error message is required");
        }
    }
}
