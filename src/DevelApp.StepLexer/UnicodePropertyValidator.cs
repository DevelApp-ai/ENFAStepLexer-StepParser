using System;
using System.Collections.Generic;

namespace DevelApp.StepLexer
{
    /// <summary>
    /// The kind of Unicode property that a property name refers to.
    /// </summary>
    public enum UnicodePropertyKind
    {
        /// <summary>The name does not match any known Unicode property.</summary>
        Unknown,

        /// <summary>A Unicode general category (e.g. "L", "Nd", "Lowercase_Letter").</summary>
        GeneralCategory,

        /// <summary>A Unicode binary property (e.g. "Alphabetic", "White_Space", "Emoji").</summary>
        BinaryProperty,

        /// <summary>A Unicode script name (e.g. "Latin", "Greek", "Han").</summary>
        Script,

        /// <summary>A Unicode block name (e.g. "Basic_Latin", "Greek_and_Coptic").</summary>
        Block
    }

    /// <summary>
    /// Validates and normalizes Unicode property names used in
    /// <c>\p{...}</c> and <c>\P{...}</c> pattern constructs. Names are matched
    /// case-insensitively and ignore underscores, spaces and hyphens, following
    /// the loose matching rules recommended for regular expressions (UTS #18).
    /// Block names additionally accept the <c>Is</c> and <c>In</c> prefixes
    /// used by .NET and Perl-style property syntax.
    /// </summary>
    public static class UnicodePropertyValidator
    {
        /// <summary>
        /// Checks whether the specified name is a known Unicode property name.
        /// </summary>
        /// <param name="name">The property name to validate (already stripped of any <c>\p{</c>/<c>\P{</c> delimiters).</param>
        /// <returns><c>true</c> when the name identifies a general category, binary property, script or block; otherwise <c>false</c>.</returns>
        public static bool IsValidPropertyName(string? name)
        {
            return NormalizePropertyName(name) != null;
        }

        /// <summary>
        /// Determines which kind of Unicode property the given name refers to.
        /// </summary>
        /// <param name="name">The property name to classify.</param>
        /// <returns>The <see cref="UnicodePropertyKind"/> for the name, or <see cref="UnicodePropertyKind.Unknown"/> for unrecognized names.</returns>
        public static UnicodePropertyKind GetPropertyKind(string? name)
        {
            var normalized = NormalizePropertyName(name);
            if (normalized == null)
            {
                return UnicodePropertyKind.Unknown;
            }
            if (_generalCategories.Contains(normalized))
            {
                return UnicodePropertyKind.GeneralCategory;
            }
            if (_binaryProperties.Contains(normalized))
            {
                return UnicodePropertyKind.BinaryProperty;
            }
            if (_scripts.Contains(normalized))
            {
                return UnicodePropertyKind.Script;
            }
            return UnicodePropertyKind.Block;
        }

        /// <summary>
        /// Normalizes a Unicode property name to its canonical form.
        /// General categories normalize to their two-letter codes
        /// (e.g. <c>Lowercase_Letter</c> and <c>lowercaseletter</c> both
        /// become <c>Ll</c>); block names lose any <c>Is</c>/<c>In</c> prefix;
        /// other names normalize to their canonical spelling.
        /// </summary>
        /// <param name="name">The property name to normalize.</param>
        /// <returns>The canonical property name, or <c>null</c> when the name is not recognized.</returns>
        public static string? NormalizePropertyName(string? name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return null;
            }

            var key = BuildLookupKey(name);

            if (_lookup.TryGetValue(key, out var canonical))
            {
                return canonical;
            }

            // .NET and Perl-style block syntax allows IsGreek / InGreek_and_Coptic
            if ((key.StartsWith("is") || key.StartsWith("in")) && key.Length > 2)
            {
                if (_lookup.TryGetValue(key[2..], out var prefixed))
                {
                    return prefixed;
                }
            }

            return null;
        }

        /// <summary>
        /// Gets the recognized general category codes, in canonical short form.
        /// </summary>
        public static IReadOnlyCollection<string> GeneralCategories => _generalCategories;

        /// <summary>
        /// Gets the recognized binary property names, in canonical form.
        /// </summary>
        public static IReadOnlyCollection<string> BinaryProperties => _binaryProperties;

