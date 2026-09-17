using System.Text.RegularExpressions;
using BenchmarkDotNet.Attributes;
using DevelApp.StepLexer;

// The lexer class shares its name with the DevelApp.StepLexer namespace; alias it
// so the simple name resolves to the type
using Lexer = DevelApp.StepLexer.StepLexer;

namespace DevelApp.Benchmarks
{
    /// <summary>
    /// Throughput and memory benchmarks for the <see cref="Lexer"/> tokenization
    /// pipeline, compared against a compiled .NET <see cref="Regex"/> baseline.
    /// </summary>
    [MemoryDiagnoser]
    public class LexerBenchmarks
    {
        private Lexer _lexer = null!;
        private Regex _compiledRegex = null!;
        private byte[] _utf8Input = null!;
        private string _input = null!;
        private ReadOnlyMemory<byte> _inputMemory;

        /// <summary>
        /// Gets or sets the number of tokens in the generated input.
        /// Note: line/column lookup and path merging are incremental, so
        /// tokenization cost scales linearly with input size.
        /// </summary>
        [Params(1_000, 10_000)]
        public int TokenCount { get; set; }

        /// <summary>
        /// Prepare the lexer, input and baseline regex.
        /// </summary>
        [GlobalSetup]
        public void Setup()
        {
            _input = InputGenerator.GenerateMixedSource(TokenCount);
            _utf8Input = System.Text.Encoding.UTF8.GetBytes(_input);
            _inputMemory = new ReadOnlyMemory<byte>(_utf8Input);

            _compiledRegex = new Regex(@"(value\d+|name_\d+|[0-9]+|[+\-])", RegexOptions.Compiled);

            _lexer = CreateLexer();
        }

        /// <summary>
        /// Run the full StepLexer pipeline: initialize over the UTF-8 input and
        /// step until tokenization is complete.
        /// </summary>
        /// <returns>The number of tokens produced.</returns>
        [Benchmark(Baseline = true)]
        public int StepLexer_Tokenize()
        {
            _lexer.Initialize(_inputMemory, "benchmark.txt");
            var tokenCount = 0;
            var steps = 0;
            var maxSteps = _utf8Input.Length * 10;

            while (steps < maxSteps)
            {
                var result = _lexer.Step();
                tokenCount += result.NewTokens.Count;
                steps++;

                if (result.IsComplete)
                {
                    break;
                }
            }

            return tokenCount;
        }

        /// <summary>
        /// Baseline: tokenize the same input with a compiled .NET regex.
        /// </summary>
        /// <returns>The number of tokens produced.</returns>
        [Benchmark]
        public int DotNetCompiledRegex_Tokenize()
        {
            var tokenCount = 0;
            var matches = _compiledRegex.Matches(_input);
            foreach (Match match in matches)
            {
                if (match.Length > 0)
                {
                    tokenCount++;
                }
            }
            return tokenCount;
        }

        /// <summary>
        /// Run only phase 1 (lexical scan) of the lexer pipeline.
        /// </summary>
        /// <returns><c>true</c> if the scan completed successfully.</returns>
        [Benchmark]
        public bool StepLexer_Phase1ScanOnly()
        {
            var view = new ZeroCopyStringView(_utf8Input);
            return _lexer.Phase1_LexicalScan(view);
        }

        /// <summary>
        /// Tokenize space-separated numbers with a NUMBER rule and a
        /// skippable WS rule — the minimal grammar used to track lexer
        /// scaling (#52). Total allocation and time must grow linearly
        /// with <see cref="TokenCount"/>.
        /// </summary>
        /// <returns>The number of tokens produced.</returns>
        [Benchmark]
        public int StepLexer_TokenizeNumbersOnly()
        {
            var lexer = new Lexer();
            lexer.AddRule(new TokenRule("NUMBER", @"/[0-9]+/"));
            lexer.AddRule(new TokenRule("WS", @"/[ \t\r\n]+/") { IsSkippable = true });

            var utf8Input = System.Text.Encoding.UTF8.GetBytes(InputGenerator.GenerateNumberSource(TokenCount));
            lexer.Initialize(new ReadOnlyMemory<byte>(utf8Input), "benchmark.txt");
            var tokenCount = 0;
            var steps = 0;
            var maxSteps = utf8Input.Length * 10;

            while (steps < maxSteps)
            {
                var result = lexer.Step();
                tokenCount += result.NewTokens.Count;
                steps++;

                if (result.IsComplete)
                {
                    break;
                }
            }

            return tokenCount;
        }

        /// <summary>
        /// Create a lexer configured with token rules covering the generated input.
        /// Patterns use the grammar syntax understood by the lexer: /regex/ or "literal".
        /// </summary>
        private static Lexer CreateLexer()
        {
            var lexer = new Lexer();
            lexer.AddRule(new TokenRule("IDENTIFIER", @"/[a-zA-Z][a-zA-Z0-9]*/"));
            lexer.AddRule(new TokenRule("NUMBER", @"/[0-9]+/"));
            lexer.AddRule(new TokenRule("PLUS", @"""+"""));
            lexer.AddRule(new TokenRule("MINUS", @"""-"""));
            lexer.AddRule(new TokenRule("WS", @"/[ \t\r\n]+/") { IsSkippable = true });
            return lexer;
        }
    }
}
