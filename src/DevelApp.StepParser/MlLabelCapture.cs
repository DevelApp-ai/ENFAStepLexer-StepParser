using System.Collections.Generic;
using System.Threading;

namespace DevelApp.StepParser
{
    /// <summary>
    /// Optional, harness-only capture of per-step GLR path observations for
    /// training the issue #75 path pruner (issue #93). Not part of the
    /// public surface: the ML trace harness attaches a capture for the
    /// duration of one parse. <see cref="StepParser"/> records, at every
    /// step whose active path count is above the deterministic path budget
    /// (the regime the learned pruner is consulted in), the pruner feature
    /// vector of every active path keyed by path id, and the path ids that
    /// ultimately succeeded are marked when the parse completes. Recording
    /// is observation-only: paths, ordering, scores and pruning are
    /// untouched.
    /// <para>
    /// Captures can be bounded: <see cref="Begin(int)"/> keeps a uniform,
    /// deterministic sample of at most the given number of observations,
    /// mirroring <see cref="RuleMatchLabelCapture"/> (issue #93
    /// corpus-scale captures).
    /// </para>
    /// </summary>
    internal sealed class PathLabelCapture
    {
        [ThreadStatic]
        private static PathLabelCapture? _current;

        /// <summary>The capture attached on the current thread, if any.</summary>
        internal static PathLabelCapture? Current => _current;

        private readonly List<PathLabelRecord> _records = new();
        private readonly HashSet<int> _successfulPathIds = new();
        private int _step;
        private readonly int _capacity;
        private long _observed;
        private ulong _rngState;

        private PathLabelCapture(int maxRecords)
        {
            _capacity = maxRecords;
            _rngState = 0x9E3779B97F4A7C15UL; // fixed seed: samples are reproducible
        }

        /// <summary>
        /// Observed (parser step, path) feature vectors, in capture order.
        /// </summary>
        internal IReadOnlyList<PathLabelRecord> Records => _records;

        /// <summary>
        /// Total number of (parser step, path) observations seen, kept and
        /// dropped alike (identical whether or not the capture is bounded).
        /// </summary>
        internal long ObservedRecords => _observed;

        /// <summary>
        /// Attach a fresh, uncapped capture on the current thread and
        /// return it: every observation is kept.
        /// </summary>
        internal static PathLabelCapture Begin()
        {
            return Begin(0);
        }

        /// <summary>
        /// Attach a fresh capture on the current thread and return it. When
        /// <paramref name="maxRecords"/> is positive the capture is bounded:
        /// the first <paramref name="maxRecords"/> observations are kept and
        /// every later observation replaces a uniformly chosen kept record
        /// (reservoir sampling, Vitter's algorithm R driven by a fixed-seed
        /// LCG), so the final records are a uniform, deterministic sample
        /// of all observations. Over-budget regimes of ambiguous grammars
        /// generate millions of (step, path) observations per parse; the
        /// bound keeps capture memory and time proportional to the sample
        /// size instead of the observation count.
        /// </summary>
        /// <param name="maxRecords">
        /// Maximum number of records to keep; 0 keeps every observation.
        /// </param>
        /// <returns>The capture the parser records into.</returns>
        internal static PathLabelCapture Begin(int maxRecords)
        {
            var capture = new PathLabelCapture(maxRecords);
            _current = capture;
            return capture;
        }

        /// <summary>Detach the capture from the current thread.</summary>
        internal static void End()
        {
            _current = null;
        }

        /// <summary>
        /// Record the pruner feature vector of one active path at the
        /// current step. When the capture is bounded, observations past the
        /// capacity replace a uniformly chosen kept record (reservoir
        /// sampling).
        /// </summary>
        internal void Record(int pathId, double[] features)
        {
            if (_capacity <= 0)
            {
                _records.Add(new PathLabelRecord(_step, pathId, features));
                _observed++;
                return;
            }

            var index = _observed++;
            if (index < _capacity)
            {
                _records.Add(new PathLabelRecord(_step, pathId, features));
            }
            else if (NextUniform() < (double)_capacity / (index + 1))
            {
                _records[(int)(NextUniform() * _capacity)] =
                    new PathLabelRecord(_step, pathId, features);
            }
        }

        /// <summary>
        /// Uniform value in [0, 1) from a fixed-seed LCG (Knuth
        /// multiplicative constants): bounded captures are deterministic
        /// and reproducible.
        /// </summary>
        private double NextUniform()
        {
            _rngState = _rngState * 6364136223846793005UL + 1442695040888963407UL;
            return (_rngState >> 11) * (1.0 / (1UL << 53));
        }

        /// <summary>Advance the step counter (once per parser step).</summary>
        internal void NextStep()
        {
            _step++;
        }

        /// <summary>
        /// Mark the ids of the paths that ultimately succeeded. Called by
        /// <see cref="StepParser"/> when the parse reaches end of input.
        /// </summary>
        internal void MarkSuccessful(IEnumerable<int> successfulPathIds)
        {
            _successfulPathIds.Clear();
            foreach (var id in successfulPathIds)
            {
                _successfulPathIds.Add(id);
            }
        }

        /// <summary>Whether the given path id ultimately succeeded.</summary>
        internal bool Succeeded(int pathId)
        {
            return _successfulPathIds.Contains(pathId);
        }
    }

    /// <summary>
    /// One (parser step, path) observation: the pruner feature vector
    /// (<see cref="LearnedPathPruner.FeatureCount"/> entries) of an active
    /// path at one step of the parse.
    /// </summary>
    internal readonly struct PathLabelRecord
    {
        /// <summary>Parser step index (0-based).</summary>
        public int Step { get; }

        /// <summary>The observed path's id.</summary>
        public int PathId { get; }

        /// <summary>The pruner feature vector.</summary>
        public double[] Features { get; }

        /// <summary>Initializes a new record.</summary>
        /// <param name="step">Parser step index.</param>
        /// <param name="pathId">The observed path's id.</param>
        /// <param name="features">The pruner feature vector.</param>
        public PathLabelRecord(int step, int pathId, double[] features)
        {
            Step = step;
            PathId = pathId;
            Features = features;
        }
    }
}
