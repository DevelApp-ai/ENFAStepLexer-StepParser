using Xunit;
using DevelApp.StepLexer;

namespace DevelApp.StepLexer.Tests
{
    /// <summary>
    /// Extended tests for UnicodePropertyMatcher general category matching
    /// </summary>
    public class UnicodePropertyMatcherExtendedTests
    {
        [Theory]
        [InlineData('a', "L", true)]
        [InlineData('1', "L", false)]
        [InlineData('a', "LC", true)]
        [InlineData('A', "LC", true)]
        [InlineData('_', "LC", false)]
        [InlineData('a', "Ll", true)]
        [InlineData('A', "Ll", false)]
        [InlineData('A', "Lu", true)]
        [InlineData('a', "Lu", false)]
        public void MatchesProperty_LetterCategories(char ch, string property, bool expected)
        {
            // Act
            var result = UnicodePropertyMatcher.MatchesProperty(ch, property);

            // Assert
            Assert.Equal(expected, result);
        }

        [Theory]
        [InlineData('\u02B0', "Lm", true)]   // MODIFIER LETTER SMALL H
        [InlineData('a', "Lm", false)]
        [InlineData('\u4E2D', "Lo", true)]   // CJK ideograph
        [InlineData('a', "Lo", false)]
        [InlineData('\u01C5', "Lt", true)]   // LATIN CAPITAL LETTER D WITH SMALL LETTER Z WITH CARON
        [InlineData('a', "Lt", false)]
        public void MatchesProperty_LetterSubcategories(char ch, string property, bool expected)
        {
            // Act
            var result = UnicodePropertyMatcher.MatchesProperty(ch, property);

            // Assert
            Assert.Equal(expected, result);
        }

        [Theory]
        [InlineData('\u0301', "M", true)]    // COMBINING ACUTE ACCENT (non-spacing mark)
        [InlineData('\u0301', "Mn", true)]
        [InlineData('\u0903', "Mc", true)]   // DEVANAGARI SIGN VISARGA (spacing combining mark)
        [InlineData('\u0301', "Mc", false)]
        [InlineData('a', "M", false)]
        public void MatchesProperty_MarkCategories(char ch, string property, bool expected)
        {
            // Act
            var result = UnicodePropertyMatcher.MatchesProperty(ch, property);

            // Assert
            Assert.Equal(expected, result);
        }

        [Theory]
        [InlineData('1', "N", true)]
        [InlineData('a', "N", false)]
        [InlineData('1', "Nd", true)]
        [InlineData('a', "Nd", false)]
        [InlineData('\u2160', "Nl", true)]   // ROMAN NUMERAL ONE
        [InlineData('1', "Nl", false)]
        [InlineData('\u00BD', "No", true)]   // VULGAR FRACTION ONE HALF
        [InlineData('1', "No", false)]
        public void MatchesProperty_NumberCategories(char ch, string property, bool expected)
        {
            // Act
            var result = UnicodePropertyMatcher.MatchesProperty(ch, property);

            // Assert
            Assert.Equal(expected, result);
        }

        [Theory]
        [InlineData('_', "Pc", true)]        // low line is connector punctuation
        [InlineData('-', "Pd", true)]        // hyphen-minus is dash punctuation
        [InlineData(')', "Pe", true)]        // right parenthesis closes
        [InlineData('(', "Ps", true)]        // left parenthesis opens
        [InlineData('.', "Po", true)]        // full stop is other punctuation
        [InlineData('\u2018', "Pi", true)]   // left single quotation mark
        [InlineData('\u2019', "Pf", true)]   // right single quotation mark
        [InlineData('a', "P", false)]
        [InlineData('1', "Pc", false)]
        [InlineData('a', "Pd", false)]
        [InlineData('a', "Pe", false)]
        [InlineData('a', "Ps", false)]
        [InlineData('a', "Po", false)]
        [InlineData('a', "Pi", false)]
        [InlineData('a', "Pf", false)]
        public void MatchesProperty_PunctuationCategories(char ch, string property, bool expected)
        {
            // Act
            var result = UnicodePropertyMatcher.MatchesProperty(ch, property);

            // Assert
            Assert.Equal(expected, result);
        }

