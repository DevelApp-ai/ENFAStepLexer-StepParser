using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using Xunit.Abstractions;

namespace DevelApp.ReDos.Tests
{
    /// <summary>
    /// ReDoS-immunity enforcement (issue #103): every article and corpus
    /// pattern, on its adversarial input at 1 KB, 10 KB and 100 KB, must
    /// complete inside the hard per-case timeout. The two-phase StepLexer
    /// matcher is forward-only: supported patterns scan greedily once and
    /// unsupported constructs (groups, alternation, anchors) are rejected
    /// at the first position - there is no backtracking machinery to
    /// attack, so exponential behavior cannot exist. These tests pin that
    /// property continuously in CI against the same pattern corpus that
    /// blows up traditional engines.
    /// </summary>
    public class RedosImmunityTests
    {
        private readonly ITestOutputHelper _output;

        public RedosImmunityTests(ITestOutputHelper output)
        {
            _output = output;
        }

        public static TheoryData<string> PatternNames
        {
            get
            {
                var data = new TheoryData<string>();
                foreach (var pattern in RedosPatternCatalog.ArticleAndCorpusPatterns)
                {
                    data.Add(pattern.Name);
                }

                return data;
            }
        }

        public static IEnumerable<int> AdversarialLengths => new[]
        {
            RedosPatternCatalog.SmallLengthBytes,
            RedosPatternCatalog.MediumLengthBytes,
            RedosPatternCatalog.LargeLengthBytes,
        };

        [Theory]
        [MemberData(nameof(PatternNames))]
        public void StepLexer_CompletesAdversarialInput_WithinHardTimeout(string name)
        {
            var pattern = RedosPatternCatalog.FindArticleOrCorpusPattern(name);

            foreach (var length in AdversarialLengths)
            {
                var input = pattern.BuildInput(length);
                var result = RedosMeasurementHarness.RunStepLexer(pattern.LexerRulePattern, input);

                Assert.True(
                    result.Completed,
                    $"{name} on {length} bytes of adversarial input did not complete within "
                        + $"{RedosMeasurementHarness.StepLexerHardTimeoutMs} ms. "
                        + "This is a ReDoS regression: the StepLexer matcher must never backtrack.");

                _output.WriteLine(
                    $"{name}: {length} bytes -> {result.ElapsedMilliseconds:F3} ms, " +
                    $"tokens={result.TokenCount}, no-match-at={result.LastNoMatchPosition}, completed={result.CompletedInput}");
            }
        }

        public static TheoryData<string> UnsupportedPatternNames
        {
            get
            {
                var data = new TheoryData<string>();
                foreach (var pattern in RedosPatternCatalog.ArticleAndCorpusPatterns)
                {
                    if (!pattern.SupportedByStepLexer)
                    {
                        data.Add(pattern.Name);
                    }
                }
                return data;
            }
        }

        [Theory]
        [MemberData(nameof(UnsupportedPatternNames))]
        public void UnsupportedConstructs_FailFastAtFirstPosition(string name)
        {
            // The remaining unsupported article/corpus patterns contain
            // anchors (^/$), which the forward-only matcher does not
            // evaluate (since issue #129 groups/alternation ARE supported
            // via the compile-time NFA and are covered by the hard-timeout
            // theory above). Unsupported constructs must be rejected at
            // position 0 - in time bounded by the pattern length - rather
            // than matching wrongly or evaluating with backtracking.
            var pattern = RedosPatternCatalog.FindArticleOrCorpusPattern(name);
            Assert.False(pattern.SupportedByStepLexer);

            var input = pattern.BuildInput(RedosPatternCatalog.LargeLengthBytes);
            var result = RedosMeasurementHarness.RunStepLexer(pattern.LexerRulePattern, input);

            Assert.True(result.Completed);
            Assert.Equal(0, result.TokenCount);
            Assert.Equal(0, result.LastNoMatchPosition);
        }

        [Theory]
        [MemberData(nameof(PatternNames))]
        public void NonBacktrackingReference_CompletesAdversarialInput(string name)
        {
            // The linear-time .NET reference engine (RegexOptions.NonBacktracking)
            // must also complete on the same inputs - a cross-check that the
            // adversarial corpus is matchable in linear time and the timeout
            // budget is generous.
            var pattern = RedosPatternCatalog.FindArticleOrCorpusPattern(name);

            foreach (var length in AdversarialLengths)
            {
                var input = pattern.BuildInput(length);
                var result = RedosMeasurementHarness.RunNonBacktrackingBaseline(pattern.CorePattern, input);

                Assert.False(
                    result.TimedOut,
                    $"RegexOptions.NonBacktracking timed out on {name} at {length} bytes; "
                        + "unexpected for a linear-time engine.");
            }
        }

        [Theory]
        [MemberData(nameof(PatternNames))]
        public void BacktrackingBaseline_IsRecorded_PerPattern(string name)
        {
            // The traditional .NET backtracking engine on the identical
            // adversarial input at the pattern's representative length.
            // Its outcome is recorded (blowup patterns hit the match
            // timeout); no speed assertion is made for the baseline - the
            // immunity guarantee under test is the StepLexer's.
            var pattern = RedosPatternCatalog.FindArticleOrCorpusPattern(name);
            var input = pattern.BuildInput(pattern.BaselineLengthBytes);
            var result = RedosMeasurementHarness.RunBacktrackingBaseline(pattern.CorePattern, input);

            _output.WriteLine(
                $"{name}: backtracking baseline on {pattern.BaselineLengthBytes} bytes -> " +
                (result.TimedOut
                    ? $"> {RedosMeasurementHarness.BacktrackingBaselineTimeoutMs} ms (match timeout)"
                    : $"{result.ElapsedMilliseconds:F3} ms (match={result.IsMatch})"));
        }
    }
}
