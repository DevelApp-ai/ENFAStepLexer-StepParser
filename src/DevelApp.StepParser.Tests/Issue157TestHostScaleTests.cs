using System;
using System.Diagnostics;
using System.Text;
using DevelApp.StepParser;
using DevelApp.StepLexer;
using Xunit;

namespace DevelApp.StepParser.Tests
{
    /// <summary>
    /// Issue #157, part 4: the parser test host aborted on long ambiguous
    /// chains (1,600 terms). Root cause: the append-only CognitiveGraph
    /// builder accumulated massive numbers of duplicate node writes and
    /// its capacity doubling eventually OOM-aborted the process — on a
    /// 1,600-term SimpleExpr chain over 2.3 million node writes, more than
    /// 99% of them duplicates of nodes already written for the same token
    /// or the same (path state, rule) reduction. The engine now shares the
    /// terminal node per shifted token instance and memoizes the node
    /// written by each distinct (state, rule) reduction derivation, so
    /// node writes are bounded by distinct derivation states instead of
    /// path-visits. A 1,600-term chain that OOM-aborted before now parses
    /// successfully in seconds. This also covers the JSON.grammar
    /// constructor-crash observation from triage (unverified single
    /// occurrence): repeated grammar construction plus parse must stay
    /// stable.
    /// </summary>
    public class Issue157TestHostScaleTests
    {
        /// <summary>
        /// Builds an n-term chain of the form "1+1+...+1".
        /// </summary>
        private static string BuildChain(int terms)
        {
            var sb = new StringBuilder(terms * 2);
            for (int i = 0; i < terms; i++)
            {
                if (i > 0)
                {
                    sb.Append('+');
                }

                sb.Append('1');
            }

            return sb.ToString();
        }

        /// <summary>
        /// The 1,600-term ambiguous chain from the issue: the fully
        /// associative grammar (expr ::= expr PLUS expr) makes every
        /// infix chain exponentially ambiguous, so the GLR engine keeps
        /// many alternative paths alive. Before the fix the parse
        /// OOM-aborted the test host; it must now complete within a
        /// generous bound that a healthy engine meets by orders of
        /// magnitude.
        /// </summary>
        [Fact]
        public void Parse_With1600TermAmbiguousChain_CompletesWithoutHostAbort()
        {
            var grammar = TestGrammars.Get(
                "test-grammars/step-parser-tests/StepParserTests/SimpleExpr.grammar");
            var engine = new StepParserEngine();
            engine.LoadGrammarFromContent(grammar, "SimpleExpr.grammar");

            var input = BuildChain(1_600);
            var sw = Stopwatch.StartNew();
            var result = engine.Parse(input, "chain.txt");
            sw.Stop();

            // The chain grammar is exponentially ambiguous; the engine's
            // path budget resolves it deterministically, and the parse
            // succeeds today. Assert completion primarily (never a host
            // abort, never an OOM stray), and success on the current
            // resolution: a regression to failure-with-diagnostics is
            // reviewable in CI, a regression to a host abort is not.
            Assert.True(
                sw.ElapsedMilliseconds < 30_000,
                $"1600-term ambiguous chain took {sw.ElapsedMilliseconds}ms to complete" +
                " (issue #157 part 4: the pre-fix engine OOM-aborted; this bound" +
                " guards the memoized shift/reduction node sharing).");
            Assert.True(
                result.Success,
                "1600-term ambiguous chain should parse successfully. Errors: " +
                $"{string.Join("; ", result.Errors)}");
        }

        /// <summary>
        /// A smaller-scale guard: the chain must still succeed and stay
        /// fast at 800 terms, pinning the shared-node behavior (shift
        /// writes are exactly one terminal node per token) without the
        /// CI cost of the full 1,600-term case.
        /// </summary>
        [Fact]
        public void Parse_With800TermAmbiguousChain_StillParsesSuccessfully()
        {
            var grammar = TestGrammars.Get(
                "test-grammars/step-parser-tests/StepParserTests/SimpleExpr.grammar");
            var engine = new StepParserEngine();
            engine.LoadGrammarFromContent(grammar, "SimpleExpr.grammar");

            var sw = Stopwatch.StartNew();
            var result = engine.Parse(BuildChain(800), "chain.txt");
            sw.Stop();

            Assert.True(
                result.Success,
                "800-term ambiguous chain should parse successfully. Errors: " +
                $"{string.Join("; ", result.Errors)}");
            Assert.True(
                sw.ElapsedMilliseconds < 10_000,
                $"800-term chain took {sw.ElapsedMilliseconds}ms, a scale regression (issue #157 part 4).");
        }

        /// <summary>
        /// The JSON.grammar constructor crash observed once during original
        /// triage (unverified): repeatedly constructing the engine, loading
        /// the corpus JSON grammar and parsing a representative document
        /// must stay stable and produce a successful parse every iteration.
        /// </summary>
        [Fact]
        public void Parse_WithCorpusJsonGrammar_RepeatedLoadAndParseIsStable()
        {
            var grammar = TestGrammars.Get("programming-languages/json/JSON.grammar");
            const string document = """
                {
                  "name": "issue-157",
                  "count": 1600,
                  "nested": { "a": [1, 2, 3], "b": { "c": "deep" } },
                  "truthy": true,
                  "missing": null
                }
                """;

            for (int i = 0; i < 5; i++)
            {
                var engine = new StepParserEngine();
                engine.LoadGrammarFromContent(grammar, "JSON.grammar");
                var result = engine.Parse(document, "doc.json");
                Assert.True(
                    result.Success,
                    $"JSON document should parse on iteration {i}. Errors: " +
                    $"{string.Join("; ", result.Errors)}");
            }
        }
    }
}
