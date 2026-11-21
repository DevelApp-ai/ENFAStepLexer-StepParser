using System;
using System.Diagnostics;
using System.Text;
using DevelApp.StepParser;
using DevelApp.StepLexer;
using Xunit;

namespace DevelApp.StepParser.Tests
{
    /// <summary>
    /// Issue #157, part 1 follow-up: the pending-reduction loop in
    /// ProcessParserPath could spend many reduction rounds re-visiting
    /// parser states it had already explored, because unit-production
    /// cycles (a ::= b, b ::= a) keep merged paths alive that derive the
    /// same state. Until the fix, the loop only stopped at the
    /// token-count-scaled maxRounds cap, so a single Step() could burn
    /// thousands of reduction rounds while more than 99% of them
    /// re-visited already-seen states; on the grammar corpus one
    /// hanging Step() measured 35+ seconds. The loop now terminates at
    /// the fixed point: once a merge round adds no new state key, it
    /// cannot make further progress. These tests pin both that the
    /// cycle grammar completes fast and that legitimate deep reduction
    /// chains still parse correctly.
    /// </summary>
    public class Issue157ReductionCycleTerminationTests
    {
        /// <summary>
        /// A grammar with mutually recursive unit productions that makes
        /// merged parser paths cycle between equivalent states while the
        /// pending-reduction loop runs.
        /// </summary>
        private const string CycleGrammar = """
            Grammar: Issue157ReductionCycle
            <WS> ::= /[ \t\r\n]+/
            <X> ::= /x/
            <s> ::= <chain>
            <chain> ::= <chain> <X> | <X>
            <a> ::= <b> | <X>
            <b> ::= <a>
            """;

        /// <summary>
        /// An ordinary deep right-recursive expression grammar whose long
        /// inputs legitimately require long (but always progressing)
        /// reduction chains; it must keep parsing successfully after the
        /// fixed-point termination guard.
        /// </summary>
        private const string DeepChainGrammar = """
            Grammar: Issue157DeepChain
            <WS> ::= /[ \t]+/
            <ID> ::= /[a-z]+/
            <PLUS> ::= /\+/
            <s> ::= <expr>
            <expr> ::= <term> <PLUS> <expr> | <term>
            <term> ::= <ID>
            """;

        /// <summary>
        /// Builds an input of <paramref name="count"/> space-separated
        /// x tokens.
        /// </summary>
        private static string BuildXChain(int count)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < count; i++)
            {
                if (i > 0)
                {
                    sb.Append(' ');
                }

                sb.Append('x');
            }

            return sb.ToString();
        }

        /// <summary>
        /// Parses a long input against the mutually recursive unit
        /// production cycle grammar and asserts that it completes quickly:
        /// without the fixed-point termination the pending-reduction loop
        /// spins up to tokens*2+16 rounds per token step, which takes many
        /// minutes at this input size.
        /// </summary>
        [Fact]
        public void Parse_WithUnitProductionCycle_CompletesQuickly()
        {
            var engine = new StepParserEngine();
            engine.LoadGrammarFromContent(CycleGrammar, "issue157-cycle.grammar");

            var input = BuildXChain(1_000);
            var sw = Stopwatch.StartNew();
            var result = engine.Parse(input, "issue157-cycle.txt");
            sw.Stop();

            // May succeed or fail with diagnostics; either way it must
            // not hang. Unfixed this takes minutes; allow a generous CI
            // bound.
            Assert.True(
                sw.ElapsedMilliseconds < 5_000,
                $"Parse of the cycle grammar took {sw.ElapsedMilliseconds}ms; " +
                "the pending-reduction loop is likely cycling through already-seen states again (issue #157).");
        }

        /// <summary>
        /// The fixed-point termination must not stop reduction rounds that
        /// are still making progress: a long chain grammar with a deep
        /// (but acyclic) reduction ladder still parses successfully and
        /// quickly.
        /// </summary>
        [Fact]
        public void Parse_WithDeepReductionChain_StillParsesSuccessfully()
        {
            var engine = new StepParserEngine();
            engine.LoadGrammarFromContent(DeepChainGrammar, "issue157-deep.grammar");

            var sb = new StringBuilder();
            for (int i = 0; i < 300; i++)
            {
                if (i > 0)
                {
                    sb.Append(" + ");
                }

                sb.Append("id");
            }

            var sw = Stopwatch.StartNew();
            var result = engine.Parse(sb.ToString(), "issue157-deep.txt");
            sw.Stop();

            Assert.True(
                result.Success,
                $"Deep chain should parse fully. Errors: {string.Join("; ", result.Errors)}");
            Assert.True(
                sw.ElapsedMilliseconds < 5_000,
                $"Deep chain parse took {sw.ElapsedMilliseconds}ms, which is a regression.");
        }

        /// <summary>
        /// A second cycle shape interleaving the cycle with real content,
        /// exercising FinalizeEndOfInputReductions: reduce the parameterless
        /// end-of-input rounds the same way (a round that only re-merges
        /// seen states must stop the loop).
        /// </summary>
        [Fact]
        public void Parse_WithCycleAtEndOfInput_CompletesQuickly()
        {
            var engine = new StepParserEngine();
            engine.LoadGrammarFromContent(CycleGrammar, "issue157-cycle.grammar");

            var input = BuildXChain(200) + " x";
            var sw = Stopwatch.StartNew();
            var result = engine.Parse(input, "issue157-cycle-end.txt");
            sw.Stop();

            Assert.True(
                sw.ElapsedMilliseconds < 5_000,
                $"Parse with trailing cycle took {sw.ElapsedMilliseconds}ms; " +
                "end-of-input reduction rounds are likely cycling (issue #157).");
        }
    }
}
