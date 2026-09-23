using System;
using System.Collections.Generic;
using System.Linq;
using DevelApp.StepLexer;

namespace DevelApp.StepParser
{
    /// <summary>
    /// Grammar overlay/composition support (ENFAStepLexer-StepParser issue #66):
    /// compose an already-loaded base grammar with an overlay grammar at
    /// runtime, without authoring a derived grammar file that re-declares what
    /// it inherits.
    /// </summary>
    public partial class GrammarLoader
    {
        /// <summary>
        /// Compose a base grammar with an overlay grammar using the specified
        /// conflict resolution strategy. Neither input grammar is mutated; the
        /// merged grammar is a new <see cref="GrammarDefinition"/>.
        /// </summary>
        /// <param name="baseGrammar">The grammar to overlay onto (e.g. a target language grammar).</param>
        /// <param name="overlay">The overlay grammar whose rules are injected (e.g. Labyrinth pattern tokens).</param>
        /// <param name="conflictResolution">How name collisions between base and overlay are resolved (default: overlay wins).</param>
        /// <param name="name">Optional name for the merged grammar; defaults to the base grammar's name.</param>
        /// <returns>The merge result containing the composed grammar and the resolved conflicts.</returns>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="baseGrammar"/> or <paramref name="overlay"/> is null.</exception>
        public GrammarMergeResult ComposeGrammars(
            GrammarDefinition baseGrammar,
            GrammarDefinition overlay,
            OverlayConflictResolution conflictResolution = OverlayConflictResolution.OverlayWins,
            string? name = null)
        {
            if (baseGrammar is null)
            {
                throw new ArgumentNullException(nameof(baseGrammar));
            }

            if (overlay is null)
            {
                throw new ArgumentNullException(nameof(overlay));
            }

            var merged = CloneGrammar(baseGrammar, name);
            var conflicts = new List<string>();
            MergeOverlayInto(merged, overlay, conflictResolution, conflicts);
            return new GrammarMergeResult(merged, conflicts);
        }

        /// <summary>
        /// Compose a base grammar (given as grammar file content) with an
        /// overlay grammar (also grammar file content) using the specified
        /// conflict resolution strategy. Both contents are parsed with
        /// inheritance processing, so base and overlay may themselves use
        /// <c>Inherits:</c> declarations.
        /// </summary>
        /// <param name="baseGrammarContent">The grammar file content of the base grammar.</param>
        /// <param name="overlayGrammarContent">The grammar file content of the overlay grammar.</param>
        /// <param name="conflictResolution">How name collisions between base and overlay are resolved (default: overlay wins).</param>
        /// <param name="baseFileName">File name used for error reporting of the base grammar.</param>
        /// <param name="overlayFileName">File name used for error reporting of the overlay grammar.</param>
        /// <returns>The merge result containing the composed grammar and the resolved conflicts.</returns>
        /// <exception cref="ArgumentNullException">Thrown when either content string is null.</exception>
        public GrammarMergeResult ComposeWithOverlayContent(
            string baseGrammarContent,
            string overlayGrammarContent,
            OverlayConflictResolution conflictResolution = OverlayConflictResolution.OverlayWins,
            string baseFileName = "base.grammar",
            string overlayFileName = "overlay.grammar")
        {
            if (baseGrammarContent is null)
            {
                throw new ArgumentNullException(nameof(baseGrammarContent));
            }

            if (overlayGrammarContent is null)
            {
                throw new ArgumentNullException(nameof(overlayGrammarContent));
            }

            var baseGrammar = ParseGrammarContent(baseGrammarContent, baseFileName);
            var overlay = ParseGrammarContent(overlayGrammarContent, overlayFileName);
            return ComposeGrammars(baseGrammar, overlay, conflictResolution);
        }

