// Pinned regression tests for the upstream issue reports in
// DevelApp-ai/ENFAStepLexer-StepParser. Each class documents the verified
// behavior of one issue so a future change cannot silently reintroduce it.
//
//  - Issue #106: token-rule regexes support a subset of PCRE2 (no groups,
//    no alternation, no anchors); grammar-level alternatives ARE supported
//    and are the intended way to express alternation.
//  - Issue #111: zero-width token rules (empty quoted literals such as
//    `<x> ::= ""`) must be rejected at load and can never match at runtime.
//  - Issue #112: spec for the proposed ResolvedTokens accessor (winning
//    path), pinned here as an acceptance sketch to enable on implementation.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using DevelApp.StepLexer;
using DevelApp.StepParser;
using StepLexerClass = DevelApp.StepLexer.StepLexer;

namespace DevelApp.StepParser.Tests
{
    /// <summary>
    /// Issue #106: alternation and groups are supported at the GRAMMAR
    /// level (production alternatives split by SplitAlternatives / EBNF
    /// normalization) but token-rule regexes are matched by a simplified
    /// engine (TryMatchRegex → TryParseClassSequence) that cannot evaluate
    /// groups, alternation or anchors. These tests pin both facts so
    /// neither the supported grammar-level behavior nor the token-rule
    /// limitation can regress silently.
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

        public static IEnumerable<object[]> UnsupportedTokenRulePatterns =>
            new List<object[]>
            {
                // (pattern, inputThatShouldHaveMatchedHadGroupsBeenSupported)
                new object[] { "/a|b/", "a" },
                new object[] { "/a|b/", "b" },
                new object[] { "/[ab]|c/", "c" },
                new object[] { "/(?:ab)+/", "abab" },
                new object[] { "/(a|b)+/", "ab" },
                new object[] { "/(ab)+/", "abab" },
                new object[] { "/foo|bar/", "bar" }
            };

        [Theory]
        [MemberData(nameof(UnsupportedTokenRulePatterns))]
        public void TokenRuleRegex_GroupsAndAlternation_NeverMatch(string pattern, string input)
        {
            // The runtime token matcher (TryMatchRegex) rejects '(', ')' and
            // '|' as unsupported literals, so a token rule whose pattern
            // relies on them matches nothing. Pinned as current behavior:
            // the input is reported as unexpected instead of being matched
            // wrongly (issue #106).
            var grammar = "Grammar: Issue106TokenRule\n" +
                          "<start> ::= <word>\n" +
                          $"<word> ::= {pattern}\n";

            using var engine = new StepParserEngine();
            engine.LoadGrammarFromContent(grammar);

            var result = engine.Parse(input);
            Assert.False(result.Success, $"token-rule pattern {pattern} must not match (issue #106)");
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
        public void Phase1And2_DecomposeGroupAlternation_ButRuntimeMatcherDoesNotUseThem()
        {
            // The two-phase pattern parser correctly decomposes `(a|b)+` into
            // group/alternation/quantifier states — but the runtime token
            // matcher (TryMatchRule) never consults those states, so a
            // token rule with this pattern still matches nothing (issue #106).
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

            // The runtime matcher, by contrast, rejects the same pattern:
            lexer.ClearRules();
            lexer.AddRule(new TokenRule("word", "/(a|b)+/", ""));
            lexer.Initialize(Encoding.UTF8.GetBytes("ab"));
            var step = lexer.Step();
            Assert.Empty(step.NewTokens);
            Assert.Equal(0, step.ActivePathCount);
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
    /// Issue #112: proposed ResolvedTokens accessor — the flattened token
    /// sequence of the single winning path after ambiguity resolution, as
    /// opposed to Tokens which contains the per-path union (by design, see
    /// issue #109). The first test pins the invariant any implementation
    /// must preserve; the acceptance sketch below is the full spec.
    /// </summary>
    public class Issue112_ResolvedTokensSpecTests
    {
        [Fact]
        public void Tokens_OnUnambiguousSuccess_ConcatenateToInput()
        {
            // On an unambiguous grammar the token stream is a single path,
            // so it already concatenates back to the input. ResolvedTokens
            // (issue #112) must preserve exactly this invariant on every
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

        [Fact(Skip = "Spec only until ResolvedTokens is implemented (issue #112). " +
                     "Enable together with the acceptance sketch below.")]
        public void ResolvedTokens_Acceptance()
        {
            // Acceptance sketch (together with the skipped test above):
            //
            // var grammar = "<start> ::= <word> ;\n" +
            //               "<word> ::= /[a-z]+/\n" +
            //               "<word> ::= /[a-z]+ <ws>/\n" +
            //               "<ws> ::= /\\s+/";
            // using var engine = new StepParserEngine();
            // engine.LoadGrammarFromContent(grammar);
            // var result = engine.Parse("foo");
            //
            // Assert.True(result.Success, "input must be derivable");
            // Assert.NotNull(result.ResolvedTokens);               // accessor exists
            // Assert.NotEmpty(result.ResolvedTokens);              // winning path has tokens
            // Assert.All(result.ResolvedTokens,
            //     t => Assert.True(t.Length > 0));                   // no zero-width tokens
            // Assert.All(result.ResolvedTokens,
            //     t => Assert.Equal("word", t.Type));               // typed by winning production path
            // Assert.Equal("foo",
            //     string.Concat(result.ResolvedTokens.Select(t => t.Value)));
            // Assert.True(result.ResolvedTokens.Count <= result.Tokens.Count,
            //     "ResolvedTokens is a subsequence of the per-path union");
            Assert.True(false, "ResolvedTokens is not implemented yet (issue #112)");
        }
    }
}
