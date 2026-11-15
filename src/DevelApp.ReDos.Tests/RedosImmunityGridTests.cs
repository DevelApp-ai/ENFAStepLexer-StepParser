using System.Linq;
using Xunit;
using Xunit.Abstractions;

namespace DevelApp.ReDos.Tests
{
    /// <summary>
    /// The full immunity grid (issue #103 extension): every catalog
    /// pattern - article, corpus and the supported scaling analogues,
    /// including the multibyte and case-folding shapes - on every
    /// adversarial length, as one xunit theory case each. The per-pattern
    /// theories in <see cref="RedosImmunityTests"/> prove the property
    /// per pattern; this grid proves it per (pattern, length) pair so a
    /// single regression at one length of one pattern fails a named,
    /// individually identifiable test case in CI.
    /// </summary>
    public class RedosImmunityGridTests
    {
        private readonly ITestOutputHelper _output;

        public RedosImmunityGridTests(ITestOutputHelper output)
        {
            _output = output;
        }

        /// <summary>
        /// Every (pattern, length) pair in the catalog: article/corpus
        /// patterns plus scaling analogues, each at 1 KB, 10 KB and 100 KB.
        /// </summary>
        public static TheoryData<string, int> GridCases
        {
            get
            {
                var data = new TheoryData<string, int>();
                foreach (var pattern in RedosPatternCatalog.ArticleAndCorpusPatterns.Concat(RedosPatternCatalog.ScalingPatterns))
                {
                    foreach (var length in new[]
                    {
                        RedosPatternCatalog.SmallLengthBytes,
                        RedosPatternCatalog.MediumLengthBytes,
                        RedosPatternCatalog.LargeLengthBytes,
                    })
                    {
                        data.Add(pattern.Name, length);
                    }
                }

                return data;
            }
        }

        [Theory]
        [MemberData(nameof(GridCases))]
        public void StepLexer_CompletesAdversarialPair_WithinHardTimeout(string name, int length)
        {
            var pattern = RedosPatternCatalog.FindPattern(name);
            var input = pattern.BuildInput(length);
            var result = RedosMeasurementHarness.RunStepLexer(pattern.LexerRulePattern, input);

            Assert.True(
                result.Completed,
                $"{name} on {length} bytes of adversarial input did not complete within "
                    + $"{RedosMeasurementHarness.StepLexerHardTimeoutMs} ms. "
                    + "This is a ReDoS regression: the StepLexer matcher must never backtrack.");

            _output.WriteLine(
                $"{name} @ {length} bytes: {result.ElapsedMilliseconds:F3} ms, tokens={result.TokenCount}, " +
                $"no-match-at={result.LastNoMatchPosition}, completed-input={result.CompletedInput}");
        }
    }
}
