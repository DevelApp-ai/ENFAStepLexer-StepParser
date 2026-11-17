using System.Collections.Generic;
using System.Threading;

namespace DevelApp.StepLexer
{
    /// <summary>
    /// Optional, harness-only capture of per-attempt rule-match labels for
    /// training the issue #74 rule prioritizer (issue #93). Not part of the
    /// public surface: the ML trace harness attaches a capture buffer for
    /// the duration of one lexing run, and <see cref="StepLexer"/> records
    /// one label per rule-evaluation attempt (the prioritizer feature
    /// vector for the (rule, next input byte) pair plus whether the attempt
    /// matched). Recording is observation-only: it changes no behavior and
    /// costs one thread-static field read per attempt while detached.
    /// </summary>
    internal static class RuleMatchLabelCapture
    {
        /// <summary>
        /// The default per-parse label budget. Pathological lexical
        /// ambiguity can generate millions of rule-evaluation attempts
        /// for a tiny input (issue #93: an unbounded buffer OOM-killed
        /// whole capture runs), so recording stops and the overflow
        /// latches once the budget is spent. The harness then discards the
        /// grammar's labels rather than training on a truncated prefix
        /// (the stored prefix is position/step-biased).
        /// </summary>
        internal const int DefaultMaxRecords = 1_000_000;

        [ThreadStatic]
        private static RuleMatchLabelBuffer? _current;

        /// <summary>
        /// Whether a capture buffer is attached on the current thread.
        /// </summary>
        internal static bool IsCapturing => _current != null;

        /// <summary>
        /// Attach a fresh capture buffer on the current thread and return it.
        /// </summary>
        /// <param name="maxRecords">
        /// The maximum number of labels the buffer accepts before it
        /// latches <see cref="RuleMatchLabelBuffer.Overflowed"/> and stops
        /// recording.
        /// </param>
        /// <returns>The buffer the lexer will record into.</returns>
        internal static RuleMatchLabelBuffer Begin(int maxRecords = DefaultMaxRecords)
        {
            var buffer = new RuleMatchLabelBuffer(maxRecords);
            _current = buffer;
            return buffer;
        }

        /// <summary>Detach the capture buffer on the current thread.</summary>
        internal static void End()
        {
            _current = null;
        }

        /// <summary>
        /// Record one rule-evaluation attempt into the attached buffer.
        /// No-op unless a buffer is attached.
        /// </summary>
        internal static void Record(TokenRule rule, byte firstByte, bool matched)
        {
            _current?.Record(rule, firstByte, matched);
        }
    }

    /// <summary>
    /// Buffer of rule-evaluation attempts observed by the trace harness.
    /// The budget is checked before features are extracted, so an
    /// overflowing buffer costs nothing beyond the check itself.
    /// </summary>
    internal sealed class RuleMatchLabelBuffer
    {
        private readonly List<RuleMatchLabelRecord> _records = new();

        /// <summary>Initializes a new buffer.</summary>
        /// <param name="maxRecords">The record budget.</param>
        internal RuleMatchLabelBuffer(int maxRecords)
        {
            MaxRecords = maxRecords;
        }

        /// <summary>The maximum number of labels this buffer accepts.</summary>
        internal int MaxRecords { get; }

        /// <summary>
        /// Whether the budget was exhausted and recording stopped. The
        /// flag latches: once set, further records are ignored.
        /// </summary>
        internal bool Overflowed { get; private set; }

        /// <summary>The observed labels, in capture order.</summary>
        internal IReadOnlyList<RuleMatchLabelRecord> Records => _records;

        /// <summary>
        /// Record one rule-evaluation attempt: the model feature vector for
        /// the (rule, next input byte) pair and whether the attempt matched.
        /// Stops and latches overflow once the budget is spent.
        /// </summary>
        internal void Record(TokenRule rule, byte firstByte, bool matched)
        {
            if (Overflowed)
            {
                return;
            }

            if (_records.Count >= MaxRecords)
            {
                Overflowed = true;
                return;
            }

            _records.Add(new RuleMatchLabelRecord(
                LearnedRulePrioritizer.ExtractFeatures(rule, firstByte), matched));
        }
    }

    /// <summary>
    /// One rule-evaluation attempt observed by the trace harness: the
    /// prioritizer feature vector
    /// (<see cref="LearnedRulePrioritizer.FeatureCount"/> entries) and
    /// whether the attempted rule matched at the position.
    /// </summary>
    internal readonly struct RuleMatchLabelRecord
    {
        /// <summary>
        /// The prioritizer feature vector (see
        /// <c>LearnedRulePrioritizer.ExtractFeatures</c>).
        /// </summary>
        public double[] Features { get; }

        /// <summary>Whether the attempted rule matched at the position.</summary>
        public bool Matched { get; }

        /// <summary>Initializes a new record.</summary>
        /// <param name="features">The prioritizer feature vector.</param>
        /// <param name="matched">Whether the attempt matched.</param>
        public RuleMatchLabelRecord(double[] features, bool matched)
        {
            Features = features;
            Matched = matched;
        }
    }
}
