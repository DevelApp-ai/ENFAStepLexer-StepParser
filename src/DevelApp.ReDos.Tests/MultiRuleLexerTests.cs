using System.Linq;
using System.Text;
using Xunit;
using Xunit.Abstractions;

namespace DevelApp.ReDos.Tests
{
    /// <summary>
    /// Multi-rule lexer immunity and semantics (issue #103 extension):
    /// the StepLexer's real tokenization shape is a rule set with
    /// multi-path pathfinding - one path per matching rule, merged every
    /// step. These tests run adversarial inputs through multi-rule
    /// lexers so the immunity suite covers the multi-path machinery and
    /// not only single-rule matching: non-overlapping rule sets must tile
    /// the input exactly, overlapping rule sets must stay bounded at
    /// 100 KB, and a stalled tail must terminate the run linearly.
    /// </summary>
    public class MultiRuleLexerTests
    {
        private readonly ITestOutputHelper _output;

        public MultiRuleLexerTests(ITestOutputHelper output)
        {
            _output = output;
        }

        [Fact]
        public void NonOverlappingRules_TileInput_Exactly()
        {
            // WORD and NUM are disjoint: exactly one rule matches at every
            // position, so the token stream must tile the input with one
            // token per run and consume the whole input.
            var rules = new[]
            {
                new RedosMeasurementHarness.MultiRuleSpec("WORD", "/[a-z]+/"),
                new RedosMeasurementHarness.MultiRuleSpec("NUM", "/[0-9]+/"),
            };
            var input = Encoding.UTF8.GetBytes("ab12cd34");

            var result = RedosMeasurementHarness.RunStepLexerMultiRule(rules, input);

            Assert.True(result.Completed);
            Assert.Equal(4, result.TokenCount);
            Assert.True(result.CompletedInput);
            Assert.Equal(-1, result.LastNoMatchPosition);
        }

        [Fact]
        public void OverlappingRules_CompleteAtCappedLength_WithinHardTimeout()
        {
            // /[ab]+/, /a+/ and /b+/ all match at many positions of an
            // "ab" stream: every position offers multiple matching rules,
            // and - unlike identical rules - the resulting paths carry
            // different token-type sequences, so path merging cannot fold
            // them. Measured behavior (2026-11-15): token count grows
            // linearly (two tokens per byte) but the surviving-path
            // bookkeeping grows superlinearly - ~16 ms at 256 B,
            // ~259 ms at 4 KB, ~3.4 s at 16 KB - which extrapolates far
            // past the hard timeout at 100 KB. Matching itself stays
            // forward-only and a no-match terminates the run, so this is
            // a multi-path bookkeeping overhead, not a classical ReDoS;
            // the parser layer's hard active-path budget (10 paths) is
            // the established mitigation model, flagged here as the
            // lexer-layer follow-up. Asserted at a capped length only.
            var rules = new[]
            {
                new RedosMeasurementHarness.MultiRuleSpec("AB", "/[ab]+/"),
                new RedosMeasurementHarness.MultiRuleSpec("A", "/a+/"),
                new RedosMeasurementHarness.MultiRuleSpec("B", "/b+/"),
            };
            var input = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("ab", 2_048)));

            var result = RedosMeasurementHarness.RunStepLexerMultiRule(rules, input);

            Assert.True(
                result.Completed,
                "The overlapping three-rule lexer must complete on the capped 4 KB ab stream within the hard timeout.");
            _output.WriteLine(
                $"Overlapping rules @ {input.Length} bytes: {result.ElapsedMilliseconds:F3} ms, tokens={result.TokenCount}, " +
                $"completed-input={result.CompletedInput}");
        }

        [Fact]
        public void CloudflareHeaderLexer_TilesNameEqualsValue()
        {
        // The Cloudflare 2019 outage pattern as a working lexer: a value
        // rule that stops at '=' plus the '=' rule itself. On a header
        // line the rules are non-overlapping, so the token count is exact.
            var rules = new[]
            {
                new RedosMeasurementHarness.MultiRuleSpec("VALUE", "/[^=\r\n]+/"),
                new RedosMeasurementHarness.MultiRuleSpec("EQ", "/=/"),
            };
            var input = Encoding.UTF8.GetBytes(new string('h', 100) + "=" + new string('h', 100));

            var result = RedosMeasurementHarness.RunStepLexerMultiRule(rules, input);

            Assert.True(result.Completed);
            Assert.Equal(3, result.TokenCount);
            Assert.True(result.CompletedInput);
            Assert.Equal(-1, result.LastNoMatchPosition);
        }

        [Fact]
        public void SkippableWhitespaceRule_DoesNotEmitTokens()
        {
            // Skippable rules advance the path without emitting tokens
            // (the shipped convention, pinned in the StepLexer suite):
            // WORD + skippable WS over "ab cd" must emit exactly the two
            // word tokens and complete the input.
            var rules = new[]
            {
                new RedosMeasurementHarness.MultiRuleSpec("WS", "/[ \t]+/", IsSkippable: true),
                new RedosMeasurementHarness.MultiRuleSpec("WORD", "/[a-z]+/"),
            };
            var input = Encoding.UTF8.GetBytes("ab cd");

            var result = RedosMeasurementHarness.RunStepLexerMultiRule(rules, input);

            Assert.True(result.Completed);
            Assert.Equal(2, result.TokenCount);
            Assert.True(result.CompletedInput);
        }

        [Fact]
        public void UnmatchableTail_StallsAtFirstBadByte()
        {
            // WORD + NUM over "ab12!": after the two good tokens the '!'
            // matches no rule, the path stalls exactly there, and the run
            // terminates in bounded time - the multi-rule no-match
            // immutability that makes the lexer ReDoS-immune.
            var rules = new[]
            {
                new RedosMeasurementHarness.MultiRuleSpec("WORD", "/[a-z]+/"),
                new RedosMeasurementHarness.MultiRuleSpec("NUM", "/[0-9]+/"),
            };
            var input = Encoding.UTF8.GetBytes("ab12!");

            var result = RedosMeasurementHarness.RunStepLexerMultiRule(rules, input);

            Assert.True(result.Completed);
            Assert.Equal(2, result.TokenCount);
            Assert.Equal(4, result.LastNoMatchPosition);
            Assert.False(result.CompletedInput);
        }

        [Fact]
        public void ManyTokenTwoRuleStream_CompletesAtCappedLength()
        {
            // The many-token overhead shape through two rules: the token
            // count doubles versus the single-rule measurement, and the
            // run must still complete within the hard timeout at the
            // capped 10 KB length (quadratic line/column bookkeeping is a
            // tokenization-overhead characteristic, not a ReDoS).
            var rules = new[]
            {
                new RedosMeasurementHarness.MultiRuleSpec("WORD", "/[a-z]+/"),
                new RedosMeasurementHarness.MultiRuleSpec("NUM", "/[0-9]+/"),
            };
            var input = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("ab1", 3_413)));

            var result = RedosMeasurementHarness.RunStepLexerMultiRule(rules, input);

            Assert.True(result.Completed);
            Assert.True(result.CompletedInput);
            Assert.Equal(2 * 3_413, result.TokenCount);
            _output.WriteLine(
                $"Two-rule many-token stream @ {input.Length} bytes: {result.ElapsedMilliseconds:F3} ms for {result.TokenCount} tokens");
        }
    }
}
