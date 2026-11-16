// Pinned regression tests for the upstream issue reports in
// DevelApp-ai/ENFAStepLexer-StepParser. Each class documents the verified
// behavior of one issue so a future change cannot silently reintroduce it.
//
//  - Issue #106: grammar-level alternatives ARE supported; token-rule
//    regexes support a PCRE2 subset (classes, quantifiers, escapes,
//    \p{...}) plus groups/alternation compiled to a forward-only NFA
//    (issue #129); anchors/backreferences/lookaround remain unsupported
//    and are reported at load.
//  - Issue #111: zero-width token rules (empty quoted literals such as
//    `<x> ::= ""`) must be rejected at load and can never match at runtime.
//  - Issue #112: ResolvedTokens — the flattened token sequence of the
//    winning derivation (implemented); Tokens remains the per-path union
//    (issue #109 by design).
//  - Issue #129: within-rule groups/alternation in token-rule patterns are
//    handled internally by the lexer (compile-time NFA, linear match);
//    unsupported constructs are surfaced as load-time diagnostics.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using DevelApp.StepLexer;
using DevelApp.StepParser;
using StepLexerClass = DevelApp.StepLexer.StepLexer;

namespace DevelApp.StepParser.Tests
{
    /// <summary>
    /// Issue #106 (updated by issue #129): alternation and groups are
    /// supported at the GRAMMAR level (production alternatives split by
    /// SplitAlternatives / EBNF normalization) AND inside token-rule regexes
    /// (compiled to a forward-only NFA at rule-add time). These tests pin
    /// both supported levels, plus the remaining unsupported constructs
    /// (anchors, backreferences, lookaround) which are rejected at compile
    /// time and reported at grammar load.
    /// </summary>
    public class Issue106_TokenRuleRegexSubsetTests
    {
        [Fact]
        public void GrammarLevelAlternation_IsSupported()
        {
            var grammar = "Grammar: Issue106Alternation\n" +
                          "<start> ::= <low> | <up>\n" +
                          "<low> ::= /[a-z]+/\n" +
                          "<up> ::= /[A-Z]+/\n";

            using var engine = new StepParserEngine();
            engine.LoadGrammarFromContent(grammar);

            var lower = engine.Parse("abc");
            Assert.True(lower.Success, "lowercase input must parse via the <low> alternative");

            var upper = engine.Parse("ABC");
            Assert.True(upper.Success, "uppercase input must parse via the <up> alternative");
        }

        [Fact]
        public void GrammarLevelInlineGroup_IsSupported()
        {
            var grammar = "Grammar: Issue106InlineGroup\n" +
                          "<start> ::= ( <low> | <up> )\n" +
                          "<low> ::= /[a-z]+/\n" +
                          "<up> ::= /[A-Z]+/\n";

            using var engine = new StepParserEngine();
            engine.LoadGrammarFromContent(grammar);

            var lower = engine.Parse("abc");
            Assert.True(lower.Success, "grouped alternative must support the <low> branch");

            var upper = engine.Parse("ABC");
            Assert.True(upper.Success, "grouped alternative must support the <up> branch");
        }

        public static IEnumerable<object[]> GroupAndAlternationTokenRulePatterns =>
            new List<object[]>
            {
                // (pattern, input) - within-rule groups/alternation are compiled
                // to a forward-only NFA by the lexer (issue #129) and match.
                new object[] { "/a|b/", "a" },
                new object[] { "/a|b/", "b" },
                new object[] { "/[ab]|c/", "c" },
                new object[] { "/(?:ab)+/", "abab" },
                new object[] { "/(a|b)+/", "ab" },
                new object[] { "/(ab)+/", "abab" },
                new object[] { "/foo|bar/", "bar" }
            };

        [Theory]
        [MemberData(nameof(GroupAndAlternationTokenRulePatterns))]
        public void TokenRuleRegex_GroupsAndAlternation_Match(string pattern, string input)
        {
            // Since issue #129 the runtime token matcher compiles groups and
            // alternation into a forward-only NFA, so these patterns match
            // (previously pinned as never-matching under issue #106).
            var grammar = "Grammar: Issue106TokenRule\n" +
                          "<start> ::= <word>\n" +
                          $"<word> ::= {pattern}\n";

            using var engine = new StepParserEngine();
            engine.LoadGrammarFromContent(grammar);

            var result = engine.Parse(input);
            Assert.True(result.Success, $"token-rule pattern {pattern} must match input '{input}' (issue #129)");
            var word = Assert.Single(result.Tokens);
            Assert.Equal("word", word.Type);
            Assert.Equal(input, word.Value);
        }

