using System.Text;
using DevelApp.StepLexer;

namespace DevelApp.StepLexer.Tests
{
    /// <summary>
    /// Tests for <see cref="UnicodePropertyValidator"/>: valid and invalid
    /// property names, loose matching (case-insensitivity, separators,
    /// Is/In block prefixes) and property kind classification.
    /// </summary>
    public class UnicodePropertyValidatorTests
    {
        [Theory]
        [InlineData("L", true)]
        [InlineData("Ll", true)]
        [InlineData("Lowercase_Letter", true)]
        [InlineData("lowercaseletter", true)]
        [InlineData("lower-case-letter", true)]
        [InlineData("Nd", true)]
        [InlineData("Decimal_Number", true)]
        [InlineData("Latin", true)]
        [InlineData("Greek", true)]
        [InlineData("Cyrillic", true)]
        [InlineData("Basic_Latin", true)]
        [InlineData("IsBasic_Latin", true)]
        [InlineData("InBasic_Latin", true)]
        [InlineData("greek and coptic", true)]
        [InlineData("Emoji", true)]
        [InlineData("White_Space", true)]
        [InlineData("whitespace", true)]
        [InlineData("ID_Start", true)]
        public void IsValidPropertyName_AcceptsKnownNames(string name, bool expected)
        {
            Assert.Equal(expected, UnicodePropertyValidator.IsValidPropertyName(name));
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("InvalidProperty")]
        [InlineData("NotARealThing")]
        [InlineData("ZZ")]
        [InlineData("Lowercase Letterz")]
        public void IsValidPropertyName_RejectsUnknownNames(string name)
        {
            Assert.False(UnicodePropertyValidator.IsValidPropertyName(name));
        }

        [Fact]
        public void IsValidPropertyName_RejectsNull()
        {
            Assert.False(UnicodePropertyValidator.IsValidPropertyName(null));
        }

        [Theory]
        [InlineData("Ll", "Ll")]
        [InlineData("Lowercase_Letter", "Ll")]
        [InlineData("lowercaseletter", "Ll")]
        [InlineData("Decimal_Number", "Nd")]
        [InlineData("Nd", "Nd")]
        [InlineData("latin", "Latin")]
        [InlineData("greek", "Greek")]
        [InlineData("Basic_Latin", "Basic_Latin")]
        [InlineData("IsBasic_Latin", "Basic_Latin")]
        [InlineData("InBasic_Latin", "Basic_Latin")]
        [InlineData("basic latin", "Basic_Latin")]
        [InlineData("white_space", "White_Space")]
        public void NormalizePropertyName_CanonicalizesLooseNames(string name, string expected)
        {
            Assert.Equal(expected, UnicodePropertyValidator.NormalizePropertyName(name));
        }

        [Fact]
        public void NormalizePropertyName_ReturnsNullForUnknownNames()
        {
            Assert.Null(UnicodePropertyValidator.NormalizePropertyName("NoSuchProperty"));
            Assert.Null(UnicodePropertyValidator.NormalizePropertyName(""));
        }

        [Theory]
        [InlineData("L", UnicodePropertyKind.GeneralCategory)]
        [InlineData("Ll", UnicodePropertyKind.GeneralCategory)]
        [InlineData("Lowercase_Letter", UnicodePropertyKind.GeneralCategory)]
        [InlineData("Nd", UnicodePropertyKind.GeneralCategory)]
        [InlineData("Emoji", UnicodePropertyKind.BinaryProperty)]
        [InlineData("White_Space", UnicodePropertyKind.BinaryProperty)]
        [InlineData("Latin", UnicodePropertyKind.Script)]
        [InlineData("Greek", UnicodePropertyKind.Script)]
        [InlineData("Basic_Latin", UnicodePropertyKind.Block)]
        [InlineData("IsBasic_Latin", UnicodePropertyKind.Block)]
        [InlineData("Greek_and_Coptic", UnicodePropertyKind.Block)]
        [InlineData("NoSuchProperty", UnicodePropertyKind.Unknown)]
        public void GetPropertyKind_ClassifiesProperties(string name, UnicodePropertyKind expected)
        {
            Assert.Equal(expected, UnicodePropertyValidator.GetPropertyKind(name));
        }