        /// <summary>
        /// Gets the recognized script names, in canonical form.
        /// </summary>
        public static IReadOnlyCollection<string> Scripts => _scripts;

        /// <summary>
        /// Gets the recognized block names, in canonical form.
        /// </summary>
        public static IReadOnlyCollection<string> Blocks => _blocks;

        /// <summary>
        /// Builds the loose-matching lookup key for a name: lowercased with
        /// underscores, spaces and hyphens removed.
        /// </summary>
        private static string BuildLookupKey(string name)
        {
            var builder = new System.Text.StringBuilder(name.Length);
            foreach (var c in name.Trim())
            {
                if (c == '_' || c == '-' || c == ' ')
                {
                    continue;
                }
                builder.Append(char.ToLowerInvariant(c));
            }
            return builder.ToString();
        }

        private static readonly HashSet<string> _generalCategories = new(StringComparer.Ordinal)
        {
            // Letters
            "L", "LC", "Ll", "Lm", "Lo", "Lt", "Lu",
            // Marks
            "M", "Mc", "Me", "Mn",
            // Numbers
            "N", "Nd", "Nl", "No",
            // Punctuation
            "P", "Pc", "Pd", "Pe", "Pf", "Pi", "Po", "Ps",
            // Symbols
            "S", "Sc", "Sk", "Sm", "So",
            // Separators
            "Z", "Zl", "Zp", "Zs",
            // Other
            "C", "Cc", "Cf", "Cn", "Co", "Cs"
        };

        private static readonly HashSet<string> _binaryProperties = new(StringComparer.Ordinal)
        {
            "Alphabetic", "ASCII", "ASCII_Hex_Digit", "Bidi_Control", "Bidi_Mirrored",
            "Case_Ignorable", "Cased",
            "Changes_When_Casefolded", "Changes_When_Casemapped", "Changes_When_Lowercased",
            "Changes_When_Titlecased", "Changes_When_Uppercased",
            "Dash", "Default_Ignorable_Code_Point", "Deprecated", "Diacritic",
            "Emoji", "Emoji_Component", "Emoji_Modifier", "Emoji_Modifier_Base", "Emoji_Presentation",
            "Extended_Pictographic", "Extender",
            "Grapheme_Base", "Grapheme_Extend",
            "Hex_Digit", "Hyphen",
            "ID_Continue", "ID_Start", "Ideographic", "IDS_Binary_Operator", "IDS_Trinary_Operator",
            "Join_Control", "Logical_Order_Exception", "Lowercase", "Math",
            "Noncharacter_Code_Point", "Pattern_Syntax", "Pattern_White_Space",
            "Quotation_Mark", "Radical", "Regional_Indicator", "Sentence_Terminal",
            "Soft_Dotted", "Terminal_Punctuation", "Unified_Ideograph", "Uppercase",
            "Variation_Selector", "White_Space", "XID_Continue", "XID_Start"
        };

