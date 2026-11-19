using BenchmarkDotNet.Attributes;
using DevelApp.StepLexer;
using DevelApp.StepParser;

namespace DevelApp.Benchmarks
{
    /// <summary>
    /// Per-edit latency of <see cref="RealTimeParserSession"/> (issue #101,
    /// incremental-parsing-ides article): 100 deterministic edits against a
    /// generated document, either applied incrementally to one long-lived
    /// session (windowed re-lex + tail reuse, issue #144) or by re-initializing
    /// a fresh session per edit (full re-lex, the "full re-parse" baseline at
    /// lex level). The non-BDN <c>--measure-issue101</c> mode adds the
    /// committed corpus/traces, p50/p95, reuse ratios, cold-start and parse-time
    /// tables on top of this comparison.
    /// </summary>
    [MemoryDiagnoser]
    public class SessionEditLatencyBenchmarks
    {
        private const string WordGrammar =
            "Grammar: SessionBench\n" +
            "<start> ::= <WORD>*\n" +
            "<WORD> ::= /[a-zA-Z0-9]+/\n" +
            "<WS> ::= /[ \\t\\r\\n]+/ => { skip }\n";

        private const int EditsPerRun = 100;

        private string _doc = null!;

        /// <summary>Document size in lines.</summary>
        [Params(500, 2_000)]
        public int Lines { get; set; } = 500;

        /// <summary>Generate the deterministic document.</summary>
        [GlobalSetup]
        public void Setup()
        {
            var parts = new List<string>(Lines);
            for (var i = 0; i < Lines; i++)
            {
                parts.Add("word" + i);
            }
            _doc = string.Join("\n", parts) + "\n";
        }

        /// <summary>Baseline: per edit, a fresh session over the whole edited document (full re-lex).</summary>
        [Benchmark(Baseline = true)]
        public int FullRelexPerEdit()
        {
            var text = _doc;
            var tokens = 0;
            for (var i = 0; i < EditsPerRun; i++)
            {
                var offset = (i * 7_919) % text.Length;
                text = text.Substring(0, offset) + "x" + text.Substring(offset);
                var session = new RealTimeParserSession(WordGrammar, "bench.txt")
                {
                    LexingMode = LexingMode.GreedyLongestMatch,
                };
                session.Initialize(text);
                tokens = session.Tokens.Count;
            }
            return tokens;
        }

        /// <summary>Incremental: one session, per-edit <see cref="RealTimeParserSession.ApplyEdit"/>.</summary>
        [Benchmark]
        public int IncrementalApplyEdit()
        {
            var session = new RealTimeParserSession(WordGrammar, "bench.txt")
            {
                LexingMode = LexingMode.GreedyLongestMatch,
            };
            session.Initialize(_doc);
            var tokens = 0;
            for (var i = 0; i < EditsPerRun; i++)
            {
                var offset = (i * 7_919) % session.Text.Length;
                session.ApplyEdit(offset, 0, "x");
                tokens = session.Tokens.Count;
            }
            return tokens;
        }
    }
}
