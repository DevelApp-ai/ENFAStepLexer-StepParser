using System.Collections.Generic;

namespace DevelApp.StepParser
{
    /// <summary>
    /// Conflict resolution strategy applied when the base grammar and the
    /// overlay both define a rule with the same name
    /// (see ENFAStepLexer-StepParser issue #66).
    /// </summary>
    public enum OverlayConflictResolution
    {
        /// <summary>The base grammar's rule wins; the overlay rule is discarded (conflict is reported).</summary>
        BaseWins,

        /// <summary>The overlay's rule wins; the base grammar's rule is replaced (default).</summary>
        OverlayWins,

        /// <summary>
        /// Additive merge: the overlay's rules are appended to the base
        /// grammar's rules instead of replacing them. For production rules this
        /// adds the overlay's alternatives alongside the base's alternatives;
        /// for token rules with the same name the base rule is kept (the
        /// conflict is reported).
        /// </summary>
        Additive,

        /// <summary>
        /// Priority-based merge: the rule with the higher priority/precedence
        /// wins (ties go to the overlay).
        /// </summary>
        PriorityBased
    }

    /// <summary>
    /// Result of composing a base grammar with an overlay grammar: the merged
    /// grammar plus a report of every name collision that was resolved.
    /// </summary>
    public sealed class GrammarMergeResult
    {
        /// <summary>
        /// Initializes a new instance of the GrammarMergeResult class.
        /// </summary>
        /// <param name="grammar">The merged grammar.</param>
        /// <param name="conflicts">The conflicts (name collisions) that were resolved during the merge.</param>
        public GrammarMergeResult(GrammarDefinition grammar, IReadOnlyList<string> conflicts)
        {
            Grammar = grammar;
            Conflicts = conflicts;
        }

        /// <summary>Gets the merged grammar, usable by <see cref="StepParserEngine"/>.</summary>
        public GrammarDefinition Grammar { get; }

        /// <summary>
        /// Gets every conflict (rule name defined in both the base grammar and
        /// the overlay) with a note of how it was resolved.
        /// </summary>
        public IReadOnlyList<string> Conflicts { get; }
    }
}