        [Theory]
        [InlineData('+', "Sm", true)]        // plus sign is a math symbol
        [InlineData('$', "Sc", true)]        // dollar sign is a currency symbol
        [InlineData('^', "Sk", true)]        // circumflex accent is a modifier symbol
        [InlineData('\u00A9', "So", true)]   // copyright sign is an other symbol
        [InlineData('a', "S", false)]
        [InlineData('a', "Sm", false)]
        [InlineData('a', "Sc", false)]
        [InlineData('a', "Sk", false)]
        [InlineData('a', "So", false)]
        public void MatchesProperty_SymbolCategories(char ch, string property, bool expected)
        {
            // Act
            var result = UnicodePropertyMatcher.MatchesProperty(ch, property);

            // Assert
            Assert.Equal(expected, result);
        }

        [Theory]
        [InlineData(' ', "Zs", true)]        // space
        [InlineData('\u00A0', "Zs", true)]   // no-break space
        [InlineData('\u2028', "Zl", true)]   // line separator
        [InlineData('\u2029', "Zp", true)]   // paragraph separator
        [InlineData('a', "Z", false)]
        [InlineData('a', "Zs", false)]
        [InlineData('a', "Zl", false)]
        [InlineData('a', "Zp", false)]
        public void MatchesProperty_SeparatorCategories(char ch, string property, bool expected)
        {
            // Act
            var result = UnicodePropertyMatcher.MatchesProperty(ch, property);

            // Assert
            Assert.Equal(expected, result);
        }

        [Theory]
        [InlineData('\u0000', "C", true)]    // null is a control character
        [InlineData('a', "C", false)]
        [InlineData('\u0000', "Cc", true)]
        [InlineData('a', "Cc", false)]
        [InlineData('\uD800', "Cs", true)]   // high surrogate
        [InlineData('a', "Cs", false)]
        [InlineData('\uE000', "Co", true)]   // private use area
        [InlineData('a', "Co", false)]
        [InlineData('\u200B', "Cf", true)]   // zero width space is a format character
        [InlineData('a', "Cf", false)]
        public void MatchesProperty_OtherCategories(char ch, string property, bool expected)
        {
            // Act
            var result = UnicodePropertyMatcher.MatchesProperty(ch, property);

            // Assert
            Assert.Equal(expected, result);
        }

        [Theory]
        [InlineData('a', "Basic_Latin", true)]
        [InlineData('\u00E9', "Basic_Latin", false)]
        [InlineData('\u00E9', "Latin_1_Supplement", true)]
        [InlineData('\u0100', "Latin_1_Supplement", false)]
        [InlineData('\u0100', "Latin_Extended_A", true)]
        [InlineData('\u03B1', "Greek_and_Coptic", true)]   // Greek alpha
        [InlineData('\u0430', "Cyrillic", true)]           // Cyrillic a
        [InlineData('\u05D0', "Hebrew", true)]             // Hebrew alef
        [InlineData('\u0627', "Arabic", true)]             // Arabic alef
        [InlineData('\u3042', "Hiragana", true)]
        [InlineData('\u30A2', "Katakana", true)]
        [InlineData('\u4E2D', "CJK_Unified_Ideographs", true)]
        [InlineData('a', "Greek_and_Coptic", false)]
        public void MatchesProperty_UnicodeBlocks(char ch, string property, bool expected)
        {
            // Act
            var result = UnicodePropertyMatcher.MatchesProperty(ch, property);

            // Assert
            Assert.Equal(expected, result);
        }

        [Theory]
        [InlineData('a', "Alphabetic", true)]
        [InlineData('1', "Alphabetic", false)]
        [InlineData('1', "ASCII_Hex_Digit", true)]
        [InlineData('f', "ASCII_Hex_Digit", true)]
        [InlineData('g', "ASCII_Hex_Digit", false)]
        public void MatchesProperty_BinaryProperties(char ch, string property, bool expected)
        {
            // Act
            var result = UnicodePropertyMatcher.MatchesProperty(ch, property);

            // Assert
            Assert.Equal(expected, result);
        }
    }
}
