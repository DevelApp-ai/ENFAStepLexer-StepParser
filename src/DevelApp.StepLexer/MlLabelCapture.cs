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
        [ThreadStatic]
        private static List<RuleMatchLabelRecord>? _current;

        /// <summary>
        /// Whether a capture buffer is attached on the current thread.
        /// </summary>
        internal static bool IsCapturing => _current != null;

        /// <summary>
        /// Attach a fresh capture buffer on the current thread and return it.
        /// </summary>
        /// <returns>The buffer the lexer will record into.</returns>
        internal static List<RuleMatchLabelRecord> Begin()
        {
            var records = new List<RuleMatchLabelRecord>();
            _current = records;
            return records;
        }

        /// <summary>Detach the capture buffer on the current thread.</summary>
        internal static void End()
        {
            _current = null;
        }

        /// <summary>
        /// Record one rule-evaluation attempt: the model feature vector for
        /// the (rule, next input byte) pair and whether the attempt matched.
        /// No-op unless a buffer is attached.
        /// </summary>
        internal static void Record(TokenRule rule, byte firstByte, bool matched)
        {
            _current?.Add(new RuleMatchLabelRecord(
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
