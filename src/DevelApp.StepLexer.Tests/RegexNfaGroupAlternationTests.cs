using System.Collections.Generic;
using System.Linq;
using System.Text;
using DevelApp.StepLexer;
using Xunit;

namespace DevelApp.StepLexer.Tests
{
    /// <summary>
    /// Tests for within-rule groups and alternation in token-rule patterns
    /// (issue #129, Gap 1): such patterns are compiled once at rule-add time
    /// into a forward-only NFA and matched by longest-match simulation, so
    /// grammar authors can write ordinary regexes (block comments, strings
    /// with escaped quotes, ...) without backtracking cost at match time.
    /// Constructs that need backtracking/lookaround (backreferences,
    /// anchors, lookahead, scoped modifiers) are rejected at compile time
    /// with a specific validation error instead of silently never matching.
    /// </summary>
    public class RegexNfaGroupAlternationTests
    {
        private static List<StepToken> LexSingleRule(string pattern, string input)
        {
            var lexer = new StepLexer();
            lexer.AddRule(new TokenRule("T", pattern));
            return RunToCompletion(lexer, Encoding.UTF8.GetBytes(input));
        }

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
        public void TopLevelAlternation_MatchesEachBranch()
        {
            var dog = LexSingleRule("/cat|dog/", "dog");
            Assert.Single(dog);
            Assert.Equal("dog", dog[0].Value);

            var cat = LexSingleRule("/cat|dog/", "cat");
            Assert.Single(cat);
            Assert.Equal("cat", cat[0].Value);

            Assert.Empty(LexSingleRule("/cat|dog/", "cow"));
        }

        [Fact]
        public void GroupWithAlternation_Quantified_MatchesRuns()
        {
            var tokens = LexSingleRule("/(a|b)+/", "abba");
            Assert.Single(tokens);
            Assert.Equal("abba", tokens[0].Value);
        }

        [Fact]
        public void NonCapturingGroup_IsSupported()
        {
            var tokens = LexSingleRule("/(?:ab)+/", "abab");
            Assert.Single(tokens);
            Assert.Equal("abab", tokens[0].Value);
            Assert.Empty(LexSingleRule("/(?:ab)+/", "a"));
        }

        [Fact]
        public void NestedGroups_Match()
        {
            var tokens = LexSingleRule("/((a|b)c)+/", "acbc");
            Assert.Single(tokens);
            Assert.Equal("acbc", tokens[0].Value);
        }

        [Fact]
        public void BoundedQuantifierOnGroup_MatchesUpToMax()
        {
            var three = LexSingleRule("/(ab){2,3}/", "ababab");
            Assert.Single(three);
            Assert.Equal("ababab", three[0].Value);

            var two = LexSingleRule("/(ab){2,3}/", "abab");
            Assert.Single(two);
            Assert.Equal("abab", two[0].Value);

            Assert.Empty(LexSingleRule("/(ab){2,3}/", "abx"));
        }

        [Fact]
        public void GroupWithInlineModifier_MatchesCaseInsensitively()
        {
            var tokens = LexSingleRule("/(?i)(ab|cd)+/", "abCD");
            Assert.Single(tokens);
            Assert.Equal("abCD", tokens[0].Value);
        }

        [Fact]
        public void BlockComment_CanonicalPattern_Matches()
        {
            // The C17/JavaScript grammar rule (Minotaur-Grammars): the lexer
            // now compiles the group/alternation instead of never matching.
            const string pattern = @"/\*([^*]|\*+[^*/])*\*+\//";

            var simple = LexSingleRule(pattern, "/* hello * world */");
            Assert.Single(simple);
            Assert.Equal("/* hello * world */", simple[0].Value);

            var empty = LexSingleRule(pattern, "/**/");
            Assert.Single(empty);
            Assert.Equal("/**/", empty[0].Value);

            var stars = LexSingleRule(pattern, "/* **/ */");
            Assert.Single(stars);
            Assert.Equal("/* **/ */", stars[0].Value);
        }

        [Fact]
        public void BlockComment_LongestMatch_StopsAtFirstCloser()
        {
            const string pattern = @"/\*([^*]|\*+[^*/])*\*+\//";
            var tokens = LexSingleRule(pattern, "/* a */ x");
            Assert.Single(tokens);
            Assert.Equal("/* a */", tokens[0].Value);
        }