        private static readonly HashSet<string> _scripts = new(StringComparer.Ordinal)
        {
            "Adlam", "Ahom", "Anatolian_Hieroglyphs", "Arabic", "Armenian", "Avestan",
            "Balinese", "Bamum", "Bassa_Vah", "Batak", "Bengali", "Bhaiksuki",
            "Bopomofo", "Brahmi", "Braille", "Buginese", "Buhid",
            "Canadian_Aboriginal", "Carian", "Caucasian_Albanian", "Chakma",
            "Cham", "Cherokee", "Chorasmian", "Common", "Coptic", "Cuneiform",
            "Cypriot", "Cypro_Minoan", "Cyrillic",
            "Deseret", "Devanagari", "Dives_Akuru", "Dogra", "Duployan",
            "Egyptian_Hieroglyphs", "Elbasan", "Elymaic", "Ethiopic",
            "Georgian", "Glagolitic", "Gothic", "Grantha", "Greek", "Gujarati",
            "Gunjala_Gondi", "Gurmukhi",
            "Han", "Hangul", "Hanifi_Rohingya", "Hanunoo", "Hatran", "Hebrew",
            "Hiragana", "Imperial_Aramaic", "Inherited", "Inscriptional_Pahlavi",
            "Inscriptional_Parthian", "Javanese", "Kaithi", "Kannada",
            "Katakana", "Kawi", "Kayah_Li", "Kharoshthi", "Khitan_Small_Script",
            "Khmer", "Khojki", "Khudawadi", "Lao", "Latin", "Lepcha", "Limbu",
            "Linear_A", "Linear_B", "Lisu", "Lycian", "Lydian",
            "Mahajani", "Makasar", "Malayalam", "Mandaic", "Manichaean",
            "Marchen", "Masaram_Gondi", "Medefaidrin", "Meetei_Mayek",
            "Mende_Kikakui", "Meroitic_Cursive", "Meroitic_Hieroglyphs", "Miao",
            "Modi", "Mongolian", "Mro", "Multani", "Myanmar", "Nabataean",
            "Nag_Mundari", "Nandinagari", "New_Tai_Lue", "Newa", "Nko",
            "Nushu", "Nyiakeng_Puachue_Hmong", "Ogham", "Ol_Chiki",
            "Old_Hungarian", "Old_Italic", "Old_North_Arabian", "Old_Permic",
            "Old_Persian", "Old_Sogdian", "Old_South_Arabian", "Old_Turkic",
            "Old_Uyghur", "Oriya", "Osage", "Osmanya", "Pahawh_Hmong",
            "Palmyrene", "Pau_Cin_Hau", "Phags_Pa", "Phoenician", "Psalter_Pahlavi",
            "Rejang", "Runic", "Samaritan", "Saurashtra", "Sharada", "Shavian",
            "Siddham", "SignWriting", "Sinhala", "Sogdian", "Sora_Sompeng",
            "Soyombo", "Sundanese", "Syloti_Nagri", "Syriac",
            "Tagalog", "Tagbanwa", "Tai_Le", "Tai_Tham", "Tai_Viet", "Takri",
            "Tamil", "Tangsa", "Tangut", "Telugu", "Thaana", "Thai", "Tibetan",
            "Tifinagh", "Tirhuta", "Toto", "Ugaritic", "Unknown", "Vai",
            "Vithkuqi", "Wancho", "Warang_Citi", "Yezidi", "Yi", "Zanabazar_Square"
        };