        /// <summary>
        /// Create a shallow copy of a grammar with copied rule lists so the
        /// source grammar is never mutated by a merge.
        /// </summary>
        /// <param name="source">The grammar to clone.</param>
        /// <param name="name">Optional new name; defaults to the source name.</param>
        /// <returns>An independent copy of the grammar.</returns>
        private static GrammarDefinition CloneGrammar(GrammarDefinition source, string? name)
        {
            return new GrammarDefinition
            {
                Name = name ?? source.Name,
                TokenSplitter = source.TokenSplitter,
                TokenRules = source.TokenRules.ToList(),
                ProductionRules = source.ProductionRules.ToList(),
                Precedence = new Dictionary<string, int>(source.Precedence),
                Associativity = new Dictionary<string, string>(source.Associativity),
                Contexts = source.Contexts.ToList(),
                SemanticActions = new Dictionary<string, Action<GraphNodeRef, List<GraphNodeRef>, CognitiveGraph.Builder.CognitiveGraphBuilder>>(source.SemanticActions),
                Imports = source.Imports.ToList(),
                IsInheritable = source.IsInheritable,
                FormatType = source.FormatType
            };
        }

        /// <summary>
        /// Merge an overlay grammar into a target grammar in place, using the
        /// given conflict resolution strategy and recording every resolved
        /// collision in <paramref name="conflicts"/>.
        /// <para>
        /// The generalized merge behind both grammar inheritance
        /// (target = derived grammar, overlay = base grammar, target wins) and
        /// runtime overlay composition (target = base grammar, overlay wins).
        /// </para>
        /// </summary>
        /// <param name="target">The grammar merged into; mutated by this call.</param>
        /// <param name="overlay">The grammar whose rules are injected.</param>
        /// <param name="conflictResolution">How name collisions are resolved.</param>
        /// <param name="conflicts">Collector for resolved collision reports.</param>
        private static void MergeOverlayInto(
            GrammarDefinition target,
            GrammarDefinition overlay,
            OverlayConflictResolution conflictResolution,
            List<string> conflicts)
        {
            MergeTokenRules(target, overlay, conflictResolution, conflicts);
            MergeProductionRules(target, overlay, conflictResolution, conflicts);
            MergePrecedenceAndAssociativity(target, overlay, conflictResolution);
            MergeSemanticActions(target, overlay, conflictResolution);
            MergeContexts(target, overlay);
        }

        /// <summary>
        /// Merge overlay token rules into the target grammar by rule name.
        /// </summary>
        private static void MergeTokenRules(
            GrammarDefinition target,
            GrammarDefinition overlay,
            OverlayConflictResolution conflictResolution,
            List<string> conflicts)
        {
            var targetTokensByName = new Dictionary<string, TokenRule?>();
            foreach (var rule in target.TokenRules)
            {
                targetTokensByName.TryAdd(rule.Name, rule);
            }

            foreach (var overlayRule in overlay.TokenRules)
            {
                if (!targetTokensByName.TryGetValue(overlayRule.Name, out var targetRule) || targetRule is null)
                {
                    target.TokenRules.Add(overlayRule);
                    targetTokensByName[overlayRule.Name] = overlayRule;
                    continue;
                }

                conflicts.Add($"Token rule '{overlayRule.Name}' is defined in both grammars (base: {targetRule.Pattern}, overlay: {overlayRule.Pattern}); resolution: {conflictResolution}.");

                if (TokenOverlayWins(targetRule, overlayRule, conflictResolution))
                {
                    target.TokenRules.Remove(targetRule);
                    target.TokenRules.Add(overlayRule);
                    targetTokensByName[overlayRule.Name] = overlayRule;
                }
            }
        }

        /// <summary>
        /// Determine whether the overlay token rule replaces the target token
        /// rule under the given strategy.
        /// </summary>
        private static bool TokenOverlayWins(TokenRule targetRule, TokenRule overlayRule, OverlayConflictResolution conflictResolution)
        {
            return conflictResolution switch
            {
                OverlayConflictResolution.BaseWins => false,
                OverlayConflictResolution.Additive => false,
                OverlayConflictResolution.PriorityBased => overlayRule.Priority >= targetRule.Priority,
                _ => true
            };
        }