        public static IEnumerable<object[]> UnsupportedTokenRulePatterns =>
            new List<object[]>
            {
                // Constructs that need backtracking/lookaround stay unsupported
                // and are rejected at compile time (issue #129).
                new object[] { "/(?=a)b/", "ab" },
                new object[] { "/^ab/", "ab" },
                new object[] { "/ab$/", "ab" },
                new object[] { @"/(\w)\1/", "aa" }
            };

        [Theory]
        [MemberData(nameof(UnsupportedTokenRulePatterns))]
        public void TokenRuleRegex_UnsupportedConstructs_NeverMatch(string pattern, string input)
        {
            var grammar = "Grammar: Issue129TokenRule\n" +
                          "<start> ::= <word>\n" +
                          $"<word> ::= {pattern}\n";

            using var engine = new StepParserEngine();
            engine.LoadGrammarFromContent(grammar);

            var result = engine.Parse(input);
            Assert.False(result.Success, $"token-rule pattern {pattern} must not match (issue #129)");
            Assert.True(result.Tokens.All(t => t.Type != "word"),
                $"no 'word' token may be emitted for unsupported pattern {pattern}");
            Assert.Contains(result.Diagnostics, d => d.Code == DiagnosticCodes.LexerUnexpectedInput);
        }

        [Fact]
        public void TokenRuleRegex_SupportedSubset_CharClassSequence_StillMatches()
        {
            var grammar = "Grammar: Issue106Control\n" +
                          "<start> ::= <word>\n" +
                          "<word> ::= /[a-c]+/\n";

            using var engine = new StepParserEngine();
            engine.LoadGrammarFromContent(grammar);

            var result = engine.Parse("abcba");
            Assert.True(result.Success, "character-class sequences are the supported token-rule subset");
            var word = Assert.Single(result.Tokens);
            Assert.Equal("word", word.Type);
            Assert.Equal(5, word.Length);
        }

        [Fact]
        public void TokenRuleRegex_SupportedSubset_UnicodeProperty_StillMatches()
        {
            var grammar = "Grammar: Issue106Control\n" +
                          "<start> ::= <word>\n" +
                          "<word> ::= /\\p{L}+/\n";

            using var engine = new StepParserEngine();
            engine.LoadGrammarFromContent(grammar);

            var result = engine.Parse("abc");
            Assert.True(result.Success, "Unicode property patterns are the supported token-rule subset");
            var word = Assert.Single(result.Tokens);
            Assert.Equal("word", word.Type);
        }

        [Fact]
        public void Phase1And2_DecomposeGroupAlternation_AndRuntimeMatcherUsesThem()
        {
            // The two-phase pattern parser decomposes `(a|b)+` into
            // group/alternation/quantifier states; since issue #129 the
            // runtime token matcher compiles the same constructs into a
            // forward-only NFA, so a token rule with this pattern matches.
            var lexer = new StepLexerClass();
            var ok = lexer.Phase1_LexicalScan(
                new ZeroCopyStringView(Encoding.UTF8.GetBytes("(a|b)+")));
            Assert.True(ok, "phase 1 lexical scan supports group/alternation/quantifier syntax");

            Assert.True(lexer.Phase2_Disambiguation(), "phase 2 builds states from the phase 1 tokens");
            var types = lexer.Phase2Results.Select(s => s.TokenType).ToHashSet();
            Assert.Contains(TokenType.GroupStart, types);
            Assert.Contains(TokenType.Alternation, types);
            Assert.Contains(TokenType.GroupEnd, types);
            Assert.Contains(TokenType.Quantifier, types);

            lexer.ClearRules();
            lexer.AddRule(new TokenRule("word", "/(a|b)+/", ""));
            lexer.Initialize(Encoding.UTF8.GetBytes("ab"));
            var step = lexer.Step();
            var token = Assert.Single(step.NewTokens);
            Assert.Equal("ab", token.Value);
            Assert.Equal(1, step.ActivePathCount);
        }
    }

