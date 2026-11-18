using BenchmarkDotNet.Attributes;
using DevelApp.StepParser;
using DevelApp.StepParser.Tests;

namespace DevelApp.Benchmarks
{
    /// <summary>
    /// Issue #101, Selective Path Invalidation: per-edit parse cost of the
    /// incremental parse path on <see cref="RealTimeParserSession"/> against
    /// the full re-parse baseline on the same engine.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Documents are ambiguous expression chains over
    /// <c>SimpleExpr.grammar</c> (<c>a + 12 + b + ...</c>): every token
    /// contributes to GLR path growth, reductions and CognitiveGraph node
    /// writes, so a full re-parse pays the whole document per edit while
    /// the incremental edit resumes from the cached parser boundary below
    /// the edit.
    /// </para>
    /// <para>
    /// The warm-edit benchmark applies an insert immediately followed by
    /// the inverse delete at the same position, so the document stays
    /// stable across invocations while both operations remain warm cache
    /// hits; the reported per-edit cost is half of the measured pair.
    /// </para>
    /// <para>
    /// Larger single-expression documents (1600+ terms on this grammar)
    /// currently exhaust the host during the final reduction phase of a
    /// cold parse; that pre-existing limitation is tracked in
    /// docs/Incremental-Parsing.md and bounds the largest parameter here.
    /// </para>
    /// </remarks>
    [MemoryDiagnoser]
    public class IncrementalParsingBenchmarks
    {
        private static readonly string ExprGrammar =
            TestGrammars.Get("test-grammars/step-parser-tests/StepParserTests/SimpleExpr.grammar");

        private StepParserEngine _engine = null!;
        private RealTimeParserSession _session = null!;
        private string _source = null!;
        private int _editOffset;

        /// <summary>
        /// Gets or sets the number of <c>term +</c> pairs in the generated
        /// expression chain (2n+1 tokens).
        /// </summary>
        [Params(25, 100, 400)]
        public int Terms { get; set; }

        /// <summary>
        /// Gets or sets the relative position of the edit in the document
        /// (0.5 = middle, 0.9 = near the end, the typical typing position).
        /// </summary>
        [Params(0.5, 0.9)]
        public double EditRelativePosition { get; set; }

        /// <summary>
        /// Prepare a shared engine, a parsed session with the path cache
        /// warm, and the fixed edit offset.
        /// </summary>
        [GlobalSetup]
        public void Setup()
        {
            var parts = new string[Terms];
            for (int i = 0; i < Terms; i++)
            {
                parts[i] = (i % 2 == 0 ? "a" : "12") + " + ";
            }

            _source = string.Join(string.Empty, parts) + "z";
            _editOffset = (int)(_source.Length * EditRelativePosition);

            _engine = new StepParserEngine();
            _engine.LoadGrammarFromContent(ExprGrammar);

            _session = new RealTimeParserSession(ExprGrammar, "benchmark.txt");
            _session.EnableIncrementalParsing = true;
            _session.Initialize(_source);
        }

        /// <summary>
        /// Full re-parse baseline: the same engine re-lexes and re-parses
        /// the whole (edited) document, as a batch-mode analyzer must.
        /// </summary>
        /// <returns>Whether the parse succeeded.</returns>
        [Benchmark(Baseline = true)]
        public bool FullReparse_PerEdit()
        {
            var edited = _source.Insert(_editOffset, "X");
            var result = _engine.Parse(edited, "benchmark.txt");
            return result.Success;
        }

        /// <summary>
        /// Warm Selective Path Invalidation edit: restore the cached
        /// parser boundary below the edit and step forward (insert and
        /// inverse delete pair, halved).
        /// </summary>
        /// <returns>The session version after the edit pair.</returns>
        [Benchmark]
        public int Spi_WarmEdit()
        {
            _session.ApplyEdit(_editOffset, 0, "X");
            _session.ApplyEdit(_editOffset, 1, "");
            return _session.Version;
        }
    }
}