        /// <summary>
        /// Merge overlay production rules into the target grammar by rule
        /// name. A production rule name may carry multiple alternatives; the
        /// strategy decides whether the overlay replaces the target's
        /// alternatives, is appended to them, or is discarded.
        /// </summary>
        private static void MergeProductionRules(
            GrammarDefinition target,
            GrammarDefinition overlay,
            OverlayConflictResolution conflictResolution,
            List<string> conflicts)
        {
            var overlayAlternatives = overlay.ProductionRules
                .GroupBy(rule => rule.Name)
                .ToList();

            foreach (var alternatives in overlayAlternatives)
            {
                var existing = target.ProductionRules.Where(rule => rule.Name == alternatives.Key).ToList();
                if (existing.Count == 0)
                {
                    target.ProductionRules.AddRange(alternatives);
                    continue;
                }

                conflicts.Add($"Production rule '{alternatives.Key}' is defined in both grammars (base alternatives: {existing.Count}, overlay alternatives: {alternatives.Count()}); resolution: {conflictResolution}.");

                switch (conflictResolution)
                {
                    case OverlayConflictResolution.BaseWins:
                        break;

                    case OverlayConflictResolution.Additive:
                        target.ProductionRules.AddRange(alternatives);
                        break;

                    case OverlayConflictResolution.PriorityBased:
                        var overlayPrecedence = alternatives.Max(rule => rule.Precedence);
                        var targetPrecedence = existing.Max(rule => rule.Precedence);
                        if (overlayPrecedence >= targetPrecedence)
                        {
                            foreach (var rule in existing)
                            {
                                target.ProductionRules.Remove(rule);
                            }

                            target.ProductionRules.AddRange(alternatives);
                        }

                        break;

                    default: // OverlayWins
                        foreach (var rule in existing)
                        {
                            target.ProductionRules.Remove(rule);
                        }

                        target.ProductionRules.AddRange(alternatives);
                        break;
                }
            }
        }

        /// <summary>
        /// Merge precedence and associativity entries. Under BaseWins and
        /// Additive the target's explicit declarations are kept and the
        /// overlay only fills gaps; otherwise the overlay's entries win.
        /// </summary>
        private static void MergePrecedenceAndAssociativity(
            GrammarDefinition target,
            GrammarDefinition overlay,
            OverlayConflictResolution conflictResolution)
        {
            var targetWins = conflictResolution is OverlayConflictResolution.BaseWins or OverlayConflictResolution.Additive;

            foreach (var entry in overlay.Precedence)
            {
                if (targetWins)
                {
                    if (!target.Precedence.ContainsKey(entry.Key))
                    {
                        target.Precedence[entry.Key] = entry.Value;
                    }
                }
                else
                {
                    target.Precedence[entry.Key] = entry.Value;
                }
            }

            foreach (var entry in overlay.Associativity)
            {
                if (targetWins)
                {
                    if (!target.Associativity.ContainsKey(entry.Key))
                    {
                        target.Associativity[entry.Key] = entry.Value;
                    }
                }
                else
                {
                    target.Associativity[entry.Key] = entry.Value;
                }
            }
        }

        /// <summary>
        /// Merge semantic actions. Under BaseWins and Additive the target's
        /// actions are kept and the overlay only fills gaps; otherwise the
        /// overlay's actions win.
        /// </summary>
        private static void MergeSemanticActions(
            GrammarDefinition target,
            GrammarDefinition overlay,
            OverlayConflictResolution conflictResolution)
        {
            var targetWins = conflictResolution is OverlayConflictResolution.BaseWins or OverlayConflictResolution.Additive;

            foreach (var entry in overlay.SemanticActions)
            {
                if (targetWins)
                {
                    if (!target.SemanticActions.ContainsKey(entry.Key))
                    {
                        target.SemanticActions[entry.Key] = entry.Value;
                    }
                }
                else
                {
                    target.SemanticActions[entry.Key] = entry.Value;
                }
            }
        }

        /// <summary>
        /// Merge available parsing contexts as a union (target order first).
        /// </summary>
        private static void MergeContexts(GrammarDefinition target, GrammarDefinition overlay)
        {
            foreach (var context in overlay.Contexts)
            {
                if (!target.Contexts.Contains(context))
                {
                    target.Contexts.Add(context);
                }
            }
        }
    }
}