        private static readonly HashSet<string> _blocks = new(StringComparer.Ordinal)
        {
            "Basic_Latin", "Latin_1_Supplement", "Latin_Extended_A", "Latin_Extended_B",
            "IPA_Extensions", "Spacing_Modifier_Letters", "Combining_Diacritical_Marks",
            "Greek_and_Coptic", "Cyrillic", "Cyrillic_Supplement", "Armenian", "Hebrew",
            "Arabic", "Syriac", "Arabic_Supplement", "Thaana", "NKo", "Samaritan",
            "Mandaic", "Devanagari", "Bengali", "Gurmukhi", "Gujarati", "Oriya",
            "Tamil", "Telugu", "Kannada", "Malayalam", "Sinhala", "Thai", "Lao",
            "Tibetan", "Myanmar", "Georgian", "Hangul_Jamo", "Ethiopic",
            "Cherokee", "Unified_Canadian_Aboriginal_Syllabics", "Ogham", "Runic",
            "Tagalog", "Hanunoo", "Buhid", "Tagbanwa", "Khmer", "Mongolian",
            "Unified_Canadian_Aboriginal_Syllabics_Extended", "Limbu", "Tai_Le",
            "New_Tai_Lue", "Khmer_Symbols", "Buginese", "Tai_Tham", "Balinese",
            "Sundanese", "Batak", "Lepcha", "Ol_Chiki", "Cyrillic_Extended_C",
            "Georgian_Extended", "Sundanese_Supplement", "Vedic_Extensions",
            "Phonetic_Extensions", "Phonetic_Extensions_Supplement",
            "Combining_Diacritical_Marks_Supplement", "Latin_Extended_Additional",
            "Greek_Extended", "General_Punctuation", "Superscripts_and_Subscripts",
            "Currency_Symbols", "Combining_Diacritical_Marks_for_Symbols",
            "Letterlike_Symbols", "Number_Forms", "Arrows", "Mathematical_Operators",
            "Miscellaneous_Technical", "Control_Pictures", "Optical_Character_Recognition",
            "Enclosed_Alphanumerics", "Box_Drawing", "Block_Elements",
            "Geometric_Shapes", "Miscellaneous_Symbols", "Dingbats",
            "Miscellaneous_Mathematical_Symbols_A", "Supplemental_Arrows_A",
            "Braille_Patterns", "Supplemental_Arrows_B",
            "Miscellaneous_Mathematical_Symbols_B",
            "Supplemental_Mathematical_Operators",
            "Miscellaneous_Symbols_and_Arrows", "Glagolitic", "Latin_Extended_C",
            "Coptic", "Georgian_Supplement", "Tifinagh", "Ethiopic_Extended",
            "Cyrillic_Extended_A", "Supplemental_Punctuation", "CJK_Radicals_Supplement",
            "Kangxi_Radicals", "Ideographic_Description_Characters",
            "CJK_Symbols_and_Punctuation", "Hiragana", "Katakana",
            "Bopomofo", "Hangul_Compatibility_Jamo", "Kanbun",
            "Bopomofo_Extended", "CJK_Strokes", "Katakana_Phonetic_Extensions",
            "Enclosed_CJK_Letters_and_Months", "CJK_Compatibility",
            "CJK_Unified_Ideographs_Extension_A", "Yijing_Hexagram_Symbols",
            "CJK_Unified_Ideographs", "Yi_Syllables", "Yi_Radicals", "Lisu",
            "Vai", "Cyrillic_Extended_B", "Bamum", "Modifier_Tone_Letters",
            "Latin_Extended_D", "Syloti_Nagri", "Common_Indic_Number_Forms",
            "Phags_pa", "Saurashtra", "Devanagari_Extended", "Kayah_Li",
            "Rejang", "Hangul_Jamo_Extended_A", "Javanese", "Myanmar_Extended_B",
            "Cham", "Myanmar_Extended_A", "Tai_Viet", "Meetei_Mayek_Extensions",
            "Ethiopic_Extended_A", "Latin_Extended_E", "Cherokee_Supplement",
            "Meetei_Mayek", "Hangul_Syllables", "Hangul_Jamo_Extended_B",
            "Private_Use_Area",
            "CJK_Compatibility_Ideographs", "Alphabetic_Presentation_Forms",
            "Arabic_Presentation_Forms_A", "Variation_Selectors",
            "Vertical_Forms", "Combining_Half_Marks", "CJK_Compatibility_Forms",
            "Small_Form_Variants", "Arabic_Presentation_Forms_B",
            "Halfwidth_and_Fullwidth_Forms", "Specials",
            "Linear_B_Syllabary", "Linear_B_Ideograms", "Aegean_Numbers",
            "Ancient_Greek_Numbers", "Ancient_Symbols", "Phaistos_Disc",
            "Lycian", "Carian", "Coptic_Epact_Numbers", "Old_Italic", "Gothic",
            "Old_Permic", "Ugaritic", "Old_Persian", "Deseret", "Shavian",
            "Osmanya", "Osage", "Elbasan", "Caucasian_Albanian", "Vithkuqi",
            "Todhri", "Linear_A", "Latin_Extended_F", "Coptic_Epact_Numbers",
            "Pahawh_Hmong", "Old_Hungarian", "Medical_Symbols",
            "Combining_Diacritical_Marks_Extended", "Palmyrene", "Nabataean",
            "Hatran", "Old_North_Arabian", "Manichaean", "Psalter_Pahlavi",
            "Mahajani", "Khojki", "Khudawadi", "Gunjala_Gondi",
            "Masaram_Gondi", "Cuneiform", "Cuneiform_Numbers_and_Punctuation",
            "Early_Dynastic_Cuneiform", "Egyptian_Hieroglyphs",
            "Egyptian_Hieroglyph_Format_Controls", "Anatolian_Hieroglyphs",
            "Bamum_Supplement", "Mro", "Bassa_Vah", "Pahawh_Hmong",
            "Medefaidrin", "Miao", "Ideographic_Symbols_and_Punctuation",
            "Tangut", "Tangut_Components", "Khitan_Small_Script",
            "Tangut_Supplement", "Kana_Supplement", "Kana_Extended_A",
            "Small_Kana_Extension", "Nushu", "Duployan", "Znamenny_Musical_Notation",
            "Byzantine_Musical_Symbols", "Musical_Symbols",
            "Ancient_Greek_Musical_Notation", "Kaktovik_Numerals",
            "Mayan_Numerals", "Tai_Xuan_Jing_Symbols", "Counting_Rod_Numerals",
            "Mathematical_Alphanumeric_Symbols", "Sutton_SignWriting",
            "Glagolitic_Supplement", "Nyiakeng_Puachue_Hmong", "Wancho",
            "Nag_Mundari", "Ethiopic_Extended_B", "Mende_Kikakui",
            "Arabic_Extended_B", "Arabic_Extended_A", "Vedic_Extensions",
            "Zanabazar_Square", "Soyombo", "Unified_Canadian_Aboriginal_Syllabics_A_Extended",
            "Symbols_for_Legacy_Computing", "Symbols_for_Pictographic_Languages",
            "Emoticons", "Ornamental_Dingbats", "Transport_and_Map_Symbols",
            "Alchemical_Symbols", "Geometric_Shapes_Extended",
            "Supplemental_Arrows_C", "Supplemental_Symbols_and_Pictographs",
            "Chess_Symbols", "Symbols_and_Pictographs_Extended_A",
            "CJK_Unified_Ideographs_Extension_B", "CJK_Unified_Ideographs_Extension_III",
            "CJK_Compatibility_Ideographs_Supplement", "Tags", "Variation_Selectors_Supplement",
            "Supplementary_Private_Use_Area_A", "Supplementary_Private_Use_Area_B"
        };