        [Fact]
        public void NameSets_AreNonEmptyAndContainCanonicalEntries()
        {
            Assert.Contains("Ll", UnicodePropertyValidator.GeneralCategories);
            Assert.Contains("Emoji", UnicodePropertyValidator.BinaryProperties);
            Assert.Contains("Greek", UnicodePropertyValidator.Scripts);
            Assert.Contains("Basic_Latin", UnicodePropertyValidator.Blocks);
            Assert.NotEmpty(UnicodePropertyValidator.GeneralCategories);
            Assert.NotEmpty(UnicodePropertyValidator.BinaryProperties);
            Assert.NotEmpty(UnicodePropertyValidator.Scripts);
            Assert.NotEmpty(UnicodePropertyValidator.Blocks);
        }
    }

    /// <summary>
    /// Runtime tests for <see cref="UnicodePropertyMatcher"/>: loose name
    /// handling, the corrected "C" (Other) general category, supplementary
    /// plane code points, scripts and blocks.
    /// </summary>
    public class UnicodePropertyMatcherRuntimeTests
    {
        [Theory]
        [InlineData('a', "lowercase_letter", true)]
        [InlineData('a', "Lowercase_Letter", true)]
        [InlineData('1', "decimal_number", true)]
        [InlineData('a', "basic_latin", true)]
        [InlineData('a', "IsBasic_Latin", true)]
        [InlineData('\u03B1', "greek", true)]
        [InlineData('a', "greek", false)]
        public void MatchesProperty_AcceptsLooseNames(int codepoint, string property, bool expected)
        {
            Assert.Equal(expected, UnicodePropertyMatcher.MatchesProperty(codepoint, property));
        }

        [Fact]
        public void MatchesProperty_UnknownName_NeverMatches()
        {
            Assert.False(UnicodePropertyMatcher.MatchesProperty('a', "NoSuchProperty"));
            Assert.False(UnicodePropertyMatcher.MatchesProperty(0x1F600, "NoSuchProperty"));
        }

        [Theory]
        [InlineData('a', false)]   // Letter, not Other
        [InlineData('$', false)]   // Currency symbol, not Other
        [InlineData('.', false)]   // Punctuation, not Other
        [InlineData(' ', false)]   // Separator, not Other
        [InlineData('\u0000', true)]  // Control -> Cc -> C
        [InlineData('\u00AD', true)]  // Format -> Cf -> C
        public void MatchesProperty_OtherCategory_DoesNotMatchLettersSymbolsOrPunctuation(int codepoint, bool expected)
        {
            // Regression test: "C" previously also matched punctuation and symbols
            Assert.Equal(expected, UnicodePropertyMatcher.MatchesProperty(codepoint, "C"));
        }

        [Theory]
        [InlineData(0x1F600, "So", true)]        // Emoji face is Other Symbol
        [InlineData(0x1F600, "S", true)]
        [InlineData(0x1F600, "L", false)]
        [InlineData(0x1F600, "Emoji", true)]
        [InlineData(0x1F600, "Emoticons", true)]
        [InlineData(0x1D400, "L", true)]        // Math bold capital A is a letter
        [InlineData(0x1D400, "Lu", true)]
        [InlineData(0x1D400, "Basic_Latin", false)]
        [InlineData(0x20000, "Lo", true)]       // CJK extension B ideograph
        [InlineData(0x10400, "L", true)]        // Deseret capital letter
        [InlineData(0x10400, "Lu", true)]
        public void MatchesProperty_SupplementaryPlanes_EvaluatedWithFullCodePoints(int codepoint, string property, bool expected)
        {
            Assert.Equal(expected, UnicodePropertyMatcher.MatchesProperty(codepoint, property));
        }

        [Theory]
        [InlineData('\u03B1', "Greek", true)]
        [InlineData('\u03A9', "Greek", true)]
        [InlineData('a', "Greek", false)]
        [InlineData('\u0410', "Cyrillic", true)]
        [InlineData('\u05D0', "Hebrew", true)]
        [InlineData('a', "Latin", true)]
        [InlineData('\u03B1', "Latin", false)]
        public void MatchesProperty_Scripts(int codepoint, string script, bool expected)
        {
            Assert.Equal(expected, UnicodePropertyMatcher.MatchesProperty(codepoint, script));
        }

        [Theory]
        [InlineData('a', "Basic_Latin", true)]
        [InlineData('\u00E9', "Latin_1_Supplement", true)]
        [InlineData('\u03B1', "Greek_and_Coptic", true)]
        [InlineData('\u03B1', "Basic_Latin", false)]
        [InlineData(0x1F600, "Emoticons", true)]
        [InlineData(0x1F600, "Transport_and_Map_Symbols", false)]
        [InlineData(0x4E00, "CJK_Unified_Ideographs", true)]
        public void MatchesProperty_Blocks(int codepoint, string block, bool expected)
        {
            Assert.Equal(expected, UnicodePropertyMatcher.MatchesProperty(codepoint, block));
        }

