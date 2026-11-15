using System.Linq;
using System.Text;
using Xunit;
using Xunit.Abstractions;

namespace DevelApp.ReDos.Tests
{
    /// <summary>
    /// Bounded-quantifier and quoted-literal semantics under adversarial
    /// input (issue #103 extension): {m}, {m,n} and \Q...\E are supported
    /// constructs, so unlike the group/alternation patterns they are
    /// actually evaluated. These tests pin that their evaluation stays
    /// forward-only and bounded: exact tiling with a stalled remainder,
    /// greedy-max range consumption, quoted metacharacters making a
    /// nested-quantifier-looking pattern supported (and still linear),
    /// and bounded quantifiers at full adversarial length.
    /// </summary>
    public class BoundedQuantifierSemanticsTests
    {
        private readonly ITestOutputHelper _output;

        public BoundedQuantifierSemanticsTests(ITestOutputHelper output)
        {
            _output = output;
        }

        [Fact]
        public void ExactQuantifier_TilesAndStallsOnRemainder()
        {
            // [a]{100} over 250 a's: two full 100-letter tokens, then the
            // 50-letter remainder cannot match and the run stalls exactly
            // at byte 200 - one forward scan, no repartitioning.
            var input = Encoding.UTF8.GetBytes(new string('a', 250));
            var result = RedosMeasurementHarness.RunStepLexer("/[a]{100}/", input);

            Assert.True(result.Completed);
            Assert.Equal(2, result.TokenCount);
            Assert.Equal(200, result.LastNoMatchPosition);
            Assert.False(result.CompletedInput);
        }

        [Fact]
        public void ExactQuantifier_TilesInputToCompletion()
        {
            // Exactly 300 a's: three full tokens, whole input consumed.
            var input = Encoding.UTF8.GetBytes(new string('a', 300));
            var result = RedosMeasurementHarness.RunStepLexer("/[a]{100}/", input);

            Assert.True(result.Completed);
            Assert.Equal(3, result.TokenCount);
            Assert.True(result.CompletedInput);
            Assert.Equal(-1, result.LastNoMatchPosition);
        }

        [Fact]
        public void RangeQuantifier_ConsumesGreedyMaximumPerToken()
        {
            // [a]{2,4} over 10 a's: greedy maximum per token gives
            // 4 + 4 + 2 - three tokens consuming the input, the remainder
            // still satisfying the lower bound. (The greedy-max bound is
            // pinned by the StepLexer suite's \p{L}{2,3} test.)
            var input = Encoding.UTF8.GetBytes(new string('a', 10));
            var result = RedosMeasurementHarness.RunStepLexer("/[a]{2,4}/", input);

            Assert.True(result.Completed);
            Assert.Equal(3, result.TokenCount);
            Assert.True(result.CompletedInput);
        }

        [Fact]
        public void RangeQuantifier_StallsBelowLowerBound()
        {
            // [a]{2,4} over 5 a's: one 4-letter token, then the single
            // remaining 'a' is below the lower bound - no match, stall at
            // byte 4. A traditional engine would try to hand the 'a' back
            // to the quantifier; the forward-only matcher just stops.
            var input = Encoding.UTF8.GetBytes(new string('a', 5));
            var result = RedosMeasurementHarness.RunStepLexer("/[a]{2,4}/", input);

            Assert.True(result.Completed);
            Assert.Equal(1, result.TokenCount);
            Assert.Equal(4, result.LastNoMatchPosition);
            Assert.False(result.CompletedInput);
        }

        [Fact]
        public void QuotedLiteral_SoleAtom_MatchesMetacharactersLiterally()
        {
            // \Q...\E as the sole atom quotes everything: the
            // nested-quantifier-looking text "(a)+" matches as plain
            // text in one forward scan.
            var input = Encoding.UTF8.GetBytes("(a)+");
            var result = RedosMeasurementHarness.RunStepLexer(@"/\Q(a)+\E/", input);

            Assert.True(result.Completed);
            Assert.Equal(1, result.TokenCount);
            Assert.True(result.CompletedInput);
        }

        [Fact]
        public void QuotedLiteral_ComposedWithOtherAtoms_FailsFastAtFirstPosition()
        {
            // The shipped matcher supports \Q...\E only as the sole atom
            // (a trailing quantifier is also accepted). Composing a quoted
            // literal with other atoms - before or after - is not
            // supported: the rule fails fast at position 0 instead of
            // being evaluated with backtracking. That fail-fast rejection
            // is the immunity for this composition; pinned here so the
            // narrow \Q...\E support surface cannot silently change.
            var pattern = @"/[b]+\Q(a)+\E/";
            var matching = Encoding.UTF8.GetBytes("bbb(a)+");
            var matchingResult = RedosMeasurementHarness.RunStepLexer(pattern, matching);
            Assert.True(matchingResult.Completed);
            Assert.Equal(0, matchingResult.TokenCount);
            Assert.Equal(0, matchingResult.LastNoMatchPosition);

            var prefixForm = Encoding.UTF8.GetBytes(new string('a', 1_024));
            var prefixResult = RedosMeasurementHarness.RunStepLexer(@"/\Qa\E[a]*/", prefixForm);
            Assert.True(prefixResult.Completed);
            Assert.Equal(0, prefixResult.TokenCount);
            Assert.Equal(0, prefixResult.LastNoMatchPosition);
        }

        [Fact]
        public void BoundedQuantifier_CompletesAt100Kb_AdversarialLength()
        {
            // [a]{10,20} over a full 100 KB a-run: ~10,240 tokens, every
            // one a bounded-quantifier evaluation. Must complete within
            // the hard timeout (the many-token quadratic bookkeeping is
            // the known overhead characteristic, capped by the timeout).
            var input = Encoding.UTF8.GetBytes(new string('a', RedosPatternCatalog.LargeLengthBytes));
            var result = RedosMeasurementHarness.RunStepLexer("/[a]{10,20}/", input);

            Assert.True(
                result.Completed,
                "The bounded-quantifier many-token run must stay bounded at 100 KB.");
            Assert.True(result.CompletedInput);
            _output.WriteLine(
                $"[a]{{10,20}} @ 102400 bytes: {result.ElapsedMilliseconds:F3} ms for {result.TokenCount} tokens");
        }
    }
}