    /// <summary>
    /// Issue #129 (Gap 1): within-rule groups/alternation in token-rule
    /// patterns are handled internally by the lexer — compiled once at
    /// rule-add time into a forward-only NFA and matched by longest-match
    /// simulation — so grammar authors write ordinary regexes (block
    /// comments, strings with escaped quotes). Responsibility split for
    /// regex validation: the lexer owns the token-rule pattern language and
    /// validates it at compile time; the grammar loader (parser side)
    /// surfaces unsupported patterns as load-time diagnostics instead of
    /// silent never-matching rules.
    /// </summary>
    public class Issue129_TokenRuleGroupAlternationTests
    {
        [Fact]
        public void BlockComment_TokenRule_LexesAndParsesEndToEnd()
        {
            // A literal '/' inside a token-rule pattern would end the /.../
            // symbol at the grammar-file layer, so slashes are written as
            // \x2F (same convention as CSharp10.grammar).
            var grammar = "Grammar: Issue129BlockComment\n" +
                          "<start> ::= <word>\n" +
                          "<word> ::= /[a-z]+/\n" +
                          @"<comment> ::= /\x2F\*([^*]|\*+[^*\x2F])*\*+\x2F/ => { skip }" + "\n";

            using var engine = new StepParserEngine();
            engine.LoadGrammarFromContent(grammar);

            var result = engine.Parse("/* hi * there */abc");
            Assert.True(result.Success, "a block comment followed by a word must parse (issue #129)");
            var word = Assert.Single(result.Tokens);
            Assert.Equal("word", word.Type);
            Assert.Equal("abc", word.Value);
        }

        [Fact]
        public void UnsupportedTokenRulePattern_IsReportedAtLoad()
        {
            var grammar = "Grammar: Issue129Load\n" +
                          "<start> ::= <word>\n" +
                          "<word> ::= /[a-z]+/\n" +
                          "<bad> ::= /(?=a)b/\n";

            var writer = new StringWriter();
            var original = Console.Out;
            Console.SetOut(writer);
            try
            {
                new GrammarLoader().ParseGrammarContent(grammar);
            }
            finally
            {
                Console.SetOut(original);
            }

            var output = writer.ToString();
            Assert.Contains("issue #129", output);
            Assert.Contains("never match", output);
            Assert.Contains("<bad>", output);
        }

        [Fact]
        public void SupportedGroupPattern_IsNotReportedAtLoad()
        {
            // Unique rule name so the negative assertion cannot be confused
            // with another test's load diagnostic (Console.Out is process-wide
            // and xunit runs test classes in parallel).
            var grammar = "Grammar: Issue129LoadOk\n" +
                          "<start> ::= <word>\n" +
                          "<word> ::= /[a-z]+/\n" +
                          @"<zz_supported_comment> ::= /\x2F\*([^*]|\*+[^*\x2F])*\*+\x2F/ => { skip }" + "\n";

            var writer = new StringWriter();
            var original = Console.Out;
            Console.SetOut(writer);
            try
            {
                new GrammarLoader().ParseGrammarContent(grammar);
            }
            finally
            {
                Console.SetOut(original);
            }

            Assert.DoesNotContain("zz_supported_comment", writer.ToString());
        }
    }

    /// <summary>
    /// Issue #111: a token rule whose pattern is an empty quoted literal
    /// (`<x> ::= ""`) used to be accepted at load and matched zero-width at
    /// every input position. Because such a match "succeeds", the stall
    /// tracking (LastNoMatchPosition) never fired, the step cap ended
    /// lexing with the input unconsumed, and the engine reported
    /// Success=true with zero diagnostics on input the grammar cannot
    /// derive (vacuous acceptance). These tests pin the fix: the rule is
    /// rejected at load, zero-width matches are impossible at runtime, and
    /// unmatchable input produces the LX1001 diagnostic instead of a
    /// vacuous success.
    /// </summary>
    public class Issue111_ZeroWidthRuleTests
    {
        [Fact]
        public void ZeroWidthQuotedLiteral_TokenRule_IsRejectedAtLoad()
        {
            var grammar = "Grammar: Issue111Load\n" +
                          "<start> ::= <word>\n" +
                          "<word> ::= /[a-z]+/\n" +
                          "<empty> ::= \"\"\n";

            var definition = new GrammarLoader().ParseGrammarContent(grammar);

            Assert.True(definition.TokenRules.All(r => r.Pattern != "\"\""),
                "an empty quoted literal must not be loaded as a token rule (issue #111)");
            Assert.True(definition.TokenRules.All(r => r.Pattern != "''"),
                "an empty single-quoted literal must not be loaded as a token rule (issue #111)");
        }