        [Theory]
        [InlineData(0xD800, "Cs", true)]
        [InlineData(0xD800, "C", true)]
        [InlineData(0xD800, "L", false)]
        public void MatchesProperty_Surrogates(int codepoint, string property, bool expected)
        {
            Assert.Equal(expected, UnicodePropertyMatcher.MatchesProperty(codepoint, property));
        }

        [Fact]
        public void MatchesProperty_RejectsOutOfRangeCodePoints()
        {
            Assert.False(UnicodePropertyMatcher.MatchesProperty(-1, "L"));
            Assert.False(UnicodePropertyMatcher.MatchesProperty(0x110000, "L"));
        }
    }

    /// <summary>
    /// End-to-end lexer tests for Unicode property escape patterns
    /// (<c>/\p{...}/</c> and <c>/\P{...}/</c>) with optional quantifiers,
    /// including multi-byte UTF-8 input.
    /// </summary>
    public class UnicodePropertyPatternMatchingTests
    {
        private static List<StepToken> RunToCompletion(StepLexer lexer, byte[] input)
        {
            lexer.Initialize(input, "test.txt");
            var tokens = new List<StepToken>();
            var guard = 0;
            while (lexer.ActivePaths.Any(p => p.IsValid && p.Position < input.Length))
            {
                var result = lexer.Step();
                tokens.AddRange(result.NewTokens);
                if (++guard > 1000)
                {
                    break;
                }
            }
            return tokens;
        }

        [Fact]
        public void UnicodeLetterPattern_MatchesMultibyteUtf8Input()
        {
            var lexer = new StepLexer();
            lexer.AddRule(new TokenRule("WORD", @"/\p{L}+/"));
            lexer.AddRule(new TokenRule("NUMBER", @"/\p{Nd}+/"));

            // "héllo123": h (1 byte) + é (2 bytes) + llo (3 bytes) + 123
            var input = Encoding.UTF8.GetBytes("héllo123");
            var tokens = RunToCompletion(lexer, input);

            var word = Assert.Single(tokens.Where(t => t.Type == "WORD"));
            Assert.Equal("héllo", word.Value);
            var number = Assert.Single(tokens.Where(t => t.Type == "NUMBER"));
            Assert.Equal("123", number.Value);
        }

        [Fact]
        public void NegatedUnicodePattern_MatchesNonLetters()
        {
            var lexer = new StepLexer();
            lexer.AddRule(new TokenRule("NONLETTER", @"/\P{L}+/"));
            lexer.AddRule(new TokenRule("WORD", @"/\p{L}+/"));

            var input = Encoding.UTF8.GetBytes("123abc");
            var tokens = RunToCompletion(lexer, input);

            Assert.Contains(tokens, t => t.Type == "NONLETTER" && t.Value == "123");
            Assert.Contains(tokens, t => t.Type == "WORD" && t.Value == "abc");
        }

        [Fact]
        public void ExactCountQuantifier_MatchesExactRepetitions()
        {
            var lexer = new StepLexer();
            lexer.AddRule(new TokenRule("THREE_DIGITS", @"/\p{Nd}{3}/"));

            var input = Encoding.UTF8.GetBytes("12345");
            var tokens = RunToCompletion(lexer, input);

            var token = Assert.Single(tokens);
            Assert.Equal("123", token.Value);
        }

        [Fact]
        public void RangeQuantifier_MatchesGreedyWithinBounds()
        {
            var lexer = new StepLexer();
            lexer.AddRule(new TokenRule("LETTERS", @"/\p{L}{2,3}/"));

            var input = Encoding.UTF8.GetBytes("abcd");
            var tokens = RunToCompletion(lexer, input);

            var token = Assert.Single(tokens);
            Assert.Equal("abc", token.Value);
        }

        [Fact]
        public void OpenEndedRangeQuantifier_MatchesAtLeastMinimum()
        {
            var lexer = new StepLexer();
            lexer.AddRule(new TokenRule("LETTERS", @"/\p{L}{2,}/"));
            lexer.AddRule(new TokenRule("SINGLE", @"/\p{L}/"));

            var input = Encoding.UTF8.GetBytes("abcdef");
            var tokens = RunToCompletion(lexer, input);

            Assert.Contains(tokens, t => t.Type == "LETTERS" && t.Value == "abcdef");
        }

