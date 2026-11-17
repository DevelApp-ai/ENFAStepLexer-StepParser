using System;
using System.Linq;
using DevelApp.StepLexer;
using DevelApp.StepParser;
using Xunit;
using Xunit.Abstractions;

namespace DevelApp.StepParser.Tests
{
    /// <summary>
    /// Real-file lexing acceptance for issue #129 / Minotaur-Grammars #194:
    /// a real language grammar from the Minotaur-Grammars repository must
    /// lex a whole real-world source file to completion as a single
    /// coherent token path in <see cref="LexingMode.GreedyLongestMatch"/>,
    /// with no LX1001/LX1002 lexer diagnostics.
    ///
    /// Grammar and input are resolved through <see cref="TestGrammars"/>:
    /// from a local Minotaur-Grammars checkout (MINOTAUR_GRAMMARS_DIR, set by
    /// the grammar-repository-tests CI job and by Minotaur-Grammars CI) or,
    /// without it, by HTTP fetch from the Minotaur-Grammars main branch -
    /// the same mechanism the other TestGrammars-based suites use, so this
    /// runs in the engine's own CI without any extra wiring. When neither
    /// source is reachable the test skips, mirroring the repository-facing
    /// gate in <see cref="GrammarRepositoryConformanceTests"/>.
    ///
    /// The wall-clock acceptance (10 KB in &lt; 1 s) stays pinned in
    /// <see cref="Issue133GreedyLexingTests"/>; this test pins the structural
    /// contract (full coverage, no stalls, non-zero tokens) on a 41 KB real
    /// file that exercises doc-comment blocks, raw/interpolated strings and
    /// escaped quotes.
    /// </summary>
    public class RealFileLexingTests
    {
        private const string GrammarPath =
            "programming-languages/csharp10/CSharp10.grammar";
        private const string RealFilePath =
            "programming-languages/csharp10/conformance/SBoBVerificationTests.cs";

        private readonly ITestOutputHelper _output;

        public RealFileLexingTests(ITestOutputHelper output)
        {
            _output = output;
        }

        [Fact]
        public void CSharp10_LexesRealWorldFile_ToCompletion_SingleTokenPath()
        {
            string grammar;
            string input;
            try
            {
                grammar = TestGrammars.Get(GrammarPath);
                input = TestGrammars.Get(RealFilePath);
            }
            catch (InvalidOperationException ex)
            {
                _output.WriteLine(
                    "Minotaur-Grammars content unreachable (no MINOTAUR_GRAMMARS_DIR, " +
                    "no network) - skipping real-file lexing test. " + ex.Message);
                return;
            }

            Assert.True(input.Length > 10_000,
                $"real-file pin must be a file-scale input, got {input.Length} chars");

            using var engine = new StepParserEngine();
            engine.LoadGrammarFromContent(grammar);
            engine.LexingMode = LexingMode.GreedyLongestMatch;

            var result = engine.Lex(input, "SBoBVerificationTests.cs");

            // Same acceptance semantics as the Conformance.txt lex: gate
            // (GrammarRepositoryConformanceTests.ValidateCase).
            Assert.True(result.Success,
                "real file must lex to completion: " + string.Join("; ", result.Errors));
            Assert.DoesNotContain(result.Diagnostics,
                d => d.Code == DiagnosticCodes.LexerUnexpectedInput
                  || d.Code == DiagnosticCodes.LexerStalled);
            Assert.NotEmpty(result.Tokens);
            Assert.All(result.Tokens, t => Assert.True(t.Length > 0,
                "no zero-length tokens may be emitted"));

            // Single coherent token path (greedy mode keeps one path per
            // step): token spans are strictly ordered and never overlap.
            // Skipped matches (whitespace/comments) advance the position
            // without emitting tokens, so they appear as gaps, not tokens.
            var ordered = result.Tokens
                .Select(t => (t.StartPosition, End: t.StartPosition + t.Length))
                .ToList();
            Assert.True(ordered.SequenceEqual(ordered.OrderBy(s => s.StartPosition)),
                "tokens must be emitted in input order");
            for (var i = 1; i < ordered.Count; i++)
            {
                Assert.True(ordered[i].StartPosition >= ordered[i - 1].End,
                    $"token spans overlap at index {i}: {ordered[i - 1]} vs {ordered[i]}");
            }
        }
    }
}
