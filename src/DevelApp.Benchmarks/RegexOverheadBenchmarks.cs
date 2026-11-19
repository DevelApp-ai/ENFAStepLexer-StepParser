using System.Text;
using System.Text.RegularExpressions;
using BenchmarkDotNet.Attributes;
using DevelApp.StepLexer;

// The lexer class shares its name with the DevelApp.StepLexer namespace; alias it
// so the simple name resolves to the type
using Lexer = DevelApp.StepLexer.StepLexer;

namespace DevelApp.Benchmarks
{
    /// <summary>
    /// One benign pattern class from the two-phase-regex article's
    /// "Performance Comparison on Benign Inputs" table (issue #104):
    /// the engine-side token rule (grammar syntax, with slashes), the
    /// equivalent .NET regex, and a deterministic input consisting only of
    /// tokens the class matches (separated by whitespace), so both engines
    /// scan the entire input and do comparable work.
    /// </summary>
    public sealed record PatternClass(string Name, string EngineRule, string DotNetPattern, Func<string> CreateInput)
    {
        /// <summary>Base-26 letters-only word for index i (a, b, ..., z, aa, ...), so class inputs contain no digits.</summary>
        public static string Letters(int i)
        {
            var sb = new StringBuilder();
            do
            {
                sb.Insert(0, (char)('a' + i % 26));
                i /= 26;
            } while (i > 0);
            return sb.ToString();
        }

        public static readonly IReadOnlyList<PatternClass> All = new List<PatternClass>
        {
            new("simple-literal",
                "/hello/",
                "hello",
                () => string.Concat(Enumerable.Repeat("hello ", 2_000))),
            new("character-class",
                "/[a-z]+/",
                "[a-z]+",
                () => string.Join(" ", Enumerable.Range(0, 10_000).Select(i => Letters(i)))),
            new("basic-quantifier",
                "/[0-9]{1,5}/",
                "[0-9]{1,5}",
                () => InputGenerator.GenerateNumberSource(10_000)),
            new("complex",
                "/(value|name)[0-9]*\\x20[0-9]+/",
                "(value|name)[0-9]*\\x20[0-9]+",
                () => string.Concat(Enumerable.Range(0, 2_000).Select(i => $"value{i} {100 + i} "))),
        };
    }

    /// <summary>
    /// Benign-pattern overhead of the two-phase engine vs compiled .NET Regex
    /// (issue #104, measurement 1): for each article pattern class, lex a
    /// generated input with a single-rule greedy <see cref="Lexer"/> and count
    /// the same matches with <see cref="Regex.Matches(string)"/>. The
    /// BenchmarkDotNet Ratio column is the per-class overhead; the
    /// <c>--measure-issue104</c> mode additionally reports medians, overhead
    /// percentages and allocation ratios as CSV/JSON/Markdown.
    /// </summary>
    [MemoryDiagnoser]
    public class RegexOverheadBenchmarks
    {
        private PatternClass _pc = null!;
        private Lexer _lexer = null!;
        private Regex _regex = null!;
        private byte[] _utf8Input = null!;
        private string _input = null!;

        /// <summary>Pattern class from the article table.</summary>
        [Params("simple-literal", "character-class", "basic-quantifier", "complex")]
        public string Class { get; set; } = "simple-literal";

        /// <summary>Prepare the class input, the single-rule lexer and the baseline regex.</summary>
        [GlobalSetup]
        public void Setup()
        {
            _pc = PatternClass.All.First(c => c.Name == Class);
            _input = _pc.CreateInput();
            _utf8Input = Encoding.UTF8.GetBytes(_input);
            _regex = new Regex(_pc.DotNetPattern, RegexOptions.Compiled);
            _lexer = CreateLexer(_pc);
        }

        /// <summary>Baseline: count matches of the class pattern with compiled .NET Regex.</summary>
        [Benchmark(Baseline = true)]
        public int DotNetCompiledRegex_Class()
        {
            var count = 0;
            foreach (Match m in _regex.Matches(_input))
            {
                if (m.Length > 0)
                {
                    count++;
                }
            }
            return count;
        }

        /// <summary>
        /// Two-phase engine, phase 1 only: the raw lexical scan (greedy NFA match per
        /// position, no path/step machinery) - the closest analogue of a traditional
        /// regex engine's match loop.
        /// </summary>
        [Benchmark]
        public int StepLexer_Phase1Scan_Class()
        {
            _lexer.Phase1_LexicalScan(new ZeroCopyStringView(_utf8Input));
            return _lexer.Phase1Results.Count;
        }

        /// <summary>Two-phase engine: re-initialize the single-rule greedy lexer and step to completion.</summary>
        [Benchmark]
        public int StepLexer_Class()
        {
            _lexer.Initialize(_utf8Input, "benchmark.txt");
            var count = 0;
            var guard = 0;
            var maxSteps = _utf8Input.Length * 10 + 100;
            while (_lexer.ActivePaths.Any(p => p.IsValid && p.Position < _utf8Input.Length))
            {
                var result = _lexer.Step();
                count += result.NewTokens.Count;
                if (++guard > maxSteps)
                {
                    break;
                }
            }
            return count;
        }

        /// <summary>Create a greedy single-target-rule lexer plus a skippable whitespace rule.</summary>
        public static Lexer CreateLexer(PatternClass pc)
        {
            var lexer = new Lexer { Mode = LexingMode.GreedyLongestMatch };
            lexer.AddRule(new TokenRule("TARGET", pc.EngineRule));
            lexer.AddRule(new TokenRule("WS", @"/[ \t\r\n]+/") { IsSkippable = true });
            return lexer;
        }
    }
}
