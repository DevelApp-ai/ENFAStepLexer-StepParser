using BenchmarkDotNet.Attributes;
using DevelApp.StepLexer;
using DevelApp.StepParser;

namespace DevelApp.Benchmarks
{
    /// <summary>
    /// Throughput and memory benchmarks for the <see cref="StepParserEngine"/>
    /// grammar loading and full parse pipeline.
    /// </summary>
    /// <remarks>
    /// The input is a sequence of space-separated number tokens processed with the
    /// &lt;expression&gt; ::= &lt;NUMBER&gt; production, exercising the lexer, GLR path
    /// management, reductions and CognitiveGraph building. Since production rule
    /// alternatives are expanded into separate rules, a fully-reducing list
    /// grammar is expressible; this single-alternative grammar intentionally
    /// keeps the stack growing to exercise deep-stack path cloning.
    /// </remarks>
    [MemoryDiagnoser]
    public class ParserBenchmarks
    {
        private StepParserEngine _engine = null!;
        private string _source = null!;

        private const string NumberGrammar = @"
Grammar: BenchmarkGrammar
<NUMBER> ::= /[0-9]+/
<WS> ::= /[ \t\r\n]+/ => { skip }
<expression> ::= <NUMBER>
";

        /// <summary>
        /// Gets or sets the number of tokens in the generated source.
        /// Note: both the lexer and the GLR pipeline allocate roughly
        /// linearly with token count (see #47 and #52); total allocation
        /// is on the order of tens of MB for a 1000-token parse.
        /// </summary>
        [Params(100, 1_000)]
        public int TokenCount { get; set; }

        /// <summary>
        /// Prepare a parser engine and the generated source text.
        /// </summary>
        [GlobalSetup]
        public void Setup()
        {
            _source = InputGenerator.GenerateNumberSource(TokenCount);
            _engine = new StepParserEngine();
            _engine.LoadGrammarFromContent(NumberGrammar);
        }

        /// <summary>
        /// Parse the full source through the engine (lexing + GLR parsing + graph building).
        /// </summary>
        /// <returns>The number of tokens recognized by the parse.</returns>
        [Benchmark]
        public int Engine_ParseFullPipeline()
        {
            var result = _engine.Parse(_source, "benchmark.txt");
            return result.Tokens.Count;
        }

        /// <summary>
        /// Measure grammar loading cost: create an engine and load the grammar from text.
        /// </summary>
        /// <returns><c>true</c> when a grammar was loaded successfully.</returns>
        [Benchmark]
        public bool Engine_LoadGrammar()
        {
            using var engine = new StepParserEngine();
            engine.LoadGrammarFromContent(NumberGrammar);
            return engine.CurrentGrammar != null;
        }
    }
}