        [Fact]
        public void BlockComment_Unterminated_DoesNotMatch()
        {
            const string pattern = @"/\*([^*]|\*+[^*/])*\*+\//";
            Assert.Empty(LexSingleRule(pattern, "/* unterminated"));
        }

        [Fact]
        public void BlockComment_LargeInput_StaysLinear()
        {
            // A backtracking matcher would blow up on this; the NFA
            // simulation is linear in the input length.
            const string pattern = @"/\*([^*]|\*+[^*/])*\*+\//";

            var terminated = "/*" + new string('a', 20_000) + "*/";
            var tokens = LexSingleRule(pattern, terminated);
            Assert.Single(tokens);
            Assert.Equal(terminated.Length, tokens[0].Length);

            var unterminated = "/*" + new string('*', 20_000) + "x";
            Assert.Empty(LexSingleRule(pattern, unterminated));
        }

        [Fact]
        public void StringLiteral_EscapedInteriorQuotes_Match()
        {
            // C# regular string with an escaped quote: "a\"b"
            const string pattern = @"/"([^""\\\n]|\\.)*""/";

            var escaped = LexSingleRule(pattern, "\"a\\\"b\"");
            Assert.Single(escaped);
            Assert.Equal("\"a\\\"b\"", escaped[0].Value);

            var plain = LexSingleRule(pattern, "\"plain\"");
            Assert.Single(plain);
            Assert.Equal("\"plain\"", plain[0].Value);

            Assert.Empty(LexSingleRule(pattern, "\"a\\\""));
        }

        [Fact]
        public void VerbatimString_DoubledQuotes_Match()
        {
            // C# verbatim string with a doubled quote: @"a""b"
            const string pattern = @"/@""([^""]|"""")*""/";

            var tokens = LexSingleRule(pattern, "@\"a\"\"b\"");
            Assert.Single(tokens);
            Assert.Equal("@\"a\"\"b\"", tokens[0].Value);
        }

        [Fact]
        public void RawString_InteriorQuotes_Match()
        {
            // C# raw string literal (C# 10) with interior quotes:
            // """he said "hi" """
            const string pattern = @"/[\x22]{3}([^\x22]|[\x22][^\x22]|[\x22]{2}[^\x22])*[\x22]{3}/";

            var input = "\"\"\"he said \"hi\" \"\"\"";
            var tokens = LexSingleRule(pattern, input);
            Assert.Single(tokens);
            Assert.Equal(input, tokens[0].Value);
        }

        [Theory]
        [InlineData("/[a-z]+/")]
        [InlineData("/cat|dog/")]
        [InlineData(@"/\*([^*]|\*+[^*/])*\*+\//")]
        [InlineData(@"/"([^""\\\n]|\\.)*""/")]
        [InlineData("\"literal\"")]
        [InlineData("/\\p{L}+/")]
        public void Validation_AcceptsSupportedPatterns(string pattern)
        {
            Assert.True(StepLexer.TryValidateRulePattern(pattern, out var error),
                $"pattern {pattern} should be supported (error: {error})");
            Assert.Null(error);
        }

        [Theory]
        [InlineData(@"/(\w)\1/", "backreference")]
        [InlineData("/(?=a)b/", "group construct")]
        [InlineData("/(?<!a)b/", "group construct")]
        [InlineData("/^abc/", "anchor")]
        [InlineData("/abc$/", "anchor")]
        [InlineData(@"/\babc/", "anchor")]
        [InlineData("/(?i:ab)/", "group construct")]
        [InlineData("/(ab){100}/", "cap")]
        [InlineData("/a(b/", "unclosed")]
        public void Validation_RejectsUnsupportedConstructs(string pattern, string errorKeyword)
        {
            Assert.False(StepLexer.TryValidateRulePattern(pattern, out var error),
                $"pattern {pattern} must be rejected as unsupported");
            Assert.NotNull(error);
            Assert.Contains(errorKeyword, error!);
        }

        [Fact]
        public void UnsupportedPattern_NeverMatchesAtRuntime_DefenseInDepth()
        {
            // Even a rule constructed directly (not through the loader) with
            // an unsupported pattern must not match: compile-time rejection
            // is the primary protection, NeverMatches is the backstop.
            var lexer = new StepLexer();
            lexer.AddRule(new TokenRule("T", @"/(\w)\1/"));
            lexer.Initialize(Encoding.UTF8.GetBytes("aa"));
            var step = lexer.Step();
            Assert.Empty(step.NewTokens);
            Assert.Equal(0, step.ActivePathCount);
        }
    }
}