        /// <summary>
        /// Loose-matching lookup from normalized key to canonical property name,
        /// covering general categories (short and long names), binary
        /// properties, scripts and blocks.
        /// </summary>
        private static readonly Dictionary<string, string> _lookup = BuildLookup();

        private static Dictionary<string, string> BuildLookup()
        {
            var lookup = new Dictionary<string, string>(1024, StringComparer.Ordinal);

            void Add(string canonical)
            {
                lookup[BuildLookupKey(canonical)] = canonical;
            }

            foreach (var category in _generalCategories)
            {
                Add(category);
            }
            foreach (var binary in _binaryProperties)
            {
                Add(binary);
            }
            foreach (var script in _scripts)
            {
                Add(script);
            }
            foreach (var block in _blocks)
            {
                Add(block);
            }

            // Long (descriptive) names for general categories, normalizing to
            // their short codes.
            AddCategoryLongNames(lookup);

            return lookup;
        }

        private static void AddCategoryLongNames(Dictionary<string, string> lookup)
        {
            var longNames = new (string LongName, string ShortName)[]
            {
                ("Letter", "L"), ("Cased_Letter", "LC"), ("Lowercase_Letter", "Ll"),
                ("Modifier_Letter", "Lm"), ("Other_Letter", "Lo"),
                ("Titlecase_Letter", "Lt"), ("Uppercase_Letter", "Lu"),
                ("Mark", "M"), ("Spacing_Mark", "Mc"), ("Enclosing_Mark", "Me"),
                ("Nonspacing_Mark", "Mn"),
                ("Number", "N"), ("Decimal_Number", "Nd"), ("Letter_Number", "Nl"),
                ("Other_Number", "No"),
                ("Punctuation", "P"), ("Connector_Punctuation", "Pc"),
                ("Dash_Punctuation", "Pd"), ("Close_Punctuation", "Pe"),
                ("Final_Punctuation", "Pf"), ("Initial_Punctuation", "Pi"),
                ("Other_Punctuation", "Po"), ("Open_Punctuation", "Ps"),
                ("Symbol", "S"), ("Currency_Symbol", "Sc"), ("Modifier_Symbol", "Sk"),
                ("Math_Symbol", "Sm"), ("Other_Symbol", "So"),
                ("Separator", "Z"), ("Line_Separator", "Zl"),
                ("Paragraph_Separator", "Zp"), ("Space_Separator", "Zs"),
                ("Other", "C"), ("Control", "Cc"), ("Format", "Cf"),
                ("Unassigned", "Cn"), ("Private_Use", "Co"), ("Surrogate", "Cs")
            };

            foreach (var (longName, shortName) in longNames)
            {
                lookup[BuildLookupKey(longName)] = shortName;
            }
        }
    }
}
