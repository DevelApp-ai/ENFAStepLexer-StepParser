using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace DevelApp.ReDos.Tests
{
    /// <summary>
    /// Adversarial input shapes used to attack each ReDoS pattern. Each kind
    /// is the classic non-matching input that drives a traditional
    /// backtracking engine into exponential (or quadratic) exploration for
    /// its pattern family: the pattern is always close to matching, so the
    /// engine keeps exploring partitions instead of giving up.
    /// </summary>
    public enum AdversarialInputKind
    {
        /// <summary>
        /// 'a' repeated n times with no terminating token: fails patterns
        /// that require a trailing character ((a*)*b, (a+)+b, (a|b|ab)*c).
        /// </summary>
        AllAs,

        /// <summary>
        /// 'x' repeated n times with no trailing 'y': fails (x+x+)+y.
        /// </summary>
        AllXs,

        /// <summary>
        /// 'a' repeated n-1 times followed by '!': fails the anchored
        /// nested-quantifier validation patterns without ever letting them
        /// match.
        /// </summary>
        AsTrailingBang,

        /// <summary>
        /// 'b' repeated n-9 times followed by exactly nine 'a's: one 'a'
        /// short of the (.*a){10} bound, so the bounded nested quantifier
        /// must exhaust every dot-star partition.
        /// </summary>
        BsPlusNineAs,

        /// <summary>
        /// 'h' repeated n times: a long header line containing no '=',
        /// the Cloudflare 2019 outage shape for .*(?:.*=.*).
        /// </summary>
        HeaderNoEquals,

        /// <summary>
        /// "ab1" repeated until n bytes: every 3-byte group is one token,
        /// the many-token tokenization shape (used for measurement only).
        /// </summary>
        Ab1Groups,
    }

    /// <summary>
    /// One ReDoS pattern under test: the pattern, the adversarial input
    /// that makes traditional backtracking engines explode, and whether the
    /// two-phase StepLexer matcher supports the pattern's constructs.
    ///
    /// Patterns containing groups, alternation or anchors are NOT
    /// supported by the forward-only StepLexer matcher (issue #83): they
    /// are rejected at the first position in time proportional to the
    /// pattern length. That fail-fast rejection is itself the ReDoS
    /// immunity for this pattern family - there is no backtracking
    /// machinery to attack - and the shipped behavior is asserted here so
    /// it cannot silently regress into a backtracking matcher.
    /// </summary>
    public sealed class RedosPattern
    {
        /// <summary>Human-readable name used in test output and reports.</summary>
        public required string Name { get; init; }

        /// <summary>Where the pattern comes from (article table, Davis et al. corpus, scaling analogue).</summary>
        public required string Source { get; init; }

        /// <summary>The regex body, without StepLexer's surrounding /.../ delimiters.</summary>
        public required string CorePattern { get; init; }

        /// <summary>The ReDoS family the pattern belongs to.</summary>
        public required string Family { get; init; }

        /// <summary>The adversarial input shape for this pattern.</summary>
        public required AdversarialInputKind InputKind { get; init; }

        /// <summary>
        /// Representative input length (bytes) for the traditional-engine
        /// backtracking baseline: large enough to demonstrate the blowup,
        /// small enough to keep CI fast (bounded by a 500 ms match timeout).
        /// </summary>
        public required int BaselineLengthBytes { get; init; }

        /// <summary>Whether the StepLexer matcher supports every construct in the pattern.</summary>
        public required bool SupportedByStepLexer { get; init; }

        /// <summary>
        /// Whether the traditional .NET backtracking engine is expected to
        /// blow up (hit the match timeout) on this pattern's adversarial
        /// input. Two article patterns are deliberately false: unanchored
        /// (a+)+ and (a|a)* match their input prefix instantly in every
        /// engine - the blowup requires anchoring, which the Davis corpus
        /// anchored variants demonstrate. The flag is asserted in the
        /// report test so a silently changed baseline behavior is caught.
        /// </summary>
        public required bool ExpectedBacktrackingBlowup { get; init; }

        /// <summary>An honest-measurement note recorded with the pattern.</summary>
        public string Note { get; init; } = string.Empty;

        /// <summary>The pattern in TokenRule /.../ form for the StepLexer run.</summary>
        public string LexerRulePattern => "/" + CorePattern + "/";

        /// <summary>Build the adversarial input of the given length in bytes.</summary>
        public byte[] BuildInput(int lengthBytes)
        {
            var text = InputKind switch
            {
                AdversarialInputKind.AllAs => new string('a', lengthBytes),
                AdversarialInputKind.AllXs => new string('x', lengthBytes),
                AdversarialInputKind.AsTrailingBang => new string('a', lengthBytes - 1) + "!",
                AdversarialInputKind.BsPlusNineAs => new string('b', Math.Max(0, lengthBytes - 9))
                    + new string('a', Math.Min(9, lengthBytes)),
                AdversarialInputKind.HeaderNoEquals => new string('h', lengthBytes),
                AdversarialInputKind.Ab1Groups => string.Concat(Enumerable.Repeat("ab1", Math.Max(1, lengthBytes / 3))),
                _ => throw new InvalidOperationException($"Unknown input kind {InputKind}."),
            };
            return Encoding.UTF8.GetBytes(text);
        }
    }

    /// <summary>
    /// The pattern catalog for issue #103: every pattern from the
    /// two-phase regex architecture article's ReDoS table, plus additional
    /// pathological patterns from the classic Davis et al. vulnerability
    /// corpus (nested quantifiers, overlapping alternation), plus the
    /// supported-subset analogues used for the linear-scaling validation.
    /// </summary>
    public static class RedosPatternCatalog
    {
        /// <summary>Smallest adversarial length: 1 KB.</summary>
        public const int SmallLengthBytes = 1_024;

        /// <summary>Middle adversarial length: 10 KB.</summary>
        public const int MediumLengthBytes = 10_240;

        /// <summary>Largest adversarial length: 100 KB.</summary>
        public const int LargeLengthBytes = 102_400;

        /// <summary>
        /// The article and corpus patterns. None of them is supported by
        /// the forward-only StepLexer matcher: every one contains a group,
        /// alternation or anchor, and is rejected at the first position.
        /// </summary>
        public static readonly IReadOnlyList<RedosPattern> ArticleAndCorpusPatterns = new RedosPattern[]
        {
            new RedosPattern
            {
                Name = "Article: (a+)+",
                Source = "Article ReDoS table",
                CorePattern = "(a+)+",
                Family = "nested quantifier",
                InputKind = AdversarialInputKind.AsTrailingBang,
                BaselineLengthBytes = SmallLengthBytes,
                SupportedByStepLexer = false,
                ExpectedBacktrackingBlowup = false,
                Note = "Unanchored (a+)+ matches the a-run prefix instantly in every engine; the blowup requires anchoring (see Davis (a+)+$). Measured as cited in the article.",
            },
            new RedosPattern
            {
                Name = "Article: (a*)*b",
                Source = "Article ReDoS table",
                CorePattern = "(a*)*b",
                Family = "nested quantifier",
                InputKind = AdversarialInputKind.AllAs,
                BaselineLengthBytes = SmallLengthBytes,
                SupportedByStepLexer = false,
                ExpectedBacktrackingBlowup = true,
            },
            new RedosPattern
            {
                Name = "Article: (a|a)*",
                Source = "Article ReDoS table",
                CorePattern = "(a|a)*",
                Family = "overlapping alternation",
                InputKind = AdversarialInputKind.AsTrailingBang,
                BaselineLengthBytes = SmallLengthBytes,
                SupportedByStepLexer = false,
                ExpectedBacktrackingBlowup = false,
                Note = "Unanchored (a|a)* matches the a-run prefix instantly in every engine; the blowup requires anchoring (see Davis (a|aa)+$). Measured as cited in the article.",
            },
            new RedosPattern
            {
                Name = "Article: (.*a){10}",
                Source = "Article ReDoS table",
                CorePattern = "(.*a){10}",
                Family = "bounded nested quantifier",
                InputKind = AdversarialInputKind.BsPlusNineAs,
                BaselineLengthBytes = LargeLengthBytes,
                SupportedByStepLexer = false,
                ExpectedBacktrackingBlowup = true,
            },
            new RedosPattern
            {
                Name = "Article: .*(?:.*=.*) (Cloudflare-style)",
                Source = "Article ReDoS table",
                CorePattern = ".*(?:.*=.*)",
                Family = "overlapping dot-star",
                InputKind = AdversarialInputKind.HeaderNoEquals,
                BaselineLengthBytes = LargeLengthBytes,
                SupportedByStepLexer = false,
                ExpectedBacktrackingBlowup = true,
            },
            new RedosPattern
            {
                Name = "Article: Stack Overflow-style e-mail validation",
                Source = "Article ReDoS table",
                CorePattern = @"^([\w\-.]+)*@([\w\-.]+)$",
                Family = "nested quantifier in validation",
                InputKind = AdversarialInputKind.AsTrailingBang,
                BaselineLengthBytes = SmallLengthBytes,
                SupportedByStepLexer = false,
                ExpectedBacktrackingBlowup = true,
            },
            new RedosPattern
            {
                Name = "Davis corpus: (a+)+$",
                Source = "Davis et al. vulnerability corpus",
                CorePattern = "(a+)+$",
                Family = "nested quantifier",
                InputKind = AdversarialInputKind.AsTrailingBang,
                BaselineLengthBytes = SmallLengthBytes,
                SupportedByStepLexer = false,
                ExpectedBacktrackingBlowup = true,
            },
            new RedosPattern
            {
                Name = "Davis corpus: (a|aa)+$",
                Source = "Davis et al. vulnerability corpus",
                CorePattern = "(a|aa)+$",
                Family = "overlapping alternation",
                InputKind = AdversarialInputKind.AsTrailingBang,
                BaselineLengthBytes = SmallLengthBytes,
                SupportedByStepLexer = false,
                ExpectedBacktrackingBlowup = true,
            },
            new RedosPattern
            {
                Name = "Davis corpus: ([a-zA-Z]+)*$",
                Source = "Davis et al. vulnerability corpus",
                CorePattern = "([a-zA-Z]+)*$",
                Family = "nested quantifier",
                InputKind = AdversarialInputKind.AsTrailingBang,
                BaselineLengthBytes = SmallLengthBytes,
                SupportedByStepLexer = false,
                ExpectedBacktrackingBlowup = true,
            },
            new RedosPattern
            {
                Name = "Davis corpus: (x+x+)+y",
                Source = "Davis et al. vulnerability corpus",
                CorePattern = "(x+x+)+y",
                Family = "nested quantifier",
                InputKind = AdversarialInputKind.AllXs,
                BaselineLengthBytes = SmallLengthBytes,
                SupportedByStepLexer = false,
                ExpectedBacktrackingBlowup = true,
            },
            new RedosPattern
            {
                Name = "Davis corpus: (a+)+b",
                Source = "Davis et al. vulnerability corpus",
                CorePattern = "(a+)+b",
                Family = "nested quantifier",
                InputKind = AdversarialInputKind.AllAs,
                BaselineLengthBytes = SmallLengthBytes,
                SupportedByStepLexer = false,
                ExpectedBacktrackingBlowup = true,
            },
            new RedosPattern
            {
                Name = "Davis corpus: (a|b|ab)*c",
                Source = "Davis et al. vulnerability corpus",
                CorePattern = "(a|b|ab)*c",
                Family = "overlapping alternation",
                InputKind = AdversarialInputKind.AllAs,
                BaselineLengthBytes = LargeLengthBytes,
                SupportedByStepLexer = false,
                ExpectedBacktrackingBlowup = true,
            },
        };

        /// <summary>
        /// Supported-subset analogues of the pathological families, used
        /// for the linear-scaling validation (1 KB -> 100 KB): the same
        /// greedy atoms and unbounded quantifiers the article patterns
        /// use, in the form the StepLexer matcher actually evaluates.
        /// These are the worst cases a forward-only matcher can face: a
        /// full-input greedy scan followed by a failed next atom.
        /// </summary>
        public static readonly IReadOnlyList<RedosPattern> ScalingPatterns = new RedosPattern[]
        {
            new RedosPattern
            {
                Name = "Scaling: [a]+ full run",
                Source = "Supported analogue of (a+)+",
                CorePattern = "[a]+",
                Family = "greedy run",
                InputKind = AdversarialInputKind.AllAs,
                BaselineLengthBytes = LargeLengthBytes,
                SupportedByStepLexer = true,
                ExpectedBacktrackingBlowup = false,
            },
            new RedosPattern
            {
                Name = "Scaling: [a]*b full-scan no-match",
                Source = "Supported analogue of (a*)*b",
                CorePattern = "[a]*b",
                Family = "greedy full scan, fail",
                InputKind = AdversarialInputKind.AllAs,
                BaselineLengthBytes = LargeLengthBytes,
                SupportedByStepLexer = true,
                ExpectedBacktrackingBlowup = false,
            },
            new RedosPattern
            {
                Name = "Scaling: [ab]* full run",
                Source = "Supported class alternation analogue",
                CorePattern = "[ab]*",
                Family = "greedy run",
                InputKind = AdversarialInputKind.AllAs,
                BaselineLengthBytes = LargeLengthBytes,
                SupportedByStepLexer = true,
                ExpectedBacktrackingBlowup = false,
            },
            new RedosPattern
            {
                Name = "Scaling: [^x]* negated full run",
                Source = "Supported negated-class analogue",
                CorePattern = "[^x]*",
                Family = "greedy run",
                InputKind = AdversarialInputKind.AllAs,
                BaselineLengthBytes = LargeLengthBytes,
                SupportedByStepLexer = true,
                ExpectedBacktrackingBlowup = false,
            },
            new RedosPattern
            {
                Name = "Scaling: .*b dot full-scan no-match",
                Source = "Supported analogue of .*(?:.*=.*)",
                CorePattern = ".*b",
                Family = "greedy full scan, fail",
                InputKind = AdversarialInputKind.AllAs,
                BaselineLengthBytes = LargeLengthBytes,
                SupportedByStepLexer = true,
                ExpectedBacktrackingBlowup = false,
            },
        };

        /// <summary>
        /// The many-token measurement pattern: one token per 3 bytes. Not a
        /// matching blowup (matching stays forward-only and linear); it is
        /// measured separately because per-token line/column bookkeeping
        /// is O(position), so token count - not matching - drives the
        /// cost. Measured at capped lengths only; no linear-scaling
        /// assertion applies to it.
        /// </summary>
        public static readonly RedosPattern ManyTokenPattern = new RedosPattern
        {
            Name = "Measurement: [a-z]+[0-9] many-token stream",
            Source = "Many-token tokenization overhead measurement",
            CorePattern = "[a-z]+[0-9]",
            Family = "many-token stream",
            InputKind = AdversarialInputKind.Ab1Groups,
            BaselineLengthBytes = MediumLengthBytes,
            SupportedByStepLexer = true,
            ExpectedBacktrackingBlowup = false,
        };

        /// <summary>Find an article/corpus pattern by name (test data lookup).</summary>
        public static RedosPattern FindArticleOrCorpusPattern(string name) =>
            ArticleAndCorpusPatterns.FirstOrDefault(p => p.Name == name)
            ?? throw new InvalidOperationException($"Unknown article/corpus pattern: {name}");

        /// <summary>Find a scaling pattern by name (test data lookup).</summary>
        public static RedosPattern FindScalingPattern(string name) =>
            ScalingPatterns.FirstOrDefault(p => p.Name == name)
            ?? throw new InvalidOperationException($"Unknown scaling pattern: {name}");
    }
}