        [Fact]
        public void OptionalQuantifier_MatchesSingleCodepoint()
        {
            var lexer = new StepLexer();
            lexer.AddRule(new TokenRule("MAYBE_DIGIT", @"/\p{Nd}?/"));

            var input = Encoding.UTF8.GetBytes("7x");
            var tokens = RunToCompletion(lexer, input);

            var token = Assert.Single(tokens);
            Assert.Equal("7", token.Value);
        }

        [Fact]
        public void StarQuantifier_NeverProducesZeroLengthMatch()
        {
            // \p{L}* must not report a zero-length match at a non-matching
            // position: the lexer advances by the match length and would
            // loop forever otherwise.
            var lexer = new StepLexer();
            lexer.AddRule(new TokenRule("MAYBE_LETTER", @"/\p{L}*/"));
            lexer.AddRule(new TokenRule("DIGITS", @"/\p{Nd}+/"));

            var input = Encoding.UTF8.GetBytes("123");
            var tokens = RunToCompletion(lexer, input);

            var token = Assert.Single(tokens);
            Assert.Equal("DIGITS", token.Type);
            Assert.Equal("123", token.Value);
        }

        [Fact]
        public void UnicodePropertyPattern_MatchesSupplementaryPlaneEmoji()
        {
            var lexer = new StepLexer();
            lexer.AddRule(new TokenRule("EMOJI", @"/\p{Emoji}+/"));
            lexer.AddRule(new TokenRule("OTHER", @"/\P{Emoji}+/"));

            var input = Encoding.UTF8.GetBytes("\U0001F600\U0001F601ab");
            var tokens = RunToCompletion(lexer, input);

            Assert.Contains(tokens, t => t.Type == "EMOJI" && t.Value == "\U0001F600\U0001F601");
            Assert.Contains(tokens, t => t.Type == "OTHER" && t.Value == "ab");
        }

        [Fact]
        public void ScriptPattern_MatchesOnlyScriptCharacters()
        {
            var lexer = new StepLexer();
            lexer.AddRule(new TokenRule("GREEK", @"/\p{Greek}+/"));
            lexer.AddRule(new TokenRule("OTHER", @"/\P{Greek}+/"));

            var input = Encoding.UTF8.GetBytes("aαβc");
            var tokens = RunToCompletion(lexer, input);

            Assert.Contains(tokens, t => t.Type == "OTHER" && t.Value == "a");
            Assert.Contains(tokens, t => t.Type == "GREEK" && t.Value == "αβ");
            Assert.Contains(tokens, t => t.Type == "OTHER" && t.Value == "c");
        }

        [Fact]
        public void BlockPattern_MatchesBlockRange()
        {
            var lexer = new StepLexer();
            lexer.AddRule(new TokenRule("BASIC_LATIN", @"/\p{Basic_Latin}+/"));

            var input = Encoding.UTF8.GetBytes("ab\u00E9");
            var tokens = RunToCompletion(lexer, input);

            // U+00E9 is in Latin-1 Supplement, not Basic Latin
            var token = Assert.Single(tokens);
            Assert.Equal("ab", token.Value);
        }

        [Fact]
        public void LoosePropertyName_IsAcceptedAtMatchTime()
        {
            var lexer = new StepLexer();
            lexer.AddRule(new TokenRule("WORD", @"/\p{lowercase_letter}+/"));

            var input = Encoding.UTF8.GetBytes("ab1");
            var tokens = RunToCompletion(lexer, input);

            var token = Assert.Single(tokens);
            Assert.Equal("ab", token.Value);
        }

        [Fact]
        public void UnknownPropertyName_NeverMatches()
        {
            var lexer = new StepLexer();
            lexer.AddRule(new TokenRule("BOGUS", @"/\p{NoSuchProperty}+/"));

            var input = Encoding.UTF8.GetBytes("abc");
            var tokens = RunToCompletion(lexer, input);

            Assert.Empty(tokens);
        }

        [Fact]
        public void UnsupportedTrailingSyntax_AfterPropertyEscape_FailsToMatch()
        {
            var lexer = new StepLexer();
            // A character class after the property escape is not supported
            lexer.AddRule(new TokenRule("BOGUS", @"/\p{L}+[0-9]/"));

            var input = Encoding.UTF8.GetBytes("ab12");
            var tokens = RunToCompletion(lexer, input);

            Assert.Empty(tokens);
        }
    }
}
