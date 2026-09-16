using BenchmarkDotNet.Attributes;
using DevelApp.StepLexer;

namespace DevelApp.Benchmarks
{
    /// <summary>
    /// Throughput and memory benchmarks for Unicode property matching and
    /// UTF-8 codepoint decoding.
    /// </summary>
    [MemoryDiagnoser]
    public class UnicodeBenchmarks
    {
        private int[] _codepoints = null!;
        private byte[] _utf8Input = null!;

        /// <summary>
        /// Gets or sets the Unicode general category or block to test.
        /// </summary>
        [Params("L", "Nd", "Lu", "Greek_and_Coptic", "CJK_Unified_Ideographs")]
        public string Property { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the number of codepoints in the test set.
        /// </summary>
        [Params(10_000)]
        public int CodepointCount { get; set; }

        /// <summary>
        /// Generate the deterministic mixed-codepoint test set.
        /// </summary>
        [GlobalSetup]
        public void Setup()
        {
            _codepoints = InputGenerator.GenerateMixedCodepoints(CodepointCount);
            _utf8Input = InputGenerator.ToUtf8(_codepoints);
        }

        /// <summary>
        /// Match every codepoint against the configured Unicode property.
        /// </summary>
        /// <returns>The number of matching codepoints.</returns>
        [Benchmark(Baseline = true)]
        public int UnicodePropertyMatcher_MatchesProperty()
        {
            var matches = 0;
            foreach (var codepoint in _codepoints)
            {
                if (UnicodePropertyMatcher.MatchesProperty(codepoint, Property))
                {
                    matches++;
                }
            }
            return matches;
        }

        /// <summary>
        /// Baseline: .NET's built-in <see cref="char"/> classification for the
        /// BMP subset of the same codepoints.
        /// </summary>
        /// <returns>The number of codepoints classified as letters or digits.</returns>
        [Benchmark]
        public int DotNet_CharClassification()
        {
            var matches = 0;
            foreach (var codepoint in _codepoints)
            {
                if (codepoint <= 0xFFFF)
                {
                    var c = (char)codepoint;
                    if (char.IsLetterOrDigit(c))
                    {
                        matches++;
                    }
                }
            }
            return matches;
        }

        /// <summary>
        /// Decode the whole UTF-8 buffer codepoint by codepoint with
        /// <see cref="UTF8Utils.GetNextCodepoint"/>.
        /// </summary>
        /// <returns>The number of codepoints decoded.</returns>
        [Benchmark]
        public int UTF8Utils_DecodeCodepoints()
        {
            var span = new ReadOnlySpan<byte>(_utf8Input);
            var decoded = 0;
            var position = 0;
            while (position < span.Length)
            {
                var (_, bytesConsumed) = UTF8Utils.GetNextCodepoint(span, position);
                if (bytesConsumed <= 0)
                {
                    break;
                }
                position += bytesConsumed;
                decoded++;
            }
            return decoded;
        }

        /// <summary>
        /// Baseline: decode the same buffer with the framework's UTF8 decoder.
        /// </summary>
        /// <returns>The number of characters decoded.</returns>
        [Benchmark]
        public int DotNet_Utf8Decoding()
        {
            return System.Text.Encoding.UTF8.GetCharCount(_utf8Input);
        }
    }
}
