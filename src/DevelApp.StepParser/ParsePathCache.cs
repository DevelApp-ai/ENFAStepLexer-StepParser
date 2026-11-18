using System;
using System.Collections.Generic;
using System.Linq;

namespace DevelApp.StepParser
{
    /// <summary>
    /// Cache of parser path-set snapshots keyed by token boundary
    /// (issue #101, Selective Path Invalidation). A snapshot taken after
    /// the step that consumed token <c>boundary - 1</c> is a valid resume
    /// point for any later parse whose token stream shares the prefix
    /// <c>[0, boundary)</c>: the persistent stacked state of a
    /// <see cref="ParserPath"/> is immutable, so a stored snapshot can be
    /// restored by cloning once per path.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Snapshots are kept for every token boundary while the document is
    /// small. When the number of stored snapshots exceeds
    /// <see cref="Capacity"/>, the cache thins its history adaptively:
    /// the oldest half of the retained snapshots is decimated (every
    /// second snapshot dropped), so recent boundaries stay dense while the
    /// distant past keeps exponentially sparser resume points. This keeps
    /// the memory bounded by roughly
    /// <c>Capacity * MaxActivePaths * O(1)</c> stack cells, because path
    /// cloning shares the immutable stack spines instead of copying them.
    /// </para>
    /// <para>
    /// Selective invalidation removes only snapshots at or after a token
    /// boundary: <see cref="InvalidateFrom"/>. Snapshots before the impact
    /// boundary remain valid because their token prefix is unchanged.
    /// </para>
    /// </remarks>
    public sealed class ParsePathCache
    {
        /// <summary>
        /// Maximum number of snapshots retained before adaptive
        /// decimation of the oldest half kicks in.
        /// </summary>
        public const int Capacity = 256;

        private readonly List<(int Boundary, List<ParserPath> Paths)> _entries = new();

        /// <summary>
        /// Gets the number of token boundaries currently cached.
        /// </summary>
        public int Count => _entries.Count;

        /// <summary>
        /// Gets the total number of cached parser paths across all
        /// stored boundaries.
        /// </summary>
        public int CachedPathCount => _entries.Sum(e => e.Paths.Count);

        /// <summary>
        /// Store a snapshot of the parser state after the step that
        /// consumed token <paramref name="boundary"/> - 1.
        /// </summary>
        /// <param name="boundary">
        /// Next unconsumed token index: tokens <c>[0, boundary)</c> have
        /// been consumed by the paths being snapshotted.
        /// </param>
        /// <param name="paths">
        /// The active parser paths at this boundary. They are cloned once
        /// so that later stepping never mutates the cached state.
        /// </param>
        public void Store(int boundary, IReadOnlyList<ParserPath> paths)
        {
            if (paths == null || paths.Count == 0)
            {
                return;
            }

            var snapshot = new List<ParserPath>(paths.Count);
            foreach (var path in paths)
            {
                // Snapshot clones keep their source PathId: path ids are
                // diagnostics/labels only and never take part in merging
                // or pruning decisions.
                snapshot.Add(path.Clone(path.PathId));
            }

            if (_entries.Count > 0 && _entries[^1].Boundary == boundary)
            {
                _entries[^1] = (boundary, snapshot);
            }
            else
            {
                _entries.Add((boundary, snapshot));
            }

            if (_entries.Count > Capacity)
            {
                DecimateOldestHalf();
            }
        }

        /// <summary>
        /// Find the cached resume point closest to (and not after) the
        /// given token boundary.
        /// </summary>
        /// <param name="boundary">
        /// Token boundary the incremental re-parse wants to resume from.
        /// </param>
        /// <param name="resumeBoundary">
        /// The cached boundary found: tokens <c>[0, resumeBoundary)</c>
        /// were consumed by its paths.
        /// </param>
        /// <param name="paths">
        /// The cached paths at <paramref name="resumeBoundary"/>. The
        /// caller must clone them before restoring (snapshots must stay
        /// pristine; see <see cref="RestoreClone"/>).
        /// </param>
        /// <returns>
        /// True when a resume point at or below the requested boundary
        /// exists; false when the cache has no valid snapshot (cold start).
        /// </returns>
        public bool TryGetNearestAtOrBelow(int boundary, out int resumeBoundary, out IReadOnlyList<ParserPath> paths)
        {
            resumeBoundary = -1;
            paths = Array.Empty<ParserPath>();

            var index = FindIndexAtOrBelow(boundary);
            if (index < 0)
            {
                return false;
            }

            var entry = _entries[index];
            resumeBoundary = entry.Boundary;
            paths = entry.Paths;
            return true;
        }

        /// <summary>
        /// Clone a cached snapshot so the step engine can restore it
        /// without mutating the store: the clones share the immutable
        /// stack spines of the snapshot clones, so this is O(paths), not
        /// O(stack depth).
        /// </summary>
        /// <param name="paths">A snapshot returned by the cache.</param>
        /// <returns>Steppable clones of the snapshot paths.</returns>
        public static List<ParserPath> RestoreClone(IReadOnlyList<ParserPath> paths)
        {
            var restored = new List<ParserPath>(paths.Count);
            foreach (var path in paths)
            {
                restored.Add(path.Clone(path.PathId));
            }

            return restored;
        }

        /// <summary>
        /// Selective invalidation (issue #101): drop every snapshot at or
        /// after the impact boundary. Snapshots below it remain valid
        /// because their consumed token prefix is unchanged by the edit.
        /// </summary>
        /// <param name="boundary">
        /// First token boundary potentially affected by an edit.
        /// </param>
        public void InvalidateFrom(int boundary)
        {
            int index = _entries.Count;
            while (index > 0 && _entries[index - 1].Boundary >= boundary)
            {
                index--;
            }

            if (index < _entries.Count)
            {
                _entries.RemoveRange(index, _entries.Count - index);
            }
        }

        /// <summary>
        /// Drop all snapshots (used when the token stream is replaced
        /// wholesale, e.g. a new document).
        /// </summary>
        public void Clear()
        {
            _entries.Clear();
        }

        /// <summary>
        /// Binary search for the newest entry whose boundary is at or
        /// below the requested boundary. Entries are strictly increasing
        /// by boundary.
        /// </summary>
        /// <param name="boundary">Requested resume boundary.</param>
        /// <returns>Index of the nearest entry at or below, or -1.</returns>
        private int FindIndexAtOrBelow(int boundary)
        {
            int lo = 0;
            int hi = _entries.Count - 1;
            int best = -1;
            while (lo <= hi)
            {
                int mid = lo + (hi - lo) / 2;
                if (_entries[mid].Boundary <= boundary)
                {
                    best = mid;
                    lo = mid + 1;
                }
                else
                {
                    hi = mid - 1;
                }
            }

            return best;
        }

        /// <summary>
        /// Adaptive decimation: drop every second snapshot in the oldest
        /// half of the history. Recent boundaries stay dense (every
        /// token), while older history keeps sparser resume points,
        /// mirroring the hierarchical cache granularity of the article's
        /// design (recent: dense, distant: sparse).
        /// </summary>
        private void DecimateOldestHalf()
        {
            int half = _entries.Count / 2;
            var retained = new List<(int, List<ParserPath>)>(_entries.Count);
            for (int i = 0; i < _entries.Count; i++)
            {
                // Keep the even-indexed entries of the oldest half and
                // every entry of the newer half (which is dense recent
                // history).
                if (i >= half || i % 2 == 0)
                {
                    retained.Add(_entries[i]);
                }
            }

            _entries.Clear();
            _entries.AddRange(retained);
        }
    }
}
