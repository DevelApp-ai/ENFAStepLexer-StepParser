using System;

namespace DevelApp.StepParser
{
    /// <summary>
    /// Outcome of the edit impact analysis phase of Selective Path
    /// Invalidation (issue #101): the token boundary at which cached
    /// parser state first becomes suspect.
    /// </summary>
    /// <remarks>
    /// The impact boundary is expressed in token stream indexes:
    /// tokens <c>[0, Boundary)</c> are unchanged by the edit (they were
    /// reused by identity from the pre-edit token list), so parser path
    /// snapshots that consumed exactly those tokens remain valid. Every
    /// cached snapshot at or after <see cref="FirstAffectedTokenBoundary"/>
    /// is invalidated and the incremental re-parse resumes from the
    /// nearest surviving snapshot.
    /// </remarks>
    public sealed class EditImpact
    {
        /// <summary>
        /// Gets the first token index affected by the edit: the token
        /// whose source span ends at or after the edit offset. Cached
        /// resume points below this boundary are still valid.
        /// </summary>
        public int FirstAffectedTokenBoundary { get; }

        /// <summary>
        /// Gets the byte offset at which the edit starts.
        /// </summary>
        public int EditOffset { get; }

        /// <summary>
        /// Gets the byte length change caused by the edit.
        /// </summary>
        public int ByteDelta { get; }

        /// <summary>
        /// Initializes a new impact description.
        /// </summary>
        /// <param name="firstAffectedTokenBoundary">First affected token index.</param>
        /// <param name="editOffset">Byte offset of the edit.</param>
        /// <param name="byteDelta">Byte length change of the edit.</param>
        public EditImpact(int firstAffectedTokenBoundary, int editOffset, int byteDelta)
        {
            FirstAffectedTokenBoundary = firstAffectedTokenBoundary;
            EditOffset = editOffset;
            ByteDelta = byteDelta;
        }
    }

    /// <summary>
    /// Phase 1 of Selective Path Invalidation (issue #101): determine
    /// which cached parse state an edit can affect.
    /// </summary>
    public static class EditImpactAnalyzer
    {
        /// <summary>
        /// Analyze the impact of an edit on the parse path cache.
        /// </summary>
        /// <param name="firstAffectedTokenIndex">
        /// First token index whose span ends at or after the edit offset,
        /// as computed by the token-level incremental lex (the session's
        /// <c>firstAffected</c>). Tokens before it are unchanged.
        /// </param>
        /// <param name="editOffset">Byte offset of the edit.</param>
        /// <param name="byteDelta">Byte length change of the edit.</param>
        /// <returns>The impact region for the cache invalidation phase.</returns>
        public static EditImpact Analyze(int firstAffectedTokenIndex, int editOffset, int byteDelta)
        {
            return new EditImpact(Math.Max(0, firstAffectedTokenIndex), editOffset, byteDelta);
        }
    }
}
