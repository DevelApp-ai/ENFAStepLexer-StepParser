using Xunit;
using DevelApp.StepParser;
using System.Text;

namespace DevelApp.StepParser.Tests
{
    /// <summary>
    /// Issue #157, parts 1/4 (parse blowups / test-host crashes at chain scale):
    /// the per-step reduction-chaining rounds are now capped by
    /// <see cref="StepParser.MaxReductionRoundsPerStep"/> (default 64) instead of
    /// the raw <c>tokens*2+16</c>, which combined with the intra-step path cap
    /// into unbounded work on pathological grammars. These tests pin that
    /// healthy long chains still parse and that the knob is respected.
    /// </summary>
    public class ParseBlowupHardeningTests
    {
        private const string ExprGrammar = "Grammar: SimpleExprInline\n" +
            "<NUMBER> ::= /[0-9]+/\n" +
            "<PLUS> ::= '+'\n" +
            "<expr> ::= <expr> '+' <expr>\n" +
            "<expr> ::= <NUMBER>\n" +
            "<start> ::= <expr>\n";

        private static string BuildChain(int terms)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < terms; i++)
            {
                if (i > 0) sb.Append('+');
                sb.Append('1');
            }
            return sb.ToString();
        }

        [Fact]
        public void MaxReductionRoundsPerStep_DefaultsTo64()
        {
            var parser = new StepParser();
            Assert.Equal(64, parser.MaxReductionRoundsPerStep);
        }

        [Fact]
        public void LongAmbiguousChain_StillParsesWithRoundCap()
        {
            // 150 terms: far above the 64-round cap, so this proves the cap
            // does not break legitimate mid-stream reduction chains (they run
            // across steps, one cascade per token, not within one step).
            using var engine = new StepParserEngine();
            engine.LoadGrammarFromContent(ExprGrammar);

            var result = engine.Parse(BuildChain(150));

            Assert.True(result.Success, $"150-term chain must still parse (errors: {string.Join("; ", result.Errors)})");
        }

        [Fact]
        public void LongAmbiguousChain_CompletesQuickly_WithTinyRoundCap()
        {
            // With the cap forced to 1 the parse may fail, but it must
            // terminate promptly instead of cascading (the host-crash regime).
            using var engine = new StepParserEngine();
            engine.LoadGrammarFromContent(ExprGrammar);
            engine.InternalParser.MaxReductionRoundsPerStep = 1;

            var result = engine.Parse(BuildChain(120));

            Assert.NotNull(result);
        }

        [Fact]
        public void ShortInput_UnaffectedByRoundCap()
        {
            using var engine = new StepParserEngine();
            engine.LoadGrammarFromContent(ExprGrammar);

            var result = engine.Parse("1+2+3");

            Assert.True(result.Success);
        }
    }
}