        [Fact]
        public void ZeroWidthLiteral_CannotMatchAtRuntime()
        {
            // Defense in depth: even a TokenRule constructed directly (not
            // through the loader) must not produce a zero-width match.
            var lexer = new StepLexerClass();
            lexer.AddRule(new TokenRule("empty", "\"\"", ""));
            lexer.AddRule(new TokenRule("empty2", "''", ""));
            lexer.Initialize(Encoding.UTF8.GetBytes("foo"));

            var step = lexer.Step();
            Assert.Empty(step.NewTokens);
            Assert.Equal(0, step.ActivePathCount);
            Assert.Equal(0, lexer.LastNoMatchPosition); // recorded no-match so LX1001 can fire (issue #111)
        }

        [Fact]
        public void PureZeroWidthGrammar_VacuousSuccess_IsFixed()
        {
            // Before the fix this grammar parsed ANY input to Success=true
            // with a stream of zero-width tokens and no diagnostics.
            var grammar = "Grammar: Issue111Vacuous\n" +
                          "<start> ::= \"\"\n";

            using var engine = new StepParserEngine();
            engine.LoadGrammarFromContent(grammar);

            var result = engine.Parse("foo");
            Assert.False(result.Success, "input the grammar cannot derive must not parse (issue #111)");
            Assert.Contains(result.Diagnostics, d => d.Code == DiagnosticCodes.LexerUnexpectedInput);
            Assert.True(result.Tokens.All(t => t.Length > 0),
                "no zero-width tokens may be emitted (issue #111)");
        }

        [Fact]
        public void ZeroWidthRule_DoesNotPollute_AOtherwiseValidLex()
        {
            // A degenerate zero-width rule alongside real rules used to
            // fork a second lexer path that spun until the step cap,
            // polluting the token stream with zero-width tokens.
            var grammar = "Grammar: Issue111Mixed\n" +
                          "<start> ::= <word>\n" +
                          "<word> ::= /[a-z]+/\n" +
                          "<empty> ::= \"\"\n";

            using var engine = new StepParserEngine();
            engine.LoadGrammarFromContent(grammar);

            var result = engine.Parse("foo");
            Assert.True(result.Success, "the real <word> path must still parse");
            var word = Assert.Single(result.Tokens);
            Assert.Equal("word", word.Type);
            Assert.Equal(3, word.Length);
        }
    }

    /// <summary>
    /// Issue #112: ResolvedTokens — the flattened token sequence of the
    /// winning derivation (the tokens the primary successful parse path
    /// shifted), as opposed to Tokens which is the per-path union of the
    /// ambiguous lex (by design, see issue #109). Implemented in the
    /// engine (ParserPath consumed-token log + StepParser.GetResolvedTokens
    /// + StepParsingResult.ResolvedTokens); these are the acceptance
    /// tests from the original spec sketch, now enabled.
    /// </summary>
    public class Issue112_ResolvedTokensSpecTests
    {
        [Fact]
        public void Tokens_OnUnambiguousSuccess_ConcatenateToInput()
        {
            // On an unambiguous grammar the token stream is a single path,
            // so it already concatenates back to the input. ResolvedTokens
            // (issue #112) preserves exactly this invariant on every
            // successful parse, including ambiguous ones where Tokens is a
            // per-path union.
            var grammar = "Grammar: Issue112Spec\n" +
                          "<start> ::= <word>\n" +
                          "<word> ::= /[a-z]+/\n";

            using var engine = new StepParserEngine();
            engine.LoadGrammarFromContent(grammar);

            var result = engine.Parse("foo");
            Assert.True(result.Success);
            var concatenated = string.Concat(result.Tokens.Select(t => t.Value));
            Assert.Equal("foo", concatenated);
            Assert.All(result.Tokens, t => Assert.True(t.Length > 0));
        }

