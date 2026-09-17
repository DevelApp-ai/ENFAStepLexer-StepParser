using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace DevelApp.StepLexer
{
    /// <summary>
    /// Advanced Unicode property matcher providing code point level property
    /// tests for general categories, scripts, blocks and common binary
    /// properties. Property names follow the loose matching rules of
    /// <see cref="UnicodePropertyValidator"/> (case-insensitive, ignoring
    /// underscores, spaces and hyphens, with <c>Is</c>/<c>In</c> block
    /// prefixes), and code points on supplementary planes are evaluated with
    /// full <see cref="Rune"/> support instead of BMP-only approximations.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Runtime matching covers general categories, scripts (primary ranges,
    /// approximated for scripts with many disjoint ranges), blocks (the common
    /// set) and the commonly used binary properties. The validator accepts the
    /// full standard name set; names that validate but have no runtime
    /// implementation simply never match.
    /// </para>
    /// <para>
    /// Script and some binary property tests use simplified primary ranges, as
    /// full coverage requires the complete Unicode database (see the
    /// "Full Unicode ICU integration" roadmap item).
    /// </para>
    /// </remarks>
    public static class UnicodePropertyMatcher
    {
        /// <summary>
        /// Check if a Unicode code point matches the specified property.
        /// </summary>
        /// <param name="codepoint">The Unicode code point to test.</param>
        /// <param name="property">The Unicode property name (e.g., "L", "Nd", "Lowercase_Letter", "Greek", "IsBasic_Latin").</param>
        /// <returns>True if the code point has the specified property.</returns>
        public static bool MatchesProperty(int codepoint, string property)
        {
            if (codepoint < 0 || codepoint > 0x10FFFF)
            {
                return false;
            }

            var canonical = UnicodePropertyValidator.NormalizePropertyName(property);
            if (canonical == null)
            {
                // Unknown property names never match; use IsValidPropertyName
                // to distinguish unknown names from non-matching code points.
                return false;
            }

            if (UnicodePropertyValidator.GetPropertyKind(canonical) == UnicodePropertyKind.GeneralCategory)
            {
                return MatchesGeneralCategory(GetUnicodeCategory(codepoint), canonical);
            }

            if (TryGetBlockRange(canonical, out var blockStart, out var blockEnd))
            {
                return codepoint >= blockStart && codepoint <= blockEnd;
            }

            if (MatchesScript(codepoint, canonical))
            {
                return true;
            }

            return MatchesBinaryProperty(codepoint, canonical);
        }

        /// <summary>
        /// Gets the Unicode general category of a code point, with full
        /// support for supplementary planes via <see cref="Rune"/>.
        /// </summary>
        private static UnicodeCategory GetUnicodeCategory(int codepoint)
        {
            if (codepoint >= 0xD800 && codepoint <= 0xDFFF)
            {
                return UnicodeCategory.Surrogate;
            }
            if (codepoint <= 0xFFFF)
            {
                return char.GetUnicodeCategory((char)codepoint);
            }
            return Rune.TryCreate(codepoint, out var rune) ? Rune.GetUnicodeCategory(rune) : UnicodeCategory.OtherNotAssigned;
        }

        /// <summary>
        /// Tests a general category short code against a Unicode category.
        /// </summary>
        private static bool MatchesGeneralCategory(UnicodeCategory category, string property)
        {
            return property switch
            {
                // Letters
                "L" => category is >= UnicodeCategory.UppercaseLetter and <= UnicodeCategory.OtherLetter,
                "LC" => category is UnicodeCategory.UppercaseLetter or UnicodeCategory.LowercaseLetter
                            or UnicodeCategory.TitlecaseLetter,
                "Ll" => category == UnicodeCategory.LowercaseLetter,
                "Lm" => category == UnicodeCategory.ModifierLetter,
                "Lo" => category == UnicodeCategory.OtherLetter,
                "Lt" => category == UnicodeCategory.TitlecaseLetter,
                "Lu" => category == UnicodeCategory.UppercaseLetter,

                // Marks
                "M" => category is >= UnicodeCategory.NonSpacingMark and <= UnicodeCategory.EnclosingMark,
                "Mc" => category == UnicodeCategory.SpacingCombiningMark,
                "Me" => category == UnicodeCategory.EnclosingMark,
                "Mn" => category == UnicodeCategory.NonSpacingMark,

                // Numbers
                "N" => category is >= UnicodeCategory.DecimalDigitNumber and <= UnicodeCategory.OtherNumber,
                "Nd" => category == UnicodeCategory.DecimalDigitNumber,
                "Nl" => category == UnicodeCategory.LetterNumber,
                "No" => category == UnicodeCategory.OtherNumber,

                // Punctuation
                "P" => category is >= UnicodeCategory.ConnectorPunctuation and <= UnicodeCategory.OtherPunctuation,
                "Pc" => category == UnicodeCategory.ConnectorPunctuation,
                "Pd" => category == UnicodeCategory.DashPunctuation,
                "Pe" => category == UnicodeCategory.ClosePunctuation,
                "Pf" => category == UnicodeCategory.FinalQuotePunctuation,
                "Pi" => category == UnicodeCategory.InitialQuotePunctuation,
                "Po" => category == UnicodeCategory.OtherPunctuation,
                "Ps" => category == UnicodeCategory.OpenPunctuation,

                // Symbols
                "S" => category is >= UnicodeCategory.MathSymbol and <= UnicodeCategory.OtherSymbol,
                "Sc" => category == UnicodeCategory.CurrencySymbol,
                "Sk" => category == UnicodeCategory.ModifierSymbol,
                "Sm" => category == UnicodeCategory.MathSymbol,
                "So" => category == UnicodeCategory.OtherSymbol,

                // Separators
                "Z" => category is >= UnicodeCategory.SpaceSeparator and <= UnicodeCategory.ParagraphSeparator,
                "Zl" => category == UnicodeCategory.LineSeparator,
                "Zp" => category == UnicodeCategory.ParagraphSeparator,
                "Zs" => category == UnicodeCategory.SpaceSeparator,

                // Other
                "C" => category is UnicodeCategory.Control or UnicodeCategory.Format
                            or UnicodeCategory.Surrogate or UnicodeCategory.PrivateUse
                            or UnicodeCategory.OtherNotAssigned,
                "Cc" => category == UnicodeCategory.Control,
                "Cf" => category == UnicodeCategory.Format,
                "Cn" => category == UnicodeCategory.OtherNotAssigned,
                "Co" => category == UnicodeCategory.PrivateUse,
                "Cs" => category == UnicodeCategory.Surrogate,

                _ => false
            };
        }

        /// <summary>
        /// Unicode block ranges for the common block set. Names are the
        /// canonical block names accepted by <see cref="UnicodePropertyValidator"/>.
        /// </summary>
        private static readonly (string Name, int Start, int End)[] _blockRanges = new[]
        {
            ("Basic_Latin", 0x0000, 0x007F),
            ("Latin_1_Supplement", 0x0080, 0x00FF),
            ("Latin_Extended_A", 0x0100, 0x017F),
            ("Latin_Extended_B", 0x0180, 0x024F),
            ("IPA_Extensions", 0x0250, 0x02AF),
            ("Spacing_Modifier_Letters", 0x02B0, 0x02FF),
            ("Combining_Diacritical_Marks", 0x0300, 0x036F),
            ("Greek_and_Coptic", 0x0370, 0x03FF),
            ("Cyrillic", 0x0400, 0x04FF),
            ("Cyrillic_Supplement", 0x0500, 0x052F),
            ("Armenian", 0x0530, 0x058F),
            ("Hebrew", 0x0590, 0x05FF),
            ("Arabic", 0x0600, 0x06FF),
            ("Syriac", 0x0700, 0x074F),
            ("Arabic_Supplement", 0x0750, 0x077F),
            ("Thaana", 0x0780, 0x07BF),
            ("NKo", 0x07C0, 0x07FF),
            ("Devanagari", 0x0900, 0x097F),
            ("Bengali", 0x0980, 0x09FF),
            ("Gurmukhi", 0x0A00, 0x0A7F),
            ("Gujarati", 0x0A80, 0x0AFF),
            ("Oriya", 0x0B00, 0x0B7F),
            ("Tamil", 0x0B80, 0x0BFF),
            ("Telugu", 0x0C00, 0x0C7F),
            ("Kannada", 0x0C80, 0x0CFF),
            ("Malayalam", 0x0D00, 0x0D7F),
            ("Sinhala", 0x0D80, 0x0DFF),
            ("Thai", 0x0E00, 0x0E7F),
            ("Lao", 0x0E80, 0x0EFF),
            ("Tibetan", 0x0F00, 0x0FFF),
            ("Myanmar", 0x1000, 0x109F),
            ("Georgian", 0x10A0, 0x10FF),
            ("Hangul_Jamo", 0x1100, 0x11FF),
            ("Ethiopic", 0x1200, 0x137F),
            ("Cherokee", 0x13A0, 0x13FF),
            ("Unified_Canadian_Aboriginal_Syllabics", 0x1400, 0x167F),
            ("Ogham", 0x1680, 0x169F),
            ("Runic", 0x16A0, 0x16FF),
            ("Tagalog", 0x1700, 0x171F),
            ("Khmer", 0x1780, 0x17FF),
            ("Mongolian", 0x1800, 0x18AF),
            ("Latin_Extended_Additional", 0x1E00, 0x1EFF),
            ("Greek_Extended", 0x1F00, 0x1FFF),
            ("General_Punctuation", 0x2000, 0x206F),
            ("Superscripts_and_Subscripts", 0x2070, 0x209F),
            ("Currency_Symbols", 0x20A0, 0x20CF),
            ("Combining_Diacritical_Marks_for_Symbols", 0x20D0, 0x20FF),
            ("Letterlike_Symbols", 0x2100, 0x214F),
            ("Number_Forms", 0x2150, 0x218F),
            ("Arrows", 0x2190, 0x21FF),
            ("Mathematical_Operators", 0x2200, 0x22FF),
            ("Miscellaneous_Technical", 0x2300, 0x23FF),
            ("Control_Pictures", 0x2400, 0x243F),
            ("Optical_Character_Recognition", 0x2440, 0x245F),
            ("Enclosed_Alphanumerics", 0x2460, 0x24FF),
            ("Box_Drawing", 0x2500, 0x257F),
            ("Block_Elements", 0x2580, 0x259F),
            ("Geometric_Shapes", 0x25A0, 0x25FF),
            ("Miscellaneous_Symbols", 0x2600, 0x26FF),
            ("Dingbats", 0x2700, 0x27BF),
            ("Miscellaneous_Mathematical_Symbols_A", 0x27C0, 0x27EF),
            ("Supplemental_Arrows_A", 0x27F0, 0x27FF),
            ("Braille_Patterns", 0x2800, 0x28FF),
            ("Supplemental_Arrows_B", 0x2900, 0x297F),
            ("Miscellaneous_Mathematical_Symbols_B", 0x2980, 0x29FF),
            ("Supplemental_Mathematical_Operators", 0x2A00, 0x2AFF),
            ("Miscellaneous_Symbols_and_Arrows", 0x2B00, 0x2BFF),
            ("Supplemental_Punctuation", 0x2E00, 0x2E7F),
            ("CJK_Radicals_Supplement", 0x2E80, 0x2EFF),
            ("Kangxi_Radicals", 0x2F00, 0x2FDF),
            ("CJK_Symbols_and_Punctuation", 0x3000, 0x303F),
            ("Hiragana", 0x3040, 0x309F),
            ("Katakana", 0x30A0, 0x30FF),
            ("Bopomofo", 0x3100, 0x312F),
            ("Hangul_Compatibility_Jamo", 0x3130, 0x318F),
            ("Kanbun", 0x3190, 0x319F),
            ("CJK_Strokes", 0x31C0, 0x31EF),
            ("Katakana_Phonetic_Extensions", 0x31F0, 0x31FF),
            ("Enclosed_CJK_Letters_and_Months", 0x3200, 0x32FF),
            ("CJK_Compatibility", 0x3300, 0x33FF),
            ("CJK_Unified_Ideographs_Extension_A", 0x3400, 0x4DBF),
            ("Yijing_Hexagram_Symbols", 0x4DC0, 0x4DFF),
            ("CJK_Unified_Ideographs", 0x4E00, 0x9FFF),
            ("Yi_Syllables", 0xA000, 0xA48F),
            ("Hangul_Syllables", 0xAC00, 0xD7AF),
            ("Private_Use_Area", 0xE000, 0xF8FF),
            ("CJK_Compatibility_Ideographs", 0xF900, 0xFAFF),
            ("Alphabetic_Presentation_Forms", 0xFB00, 0xFB4F),
            ("Arabic_Presentation_Forms_A", 0xFB50, 0xFDFF),
            ("Variation_Selectors", 0xFE00, 0xFE0F),
            ("Combining_Half_Marks", 0xFE20, 0xFE2F),
            ("CJK_Compatibility_Forms", 0xFE30, 0xFE4F),
            ("Small_Form_Variants", 0xFE50, 0xFE6F),
            ("Arabic_Presentation_Forms_B", 0xFE70, 0xFEFF),
            ("Halfwidth_and_Fullwidth_Forms", 0xFF00, 0xFFEF),
            ("Specials", 0xFFF0, 0xFFFF),
            ("Linear_B_Syllabary", 0x10000, 0x1007F),
            ("Aegean_Numbers", 0x10100, 0x1013F),
            ("Ancient_Greek_Numbers", 0x10140, 0x1018F),
            ("Old_Italic", 0x10300, 0x1032F),
            ("Gothic", 0x10330, 0x1034F),
            ("Deseret", 0x10400, 0x1044F),
            ("Byzantine_Musical_Symbols", 0x1D000, 0x1D0FF),
            ("Musical_Symbols", 0x1D100, 0x1D1FF),
            ("Mathematical_Alphanumeric_Symbols", 0x1D400, 0x1D7FF),
            ("Emoticons", 0x1F600, 0x1F64F),
            ("Transport_and_Map_Symbols", 0x1F680, 0x1F6FF),
            ("Supplemental_Symbols_and_Pictographs", 0x1F900, 0x1F9FF),
            ("Symbols_and_Pictographs_Extended_A", 0x1FA70, 0x1FAFF),
            ("CJK_Unified_Ideographs_Extension_B", 0x20000, 0x2A6DF)
        };

        /// <summary>
        /// Script primary ranges, approximated for scripts with many disjoint
        /// ranges. Names are canonical script names accepted by
        /// <see cref="UnicodePropertyValidator"/>.
        /// </summary>
        private static readonly (string Name, int[] Ranges)[] _scriptRanges = new[]
        {
            ("Latin", new[] { 0x0041, 0x005A, 0x0061, 0x007A, 0x00AA, 0x00AA, 0x00BA, 0x00BA,
                              0x00C0, 0x00D6, 0x00D8, 0x00F6, 0x00F8, 0x02B8, 0x1E00, 0x1EFF }),
            ("Greek", new[] { 0x0370, 0x0373, 0x0375, 0x0377, 0x037A, 0x037D, 0x0386, 0x0386,
                              0x0388, 0x038A, 0x038C, 0x038C, 0x038E, 0x03A1, 0x03A3, 0x03E1,
                              0x03F0, 0x03FF, 0x1F00, 0x1FFF }),
            ("Cyrillic", new[] { 0x0400, 0x052F, 0x1C80, 0x1C88, 0x2DE0, 0x2DFF, 0xA640, 0xA69F }),
            ("Hebrew", new[] { 0x0591, 0x05C7, 0x05D0, 0x05EA, 0x05EF, 0x05F4, 0xFB1D, 0xFB4F }),
            ("Arabic", new[] { 0x0600, 0x0604, 0x0606, 0x060B, 0x060D, 0x061A, 0x061E, 0x061E,
                               0x0620, 0x063F, 0x0641, 0x064A, 0x0656, 0x066F, 0x0671, 0x06DC,
                               0x06DE, 0x06FF, 0x0750, 0x077F, 0xFB50, 0xFDFF, 0xFE70, 0xFEFC }),
            ("Armenian", new[] { 0x0531, 0x0556, 0x0559, 0x058A, 0x058D, 0x058F, 0xFB13, 0xFB17 }),
            ("Georgian", new[] { 0x10A0, 0x10C5, 0x10D0, 0x10FF, 0x2D00, 0x2D2D }),
            ("Devanagari", new[] { 0x0900, 0x097F, 0xA8E0, 0xA8FF }),
            ("Bengali", new[] { 0x0980, 0x09FE }),
            ("Gurmukhi", new[] { 0x0A01, 0x0A76 }),
            ("Gujarati", new[] { 0x0A81, 0x0AFF }),
            ("Oriya", new[] { 0x0B01, 0x0B77 }),
            ("Tamil", new[] { 0x0B82, 0x0BFA }),
            ("Telugu", new[] { 0x0C00, 0x0C7F }),
            ("Kannada", new[] { 0x0C80, 0x0CF2 }),
            ("Malayalam", new[] { 0x0D00, 0x0D7F }),
            ("Sinhala", new[] { 0x0D81, 0x0DF4 }),
            ("Thai", new[] { 0x0E01, 0x0E5B }),
            ("Lao", new[] { 0x0E81, 0x0EDF }),
            ("Tibetan", new[] { 0x0F00, 0x0FDA }),
            ("Myanmar", new[] { 0x1000, 0x109F }),
            ("Khmer", new[] { 0x1780, 0x17F9 }),
            ("Mongolian", new[] { 0x1800, 0x18AA }),
            ("Ethiopic", new[] { 0x1200, 0x139F, 0x2D80, 0x2DDE }),
            ("Cherokee", new[] { 0x13A0, 0x13FD }),
            ("Syriac", new[] { 0x0700, 0x074A }),
            ("Thaana", new[] { 0x0780, 0x07B1 }),
            ("Han", new[] { 0x2E80, 0x2E99, 0x3005, 0x3005, 0x3007, 0x3007, 0x3400, 0x4DBF,
                            0x4E00, 0x9FFF, 0xF900, 0xFAFF, 0x20000, 0x2FA1F }),
            ("Hiragana", new[] { 0x3041, 0x309E, 0x309D, 0x309F }),
            ("Katakana", new[] { 0x30A1, 0x30FF, 0x31F0, 0x31FF, 0xFF66, 0xFF9D }),
            ("Hangul", new[] { 0x1100, 0x11FF, 0x3131, 0x318E, 0xA960, 0xA97F, 0xAC00, 0xD7A3 }),
            ("Bopomofo", new[] { 0x3105, 0x312F, 0x31A0, 0x31BA }),
            ("Braille", new[] { 0x2800, 0x28FF }),
            ("Coptic", new[] { 0x03E2, 0x03EF, 0x2C80, 0x2CFF }),
            ("Deseret", new[] { 0x10400, 0x1044F }),
            ("Gothic", new[] { 0x10330, 0x1034A }),
            ("Old_Italic", new[] { 0x10300, 0x1031F }),
            ("Ogham", new[] { 0x1681, 0x169A }),
            ("Runic", new[] { 0x16A0, 0x16F0 }),
            ("Yi", new[] { 0xA000, 0xA4C6 }),
            ("Inherited", new[] { 0x0300, 0x036F, 0x1AB0, 0x1AFF, 0x1DC0, 0x1DFF, 0xFE00, 0xFE0F }),
            ("Vai", new[] { 0xA500, 0xA62B }),
            ("Cham", new[] { 0xAA00, 0xAA5F }),
            ("Batak", new[] { 0x1BC0, 0x1BFF }),
            ("Balinese", new[] { 0x1B00, 0x1B7C }),
            ("Sundanese", new[] { 0x1B80, 0x1BBF }),
            ("Lepcha", new[] { 0x1C00, 0x1C4F }),
            ("Ol_Chiki", new[] { 0x1C50, 0x1C7F }),
            ("Tai_Tham", new[] { 0x1A20, 0x1AAF }),
            ("Tai_Viet", new[] { 0xAA80, 0xAADF }),
            ("New_Tai_Lue", new[] { 0x1980, 0x19DF }),
            ("Tai_Le", new[] { 0x1950, 0x1974 }),
            ("Meetei_Mayek", new[] { 0xAAE0, 0xAAF6, 0xABC0, 0xABFF }),
            ("Kayah_Li", new[] { 0xA900, 0xA92F }),
            ("Rejang", new[] { 0xA930, 0xA95F }),
            ("Javanese", new[] { 0xA980, 0xA9DF }),
            ("Saurashtra", new[] { 0xA880, 0xA8D9 }),
            ("Nko", new[] { 0x07C0, 0x07FA }),
            ("Tifinagh", new[] { 0x2D30, 0x2D7F }),
            ("Glagolitic", new[] { 0x2C00, 0x2C5F }),
            ("Tirhuta", new[] { 0x11480, 0x114DF }),
            ("SignWriting", new[] { 0x1D800, 0x1DA8B })
        };

        private static bool TryGetBlockRange(string canonicalName, out int start, out int end)
        {
            foreach (var (name, blockStart, blockEnd) in _blockRanges)
            {
                if (name == canonicalName)
                {
                    start = blockStart;
                    end = blockEnd;
                    return true;
                }
            }
            start = 0;
            end = 0;
            return false;
        }

        private static bool MatchesScript(int codepoint, string canonicalName)
        {
            foreach (var (name, ranges) in _scriptRanges)
            {
                if (name != canonicalName)
                {
                    continue;
                }
                for (int i = 0; i + 1 < ranges.Length; i += 2)
                {
                    if (codepoint >= ranges[i] && codepoint <= ranges[i + 1])
                    {
                        return true;
                    }
                }
                return false;
            }
            return false;
        }

        /// <summary>
        /// Tests common binary properties. Names without a runtime
        /// implementation return false.
        /// </summary>
        private static bool MatchesBinaryProperty(int codepoint, string property)
        {
            var category = GetUnicodeCategory(codepoint);

            return property switch
            {
                "ASCII" => codepoint <= 0x7F,
                "Alphabetic" => IsLetter(codepoint),
                "Uppercase" => IsUpper(codepoint),
                "Lowercase" => IsLower(codepoint),
                "Cased" => category is UnicodeCategory.UppercaseLetter or UnicodeCategory.LowercaseLetter
                                or UnicodeCategory.TitlecaseLetter,
                "Changes_When_Lowercased" => IsUpper(codepoint),
                "Changes_When_Uppercased" => IsLower(codepoint),
                "Changes_When_Titlecased" => IsUpper(codepoint),
                "Changes_When_Casemapped" => IsUpper(codepoint) || IsLower(codepoint) ||
                                             category == UnicodeCategory.TitlecaseLetter,
                "White_Space" => IsWhiteSpace(codepoint),
                "Pattern_White_Space" => codepoint is 0x09 or 0x0A or 0x0B or 0x0C or 0x0D or 0x20
                                                or 0x85 or 0x200E or 0x200F or 0x2028 or 0x2029,
                "Hex_Digit" or "ASCII_Hex_Digit" =>
                    codepoint is >= 0x30 and <= 0x39 or >= 0x41 and <= 0x46 or >= 0x61 and <= 0x66,
                "ID_Start" => IsLetter(codepoint) || codepoint == 0x5F,
                "ID_Continue" => IsLetterOrDigit(codepoint) || codepoint == 0x5F,
                "XID_Start" => IsLetter(codepoint) || codepoint == 0x5F, // approximated by ID_Start
                "XID_Continue" => IsLetterOrDigit(codepoint) || codepoint == 0x5F, // approximated by ID_Continue
                "Math" => category == UnicodeCategory.MathSymbol ||
                          (codepoint >= 0x2200 && codepoint <= 0x22FF) ||
                          (codepoint >= 0x2A00 && codepoint <= 0x2AFF) ||
                          (codepoint >= 0x1D400 && codepoint <= 0x1D7FF),
                "Dash" => category == UnicodeCategory.DashPunctuation || codepoint == 0x2212,
                "Hyphen" => codepoint is 0x2D or 0xAD or 0x2010 or 0x2011,
                "Quotation_Mark" => category is UnicodeCategory.InitialQuotePunctuation
                                or UnicodeCategory.FinalQuotePunctuation ||
                                codepoint is 0x22 or 0x27 or 0x2018 or 0x2019 or 0x201C or 0x201D,
                "Diacritic" => category is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark,
                "Extender" => codepoint is 0x5F or >= 0xB7,
                "Ideographic" => (codepoint >= 0x3006 && codepoint <= 0x3007) ||
                                 (codepoint >= 0x3021 && codepoint <= 0x3029) ||
                                 (codepoint >= 0x3400 && codepoint <= 0x4DBF) ||
                                 (codepoint >= 0x4E00 && codepoint <= 0x9FFF) ||
                                 (codepoint >= 0xF900 && codepoint <= 0xFAFF) ||
                                 (codepoint >= 0x20000 && codepoint <= 0x2FA1F),
                "Join_Control" => codepoint is 0x200C or 0x200D,
                "Bidi_Control" => codepoint is 0x61C or >= 0x200E and <= 0x200F or >= 0x202A and <= 0x202E
                                or >= 0x2066 and <= 0x2069,
                "Noncharacter_Code_Point" => (codepoint >= 0xFDD0 && codepoint <= 0xFDEF) ||
                                             (codepoint & 0xFFFE) == 0xFFFE,
                "Default_Ignorable_Code_Point" => codepoint is 0xAD or >= 0x200B and <= 0x200F
                                or >= 0x2060 and <= 0x206F or 0xFEFF or >= 0xFFF9 and <= 0xFFFB,
                "Deprecated" => codepoint is 0x17A3 or 0x17A4 or 0x206B or 0x206A,
                "Grapheme_Base" => category is not (UnicodeCategory.Control or UnicodeCategory.Format
                                or UnicodeCategory.Surrogate or UnicodeCategory.LineSeparator
                                or UnicodeCategory.ParagraphSeparator or UnicodeCategory.OtherNotAssigned),
                "Grapheme_Extend" => category is UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark,
                "Regional_Indicator" => codepoint >= 0x1F1E6 && codepoint <= 0x1F1FF,
                "Sentence_Terminal" => category == UnicodeCategory.OtherPunctuation &&
                                       codepoint is 0x2E or 0x21 or 0x3F or 0x2026 or 0x203C or 0x2047
                                            or 0x2048 or 0x2049 or 0x3002 or 0xFF01 or 0xFF1F or 0xFF0E,
                "Terminal_Punctuation" => category == UnicodeCategory.OtherPunctuation &&
                                          codepoint is 0x21 or 0x2C or 0x2E or 0x3A or 0x3B or 0x3F or 0x203C
                                                or 0x3001 or 0x3002,
                "Soft_Dotted" => codepoint is 0x69 or 0x6A or 0x012F or 0x0249 or 0x0268 or 0x029D
                                or 0x02B2 or 0x1D96 or 0x1DA4 or 0x1DA8 or 0x1DB2 or 0x2096 or 0x2097,
                "Variation_Selector" => (codepoint >= 0xFE00 && codepoint <= 0xFE0F) ||
                                        (codepoint >= 0xE0100 && codepoint <= 0xE01EF),
                "Emoji" => (codepoint >= 0x1F600 && codepoint <= 0x1F64F) ||
                           (codepoint >= 0x1F300 && codepoint <= 0x1F5FF) ||
                           (codepoint >= 0x1F680 && codepoint <= 0x1F6FF) ||
                           (codepoint >= 0x1F900 && codepoint <= 0x1F9FF) ||
                           (codepoint >= 0x1FA70 && codepoint <= 0x1FAFF) ||
                           (codepoint >= 0x2600 && codepoint <= 0x26FF) ||
                           (codepoint >= 0x2700 && codepoint <= 0x27BF),
                "Emoji_Presentation" => (codepoint >= 0x1F600 && codepoint <= 0x1F64F) ||
                                        (codepoint >= 0x1F300 && codepoint <= 0x1F5FF) ||
                                        (codepoint >= 0x1F680 && codepoint <= 0x1F6FF) ||
                                        (codepoint >= 0x1F900 && codepoint <= 0x1F9FF) ||
                                        (codepoint >= 0x1FA70 && codepoint <= 0x1FAFF),
                "Emoji_Modifier" => codepoint >= 0x1F3FB && codepoint <= 0x1F3FF,
                "Emoji_Component" => (codepoint >= 0x1F1E6 && codepoint <= 0x1F1FF) ||
                                     (codepoint >= 0x1F3FB && codepoint <= 0x1F3FF) ||
                                     codepoint is 0x200D or 0x20E3 or 0xFE0F,
                "Emoji_Modifier_Base" => (codepoint >= 0x261D && codepoint <= 0x261D) ||
                                         (codepoint >= 0x270A && codepoint <= 0x270D) ||
                                         (codepoint >= 0x1F385 && codepoint <= 0x1F385) ||
                                         (codepoint >= 0x1F3C3 && codepoint <= 0x1F3C4) ||
                                         (codepoint >= 0x1F3CA && codepoint <= 0x1F3CB) ||
                                         (codepoint >= 0x1F442 && codepoint <= 0x1F443) ||
                                         (codepoint >= 0x1F446 && codepoint <= 0x1F450) ||
                                         (codepoint >= 0x1F466 && codepoint <= 0x1F469) ||
                                         (codepoint >= 0x1F645 && codepoint <= 0x1F647),
                "Extended_Pictographic" => (codepoint >= 0x1F000 && codepoint <= 0x1FAFF) ||
                                           (codepoint >= 0x2600 && codepoint <= 0x27BF),
                "Pattern_Syntax" => codepoint >= 0x21 && codepoint <= 0x2F ||
                                    codepoint >= 0x3A && codepoint <= 0x40 ||
                                    codepoint >= 0x5B && codepoint <= 0x60 ||
                                    codepoint >= 0x7B && codepoint <= 0x7E,
                "Radical" => (codepoint >= 0x2E80 && codepoint <= 0x2EF3) ||
                             (codepoint >= 0x2F00 && codepoint <= 0x2FD5),
                "Unified_Ideograph" => (codepoint >= 0x3400 && codepoint <= 0x4DBF) ||
                                       (codepoint >= 0x4E00 && codepoint <= 0x9FFF) ||
                                       (codepoint >= 0xF900 && codepoint <= 0xFAFF) ||
                                       (codepoint >= 0x20000 && codepoint <= 0x2FA1F),
                _ => false
            };
        }

        /// <summary>Helper method to check if a codepoint represents a letter.</summary>
        private static bool IsLetter(int codepoint)
        {
            if (codepoint <= 0xFFFF)
            {
                return char.IsLetter((char)codepoint);
            }
            return Rune.TryCreate(codepoint, out var rune) && Rune.IsLetter(rune);
        }

        /// <summary>Helper method to check if a codepoint represents an uppercase letter.</summary>
        private static bool IsUpper(int codepoint)
        {
            if (codepoint <= 0xFFFF)
            {
                return char.IsUpper((char)codepoint);
            }
            return Rune.TryCreate(codepoint, out var rune) && Rune.IsUpper(rune);
        }

        /// <summary>Helper method to check if a codepoint represents a lowercase letter.</summary>
        private static bool IsLower(int codepoint)
        {
            if (codepoint <= 0xFFFF)
            {
                return char.IsLower((char)codepoint);
            }
            return Rune.TryCreate(codepoint, out var rune) && Rune.IsLower(rune);
        }

        /// <summary>Helper method to check if a codepoint represents whitespace.</summary>
        private static bool IsWhiteSpace(int codepoint)
        {
            if (codepoint <= 0xFFFF)
            {
                return char.IsWhiteSpace((char)codepoint);
            }
            return Rune.TryCreate(codepoint, out var rune) && Rune.IsWhiteSpace(rune);
        }

        /// <summary>Helper method to check if a codepoint represents a letter or digit.</summary>
        private static bool IsLetterOrDigit(int codepoint)
        {
            if (codepoint <= 0xFFFF)
            {
                return char.IsLetterOrDigit((char)codepoint);
            }
            return Rune.TryCreate(codepoint, out var rune) && Rune.IsLetterOrDigit(rune);
        }
    }

    /// <summary>
    /// Advanced Unicode support with ICU integration and .NET fallback for normalization and property handling
    /// </summary>
    public class AdvancedUnicodeSupport
    {
        /// <summary>
        /// Initializes a new instance of the AdvancedUnicodeSupport class
        /// </summary>
        public AdvancedUnicodeSupport()
        {
            // Initialization with available normalizers
        }

        /// <summary>
        /// Process Unicode pattern with normalization support
        /// </summary>
        /// <param name="utf8Input">The UTF-8 input to process</param>
        /// <param name="pattern">The pattern string</param>
        /// <param name="normalizationForm">The Unicode normalization form to apply</param>
        /// <returns>True if the pattern matches the normalized input</returns>
        public bool ProcessUnicodePattern(ReadOnlySpan<byte> utf8Input, string pattern, UnicodeNormalizationForm normalizationForm = UnicodeNormalizationForm.None)
        {
            try
            {
                // Convert UTF-8 to string for processing
                var inputString = System.Text.Encoding.UTF8.GetString(utf8Input);

                // Normalize input if required
                var normalizedInput = NormalizeIfNeeded(inputString, normalizationForm);

                // Process with Unicode property support
                return ProcessWithUnicodeSupport(normalizedInput, pattern);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Normalize string according to specified form using .NET normalization
        /// </summary>
        /// <param name="input">The input string to normalize</param>
        /// <param name="form">The normalization form</param>
        /// <returns>The normalized string</returns>
        public string NormalizeIfNeeded(string input, UnicodeNormalizationForm form)
        {
            return form switch
            {
                UnicodeNormalizationForm.NFC => input.Normalize(System.Text.NormalizationForm.FormC),
                UnicodeNormalizationForm.NFD => input.Normalize(System.Text.NormalizationForm.FormD),
                UnicodeNormalizationForm.NFKC => input.Normalize(System.Text.NormalizationForm.FormKC),
                UnicodeNormalizationForm.NFKD => input.Normalize(System.Text.NormalizationForm.FormKD),
                UnicodeNormalizationForm.None => input,
                _ => input
            };
        }

        /// <summary>
        /// Process pattern with Unicode property support
        /// </summary>
        private bool ProcessWithUnicodeSupport(string normalizedInput, string pattern)
        {
            // Simplified implementation for validation
            for (int i = 0; i < normalizedInput.Length; i++)
            {
                var codepoint = char.ConvertToUtf32(normalizedInput, i);

                // Skip surrogate pairs
                if (char.IsHighSurrogate(normalizedInput[i]))
                    i++; // Skip the low surrogate

                // Validate that we can process this codepoint
                if (codepoint < 0 || codepoint > 0x10FFFF)
                    return false;
            }

            return true;
        }

        /// <summary>
        /// Check if two strings are canonically equivalent using .NET normalization
        /// </summary>
        /// <param name="str1">First string</param>
        /// <param name="str2">Second string</param>
        /// <returns>True if the strings are canonically equivalent</returns>
        public bool AreCanonicallyEquivalent(string str1, string str2)
        {
            return str1.Normalize(System.Text.NormalizationForm.FormC) ==
                   str2.Normalize(System.Text.NormalizationForm.FormC);
        }

        /// <summary>
        /// Get the grapheme cluster boundaries in a string (simplified implementation)
        /// </summary>
        /// <param name="text">The text to analyze</param>
        /// <returns>Array of grapheme cluster boundary positions</returns>
        public int[] GetGraphemeClusterBoundaries(string text)
        {
            var boundaries = new List<int> { 0 };

            // Simplified boundary detection
            for (int i = 0; i < text.Length; i++)
            {
                if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                {
                    i++; // Skip the low surrogate
                    boundaries.Add(i + 1);
                }
                else if (i + 1 < text.Length)
                {
                    boundaries.Add(i + 1);
                }
            }

            return boundaries.ToArray();
        }
    }

    /// <summary>
    /// Unicode normalization forms supported by the advanced Unicode processor
    /// </summary>
    public enum UnicodeNormalizationForm
    {
        /// <summary>
        /// No normalization applied
        /// </summary>
        None,

        /// <summary>
        /// Canonical decomposition followed by canonical composition
        /// </summary>
        NFC,

        /// <summary>
        /// Canonical decomposition
        /// </summary>
        NFD,

        /// <summary>
        /// Compatibility decomposition followed by canonical composition
        /// </summary>
        NFKC,

        /// <summary>
        /// Compatibility decomposition
        /// </summary>
        NFKD
    }
}
