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
    /// </summary>
    internal sealed class PathLabelCapture
    {
        /// <summary>
        /// The default per-parse label budget. Pathological grammars can
        /// keep the live path count above the deterministic budget for
        /// thousands of steps (issue #93: unbounded buffering OOM-killed
        /// whole capture runs), so recording stops and the overflow
        /// latches once the budget is spent. The harness then discards
        /// the grammar's labels rather than training on a truncated,
        /// step-biased prefix.
        /// </summary>
        internal const int DefaultMaxRecords = 1_000_000;

        [ThreadStatic]
        private static PathLabelCapture? _current;

        /// <summary>The capture attached on the current thread, if any.</summary>
        internal static PathLabelCapture? Current => _current;

        private readonly List<PathLabelRecord> _records = new();
        private readonly HashSet<int> _successfulPathIds = new();
        private int _step;

        private PathLabelCapture(int maxRecords)
        {
            MaxRecords = maxRecords;
        }

        /// <summary>The maximum number of labels this capture accepts.</summary>
        internal int MaxRecords { get; }

        /// <summary>
        /// Whether the budget was exhausted and recording stopped. The
        /// flag latches: once set, further records are ignored.
        /// </summary>
        internal bool Overflowed { get; private set; }

        /// <summary>
        /// Observed (parser step, path) feature vectors, in capture order.
        /// </summary>
        internal IReadOnlyList<PathLabelRecord> Records => _records;

        /// <summary>
        /// Attach a fresh capture on the current thread and return it.
        /// </summary>
        /// <param name="maxRecords">
        /// The maximum number of labels the capture accepts before it
        /// latches <see cref="Overflowed"/> and stops recording.
        /// </param>
        internal static PathLabelCapture Begin(int maxRecords = DefaultMaxRecords)
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
        /// current step, keyed by the observed path's id. Stops and
        /// latches overflow once the budget is spent.
        /// </summary>
        internal void Record(int pathId, double[] features)
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

            _records.Add(new PathLabelRecord(_step, pathId, features));
        }

        /// <summary>Advance the step counter (once per parser step).</summary>
        internal void NextStep()
        {
            _step++;
        }

        /// <summary>
        /// Mark the ids of the paths that ultimately succeeded: each
        /// end-of-input survivor's own id plus its ancestry chain (the
        /// object ids it was cloned from), because per-branch-minted ids
        /// alone never survive to end of input. Called by
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

        /// <summary>
        /// Whether a completion of the parse descended from the given
        /// observed path id (directly or via clones).
        /// </summary>
        internal bool Succeeded(int pathId)
        {
            return _successfulPathIds.Contains(pathId);
        }
    }

    /// <summary>
    /// One (parser step, path) observation: the pruner feature vector
    /// (<see cref="LearnedPathPruner.FeatureCount"/> entries) of an active
    /// path at one step of the parse. The record is positive when a
    /// completion of the parse descended from the observed path — the
    /// end-of-input marking walks each surviving path's ancestry chain, so
    /// observations of both the survivor and the objects it was cloned
    /// from turn positive while doomed clone siblings stay negative.
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