        [Fact]
        public void ResolvedTokens_Acceptance_Unambiguous()
        {
            var grammar = "Grammar: Issue112Acceptance\n" +
                          "<start> ::= <word>\n" +
                          "<word> ::= /[a-z]+/\n";

            using var engine = new StepParserEngine();
            engine.LoadGrammarFromContent(grammar);

            var result = engine.Parse("foo");
            Assert.True(result.Success, "input must be derivable");

            Assert.NotNull(result.ResolvedTokens);
            Assert.NotEmpty(result.ResolvedTokens);
            Assert.All(result.ResolvedTokens, t => Assert.True(t.Length > 0));
            Assert.All(result.ResolvedTokens, t => Assert.Equal("word", t.Type));
            Assert.Equal("foo", string.Concat(result.ResolvedTokens.Select(t => t.Value)));
            Assert.True(result.ResolvedTokens.Count <= result.Tokens.Count,
                "ResolvedTokens is a subsequence of the per-path union");
            // Identity, not just equality: ResolvedTokens must be the same
            // token instances the lexer emitted, not re- synthesized ones.
            Assert.All(result.ResolvedTokens,
                rt => Assert.Contains(result.Tokens, t => ReferenceEquals(t, rt)));
        }

        [Fact]
        public void ResolvedTokens_AmbiguousLex_ContainsOneInterpretationPerSpan()
        {
            // Two token rules match the same source span, so the lexer
            // emits one token per path into the union (issue #109 by
            // design). ResolvedTokens must contain exactly ONE
            // interpretation of the span: what the winning derivation
            // actually consumed.
            var grammar = "Grammar: Issue112Ambiguous\n" +
                          "<start> ::= <word> | <other>\n" +
                          "<word> ::= /[a-z]+/\n" +
                          "<other> ::= /f.*/\n";

            using var engine = new StepParserEngine();
            engine.LoadGrammarFromContent(grammar);

            var result = engine.Parse("foo");
            Assert.True(result.Success, "both interpretations are derivable");
            // the union still carries every path's token (issue #109)
            Assert.Equal(2, result.Tokens.Count);

            Assert.Single(result.ResolvedTokens);
            Assert.Equal("foo", string.Concat(result.ResolvedTokens.Select(t => t.Value)));
            Assert.All(result.ResolvedTokens,
                rt => Assert.Contains(result.Tokens, t => ReferenceEquals(t, rt)));
        }

        [Fact]
        public void ResolvedTokens_MultiToken_Derivation_TilesInput()
        {
            var grammar = "Grammar: Issue112Multi\n" +
                          "<start> ::= <word> <ws> <word>\n" +
                          "<word> ::= /[a-z]+/\n" +
                          "<ws> ::= /\\s+/\n";

            using var engine = new StepParserEngine();
            engine.LoadGrammarFromContent(grammar);

            var result = engine.Parse("foo bar");
            Assert.True(result.Success);

            Assert.Equal(3, result.ResolvedTokens.Count);
            Assert.Equal("foo bar", string.Concat(result.ResolvedTokens.Select(t => t.Value)));

            var starts = result.ResolvedTokens.Select(t => t.StartPosition).ToList();
            Assert.Equal(0, starts[0]);
            for (int i = 1; i < starts.Count; i++)
            {
                Assert.True(starts[i] > starts[i - 1],
                    "resolved tokens are in strictly increasing source order");
            }
        }

        [Fact]
        public void ResolvedTokens_Empty_WhenInputNotDerivable()
        {
            var grammar = "Grammar: Issue112Failure\n" +
                          "<start> ::= <word>\n" +
                          "<word> ::= /[a-z]+/\n";

            using var engine = new StepParserEngine();
            engine.LoadGrammarFromContent(grammar);

            var result = engine.Parse("123");
            Assert.False(result.Success, "digits are not derivable from this grammar");
            Assert.Empty(result.ResolvedTokens);
        }
    }
}
