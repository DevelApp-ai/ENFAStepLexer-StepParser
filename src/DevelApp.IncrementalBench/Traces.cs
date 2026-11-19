namespace DevelApp.IncrementalBench
{
    /// <summary>
    /// The four edit types of the issue #101 protocol, at the exact
    /// granularity the article's cache-hit table uses: single-character
    /// insert, word completion (extend the identifier under the caret),
    /// line insertion, block paste (20 statement lines).
    /// </summary>
    public enum EditType
    {
        CharInsert,
        WordCompletion,
        LineInsertion,
        BlockPaste,
    }

    /// <summary>
    /// One deterministic edit: <paramref name="Offset"/> and
    /// <paramref name="DeleteLength"/> are absolute byte positions in the
    /// current text (the trace is byte-based, ASCII documents only, so
    /// bytes == chars).
    /// </summary>
    /// <param name="Type">Edit type.</param>
    /// <param name="Offset">Insertion offset in the current text.</param>
    /// <param name="DeleteLength">Deleted byte length (always 0 here; the
    /// protocol's four edit types are insertions).</param>
    /// <param name="InsertText">The inserted bytes.</param>
    public readonly record struct Edit(EditType Type, int Offset, int DeleteLength, string InsertText);

    /// <summary>
    /// Deterministic edit-trace generation. A trace is a pure function of
    /// (document, scenario, parameters): every arm replays the identical
    /// <see cref="Edit"/> sequence, so arm latencies are directly
    /// comparable. Relative positions are lowered to byte offsets in the
    /// current text, so the text evolution is identical across runs,
    /// arms, and machines.
    /// </summary>
    public static class Traces
    {
        /// <summary>Size of a block paste, in statement lines.</summary>
        public const int BlockPasteLines = 20;

        /// <summary>
        /// The edit for one cell of the per-edit table: a single edit of
        /// the given type at the given relative position of the current
        /// (initial) document.
        /// </summary>
        public static Edit SingleEdit(string text, EditType type, double relativePosition)
        {
            var offset = PositionFor(text, relativePosition);
            return MakeEdit(type, offset, 0);
        }

        /// <summary>
        /// Sequential trace for session-style scenarios (hit-rate traces
        /// and the cold-start curve). Positions follow a fixed oscillating
        /// schedule inside [10%, 90%] of the current text; the exact
        /// offsets are derived from the current text so all arms replay
        /// identical edits as the document grows.
        /// </summary>
        public static List<Edit> Sequential(string initialText, EditType type, int count, int seed)
        {
            var edits = new List<Edit>(count);
            var text = initialText;
            var rng = new Random(seed);
            for (int i = 0; i < count; i++)
            {
                // Oscillate deterministically around a seeded base point;
                // the same seed always yields the same schedule.
                var rel = 0.10 + 0.80 * (0.5 + 0.5 * Math.Sin(seed + i * 1.7))
                        + (rng.NextDouble() - 0.5) * 0.05;
                rel = Math.Min(0.90, Math.Max(0.10, rel));
                var offset = PositionFor(text, rel);
                var edit = MakeEdit(type, offset, 0);
                edits.Add(edit);
                text = Apply(text, edit);
            }

            return edits;
        }

        /// <summary>Apply an edit to the current text (both arms must see
        /// identical document evolution).</summary>
        public static string Apply(string text, Edit edit)
        {
            return text.Remove(edit.Offset, edit.DeleteLength)
                        .Insert(edit.Offset, edit.InsertText);
        }

        private static int PositionFor(string text, double relativePosition)
        {
            var target = (int)(text.Length * relativePosition);
            return Math.Min(text.Length - 1, Math.Max(0, target));
        }

        private static Edit MakeEdit(EditType type, int offset, int version)
        {
            switch (type)
            {
                case EditType.CharInsert:
                    return new Edit(type, offset, 0, "x");
                case EditType.WordCompletion:
                    return new Edit(type, offset, 0, "bc");
                case EditType.LineInsertion:
                    return new Edit(type, offset, 0, "v999 = 1 + 2\n");
                case EditType.BlockPaste:
                    {
                        var block = new System.Text.StringBuilder();
                        for (int k = 0; k < BlockPasteLines; k++)
                        {
                            block.Append("v9").Append(k).Append(" = 3 + 4\n");
                        }
                        return new Edit(type, offset, 0, block.ToString());
                    }
                default:
                    throw new ArgumentOutOfRangeException(nameof(type));
            }
        }
    }
}
