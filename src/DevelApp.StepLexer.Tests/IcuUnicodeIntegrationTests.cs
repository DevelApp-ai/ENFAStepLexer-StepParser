using DevelApp.StepLexer;

namespace DevelApp.StepLexer.Tests
{
    /// <summary>
    /// Tests for the ICU Unicode integration layer: normalization through
    /// ICU when the runtime provides it and through the managed fallback
    /// tables when it does not (invariant-globalization mode). All
    /// assertions hold under both implementations.
    /// </summary>
    public class IcuUnicodeIntegrationTests
    {
        [Fact]
        public void Normalize_Nfc_ComposesCombiningSequence()
        {
            Assert.Equal("\u00e9", IcuUnicodeIntegration.Normalize("e\u0301", UnicodeNormalizationForm.NFC));
        }

        [Fact]
        public void Normalize_Nfd_DecomposesPrecomposedCharacter()
        {
            Assert.Equal("e\u0301", IcuUnicodeIntegration.Normalize("\u00e9", UnicodeNormalizationForm.NFD));
        }

        [Fact]
        public void Normalize_None_ReturnsInputUnchanged()
        {
            Assert.Equal("e\u0301abc", IcuUnicodeIntegration.Normalize("e\u0301abc", UnicodeNormalizationForm.None));
        }

        [Fact]
        public void Normalize_IsIdempotent()
        {
            var once = IcuUnicodeIntegration.Normalize("e\u0301", UnicodeNormalizationForm.NFC);
            Assert.Equal(once, IcuUnicodeIntegration.Normalize(once, UnicodeNormalizationForm.NFC));
        }

        [Fact]
        public void Normalize_Nfkc_MapsFullwidthFormsToAscii()
        {
            Assert.Equal("A", IcuUnicodeIntegration.Normalize("\uFF21", UnicodeNormalizationForm.NFKC));
        }

        [Fact]
        public void Normalize_Nfkc_ComposesAfterCompatibilityDecomposition()
        {
            // Fullwidth capital I followed by a combining acute accent
            // normalizes to the precomposed I-acute under NFKC.
            Assert.Equal("\u00CD", IcuUnicodeIntegration.Normalize("\uFF29\u0301", UnicodeNormalizationForm.NFKC));
        }

        [Fact]
        public void Normalize_Nfkd_ExpandsLigature()
        {
            Assert.Equal("fi", IcuUnicodeIntegration.Normalize("\uFB01", UnicodeNormalizationForm.NFKD));
        }

        [Fact]
        public void Normalize_Nfd_CanonicalOrdersCombiningMarks()
        {
            // Cedilla (class 202) must sort before acute (class 230)
            // regardless of the input order.
            var expected = IcuUnicodeIntegration.Normalize("a\u0327\u0301", UnicodeNormalizationForm.NFD);
            var reversed = IcuUnicodeIntegration.Normalize("a\u0301\u0327", UnicodeNormalizationForm.NFD);
            Assert.Equal(expected, reversed);
        }

        [Fact]
        public void Normalize_Nfd_DecomposesGreekAndCyrillic()
        {
            Assert.Equal("\u03B1\u0301", IcuUnicodeIntegration.Normalize("\u03AC", UnicodeNormalizationForm.NFD));
            Assert.Equal("\u0438\u0306", IcuUnicodeIntegration.Normalize("\u0439", UnicodeNormalizationForm.NFD));
        }

        [Fact]
        public void AreCanonicallyEquivalent_DetectsEquivalence()
        {
            Assert.True(IcuUnicodeIntegration.AreCanonicallyEquivalent("e\u0301", "\u00e9"));
            Assert.True(IcuUnicodeIntegration.AreCanonicallyEquivalent("\u00e9", "\u00e9"));
        }

        [Fact]
        public void AreCanonicallyEquivalent_DetectsDifference()
        {
            Assert.False(IcuUnicodeIntegration.AreCanonicallyEquivalent("e", "\u00e9"));
            Assert.False(IcuUnicodeIntegration.AreCanonicallyEquivalent("e\u0301", "e"));
        }

        [Fact]
        public void AdvancedUnicodeSupport_Normalization_GoesThroughIcuIntegration()
        {
            var unicodeSupport = new AdvancedUnicodeSupport();
            Assert.Equal(
                unicodeSupport.NormalizeIfNeeded("\u00e9", UnicodeNormalizationForm.NFC),
                unicodeSupport.NormalizeIfNeeded("e\u0301", UnicodeNormalizationForm.NFC));
            Assert.True(unicodeSupport.AreCanonicallyEquivalent("e\u0301", "\u00e9"));
        }

        [Fact]
        public void Fallback_WhenIcuUnavailable_StillNormalizes()
        {
            // On runtimes without ICU (invariant globalization) the managed
            // fallback must produce correct results for the covered scripts.
            if (!IcuUnicodeIntegration.IsFullNormalizationSupported)
            {
                Assert.Equal("\u00e9", IcuUnicodeIntegration.Normalize("e\u0301", UnicodeNormalizationForm.NFC));
                Assert.Equal("e\u0301", IcuUnicodeIntegration.Normalize("\u00e9", UnicodeNormalizationForm.NFD));
            }
        }
    }
}
