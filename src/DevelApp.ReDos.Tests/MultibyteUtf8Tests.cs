using System.Text;
using Xunit;
using Xunit.Abstractions;

namespace DevelApp.ReDos.Tests
{
    /// <summary>
    /// Multibyte UTF-8 adversarial coverage (issue #103 extension): the
    /// Danish letters æ, ø and å are two UTF-8 bytes each, so an input of
    /// them attacks the matcher's code-point decoding. The assertions pin
    /// the observable outcomes through the same harness observables the
    /// immunity suite uses (token count, no-match position, input
    /// completion): multibyte input must match whole code points, an
    /// ASCII-only pattern must fail fast at byte 0 rather than mis-lexing
    /// a lead byte, and the multibyte scaling shapes must stay bounded at
    /// adversarial lengths.
    /// </summary>
    public class MultibyteUtf8Tests
    {
        private readonly ITestOutputHelper _output;

        public MultibyteUtf8Tests(ITestOutputHelper output)
        {
            _output = output;
        }

        [Fact]
        public void DanishInput_BuildInput_NeverEndsInsideAMultibyteSequence()
        {
            // The catalog's Danish input must truncate on a character
            // boundary: every requested length must produce valid UTF-8
            // that round-trips, and only whole æ/ø/å letters.
            foreach (var length in new[] { 1, 2, 5, 101, 1_024, 10_240, 102_400 })
            {
                var bytes = RedosPatternCatalog.FindPattern("Scaling: [a-zæøå]+ Danish multibyte run").BuildInput(length);
                Assert.True(bytes.Length <= length, $"Danish input must not exceed the {length}-byte budget.");
                var text = Encoding.UTF8.GetString(bytes);
                Assert.All(text, c => Assert.True("æøå".Contains(c)));
                _output.WriteLine($"{length} requested -> {bytes.Length} bytes, {text.Length} code points");
            }
        }

        [Fact]
        public void DanishClass_MatchesMultibyteRun_AsSingleToken()
        {
            // [a-zæøå]+ over a full Danish run must consume every code
            // point of the 10 KB input as one token and complete: the
            // matcher advances by decoded code points, never by bytes.
            var pattern = RedosPatternCatalog.FindPattern("Scaling: [a-zæøå]+ Danish multibyte run");
            var input = pattern.BuildInput(RedosPatternCatalog.MediumLengthBytes);
            var result = RedosMeasurementHarness.RunStepLexer(pattern.LexerRulePattern, input);

            Assert.True(result.Completed);
            Assert.Equal(1, result.TokenCount);
            Assert.True(result.CompletedInput);
            Assert.Equal(-1, result.LastNoMatchPosition);
        }

        [Fact]
        public void NegatedClass_MatchesMultibyteRun_AsSingleToken()
        {
            // [^x]* over the Danish run consumes everything (no 'x' bytes
            // in æ, ø or å) and completes; the negated predicate must
            // evaluate whole code points, not single bytes.
            var pattern = RedosPatternCatalog.FindPattern("Scaling: [^x]* negated Danish run");
            var input = pattern.BuildInput(RedosPatternCatalog.MediumLengthBytes);
            var result = RedosMeasurementHarness.RunStepLexer(pattern.LexerRulePattern, input);

            Assert.True(result.Completed);
            Assert.Equal(1, result.TokenCount);
            Assert.True(result.CompletedInput);
        }

        [Fact]
        public void AsciiOnlyClass_StallsAtFirstByte_OnMultibyteInput()
        {
            // [a-z]+ cannot match the lead byte of 'æ' (0xC3): the rule
            // must fail fast at position 0 and the run must terminate
            // linearly - the multibyte analogue of the fail-fast immunity.
            var pattern = RedosPatternCatalog.FindPattern("Scaling: [a-zæøå]+ Danish multibyte run");
            var input = pattern.BuildInput(RedosPatternCatalog.SmallLengthBytes);
            var result = RedosMeasurementHarness.RunStepLexer("/[a-z]+/", input);

            Assert.True(result.Completed);
            Assert.Equal(0, result.TokenCount);
            Assert.Equal(0, result.LastNoMatchPosition);
            Assert.False(result.CompletedInput);
        }

        [Fact]
        public void UnicodeLetterProperty_MatchesAsciiRun_AtAdversarialLength()
        {
            // \p{L}+ is the Unicode-property scaling shape: over 100 KB of
            // 'a's it must complete linearly and consume the input.
            var pattern = RedosPatternCatalog.FindPattern(@"Scaling: \p{L}+ full run");
            var input = pattern.BuildInput(RedosPatternCatalog.LargeLengthBytes);
            var result = RedosMeasurementHarness.RunStepLexer(pattern.LexerRulePattern, input);

            Assert.True(result.Completed);
            Assert.Equal(1, result.TokenCount);
            Assert.True(result.CompletedInput);
        }

        [Fact]
        public void SingleDanishLetterLiteral_AdvancesByWholeCodePoints()
        {
            // A literal 'å' rule over "åå" emits two tokens of one letter
            // each and completes: the lexer must advance past the full
            // two-byte code point, never stall or split mid-sequence.
            var input = Encoding.UTF8.GetBytes("åå");
            var result = RedosMeasurementHarness.RunStepLexer("/å/", input);

            Assert.True(result.Completed);
            Assert.Equal(2, result.TokenCount);
            Assert.True(result.CompletedInput);
        }
    }
}
