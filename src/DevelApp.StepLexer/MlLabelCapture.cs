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
    /// <para>
    /// A capture can be bounded (<see cref="Begin(int)"/> with a positive
    /// <c>maxRecords</c>): the buffer then holds a uniform, deterministic
    /// sample of the attempts (reservoir sampling), which keeps
    /// corpus-scale captures (issue #93) bounded in memory and time.
    /// Ambiguous grammars generate millions of attempts per example; a
    /// uniform bounded sample of those attempts is statistically
    /// equivalent for training a linear model while preventing
    /// test-host exhaustion.
    /// </para>
    /// </summary>
    internal static class RuleMatchLabelCapture
    {
        [ThreadStatic]
        private static List<RuleMatchLabelRecord>? _current;

        [ThreadStatic]
        private static int _capacity;

        [ThreadStatic]
        private static long _observed;

        [ThreadStatic]
        private static ulong _rngState;

        /// <summary>
        /// Whether a capture buffer is attached on the current thread.
        /// </summary>
        internal static bool IsCapturing => _current != null;

        /// <summary>
        /// Number of rule-evaluation attempts observed by the capture
        /// currently attached on this thread (kept and dropped); 0 when no
        /// capture is attached.
        /// </summary>
        internal static long ObservedAttempts => _observed;

        /// <summary>
        /// Attach a fresh, uncapped capture buffer on the current thread and
        /// return it (every attempt is kept).
        /// </summary>
        /// <returns>The buffer the lexer will record into.</returns>
        internal static List<RuleMatchLabelRecord> Begin()
        {
            return Begin(0);
        }

        /// <summary>
        /// Attach a fresh capture buffer on the current thread and return
        /// it. With <paramref name="maxRecords"/> greater than zero the
        /// buffer is capped: the first <paramref name="maxRecords"/> attempts
        /// are kept, every later attempt replaces a uniformly chosen slot
        /// with probability <c>maxRecords / attemptsSoFar</c> (reservoir
        /// sampling, Vitter's algorithm R driven by a fixed-seed LCG), so
        /// the final buffer is a uniform, deterministic sample of all
        /// attempts.
        /// </summary>
        /// <param name="maxRecords">
        /// Maximum number of records to keep; 0 or negative keeps every
        /// attempt (uncapped).
        /// </param>
        /// <returns>The buffer the lexer will record into.</returns>
        internal static List<RuleMatchLabelRecord> Begin(int maxRecords)
        {
            var records = new List<RuleMatchLabelRecord>();
            _current = records;
            _capacity = maxRecords;
            _observed = 0;
            _rngState = 0x9E3779B97F4A7C15UL; // fixed seed: samples are reproducible
            return records;
        }

        /// <summary>Detach the capture buffer on the current thread.</summary>
        internal static void End()
        {
            _current = null;
            _capacity = 0;
            _observed = 0;
        }

        /// <summary>
        /// Record one rule-evaluation attempt: the model feature vector for
        /// the (rule, next input byte) pair and whether the attempt matched.
        /// No-op unless a buffer is attached. Under a capped capture the
        /// keep/replace decision is made before any feature extraction, so
        /// dropped attempts cost one LCG step only.
        /// </summary>
        internal static void Record(TokenRule rule, byte firstByte, bool matched)
        {
            var buffer = _current;
            if (buffer == null)
            {
                return;
            }

            var capacity = _capacity;
            if (capacity <= 0)
            {
                buffer.Add(new RuleMatchLabelRecord(
                    LearnedRulePrioritizer.ExtractFeatures(rule, firstByte), matched));
                _observed++;
                return;
            }

            var index = _observed++;
            if (index < capacity)
            {
                buffer.Add(new RuleMatchLabelRecord(
                    LearnedRulePrioritizer.ExtractFeatures(rule, firstByte), matched));
            }
            else if (NextUniform() < (double)capacity / (index + 1))
            {
                var slot = (int)(NextUniform() * capacity);
                buffer[slot] = new RuleMatchLabelRecord(
                    LearnedRulePrioritizer.ExtractFeatures(rule, firstByte), matched);
            }
        }

        /// <summary>
        /// Next uniform value in [0, 1) from the capture's deterministic
        /// LCG (64-bit state, 53-bit output).
        /// </summary>
        private static double NextUniform()
        {
            _rngState = unchecked(
                _rngState * 6364136223846793005UL + 1442695040888963407UL);
            return (_rngState >> 11) * (1.0 / (1UL << 53));
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
