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
        [ThreadStatic]
        private static PathLabelCapture? _current;

        /// <summary>The capture attached on the current thread, if any.</summary>
        internal static PathLabelCapture? Current => _current;

        private readonly List<PathLabelRecord> _records = new();
        private readonly HashSet<int> _successfulPathIds = new();
        private int _step;

        /// <summary>
        /// Observed (parser step, path) feature vectors, in capture order.
        /// </summary>
        internal IReadOnlyList<PathLabelRecord> Records => _records;

        /// <summary>
        /// Attach a fresh capture on the current thread and return it.
        /// </summary>
        internal static PathLabelCapture Begin()
        {
            var capture = new PathLabelCapture();
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
        /// current step.
        /// </summary>
        internal void Record(int pathId, double[] features)
        {
            _records.Add(new PathLabelRecord(_step, pathId, features));
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
