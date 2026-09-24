using System;
using System.Collections.Generic;
using System.Linq;
using DevelApp.StepLexer;
using CognitiveGraph;
using CognitiveGraph.Builder;
using CognitiveGraph.Accessors;
using CognitiveGraph.Schema;

namespace DevelApp.StepParser
{
    public partial class StepParser : IDisposable
    {
        /// <summary>Cached boxed true value to avoid per-shift boxing allocations.</summary>
        private static readonly object _boxedTrue = true;

        /// <summary>Upper bound on paths explored per token step while
        /// chaining ambiguous reductions; <see cref="Step"/> applies the
        /// final path budget afterwards.</summary>
        private const int MaxIntraStepPaths = 32;

        /// <summary>Cached boxed false value to avoid per-reduction boxing allocations.</summary>
        private static readonly object _boxedFalse = false;

        /// <summary>
        /// Process a single parser path
        /// </summary>
        private ParserPathResult ProcessParserPath(ParserPath path, StepToken currentToken)
        {
            var result = new ParserPathResult();

            // GLR shift/reduce handling: at every state both alternatives are
            // kept — shifting the current token, and applying each applicable
            // reduction. Reductions chain within the same step (a reduction
            // makes further reductions applicable, for example the rule
            // "expr ::= NUMBER" followed by "expr ::= expr PLUS expr");
            // running only one round per token left chained reductions
            // lagging one token behind, so stacks accumulated unreducible
            // combinations and complete parses could never collapse to a
            // single entry (issue #80).
            var pending = new List<ParserPath> { path };
            var maxRounds = (_context.Tokens.Count * 2) + 16;

            for (int round = 0; round < maxRounds && pending.Count > 0; round++)
            {
                var nextPending = new List<ParserPath>();

                foreach (var pendingPath in pending)
                {
                    // Plan all available actions against the current,
                    // unmutated path state. Determining the actions up front
                    // allows the original path to be reused in place when
                    // exactly one action is possible, avoiding a full clone
                    // (with its O(stack) copy).
                    bool canShift = CanAcceptToken(pendingPath, currentToken);

                    var applicableRules = new List<ProductionRule>();
                    foreach (var rule in _grammar)
                    {
                        if (CanApplyReduction(pendingPath, rule, currentToken))
                        {
                            applicableRules.Add(rule);
                        }
                    }

                    int remainingActions = (canShift ? 1 : 0) + applicableRules.Count;

                    // A path whose stack has collapsed to a single entry
                    // may also simply rest: keep it unchanged as a candidate
                    // final path (a complete parse prefix), in addition to
                    // exploring shift and further reductions. Without the
                    // resting alternative, a collapsed path is forced to
                    // shift whatever token comes next (CanAcceptToken only
                    // checks whether any rule mentions the token type),
                    // destroying the complete prefix (issue #80). Resting is
                    // re-evaluated every step; MergeParserPaths deduplicates
                    // identical resting paths, so this does not accumulate.
                    bool canRest = pendingPath.StackDepth == 1;
                    if (canRest)
                    {
                        remainingActions++;
                    }

                    // If no actions possible, mark path as invalid
                    if (remainingActions == 0)
                    {
                        pendingPath.IsValid = false;
                        result.NewPaths.Add(pendingPath);
                        continue;
                    }

                    // Try to shift (accept current token) from this state
                    if (canShift)
                    {
                        var shiftPath = remainingActions > 1 ? pendingPath.Clone(_nextPathId++) : pendingPath;
                        remainingActions--;

                        ApplyShift(shiftPath, currentToken);
                        result.NewPaths.Add(shiftPath);
                    }

                    // Try to reduce (apply production rules)
                    foreach (var rule in applicableRules)
                    {
                        var reducePath = remainingActions > 1 ? pendingPath.Clone(_nextPathId++) : pendingPath;
                        remainingActions--;

                        if (ApplyReduction(reducePath, rule))
                        {
                            nextPending.Add(reducePath);
                            result.Reductions.Add(rule.ToString());
                        }
                        else
                        {
                            reducePath.IsValid = false;
                            result.NewPaths.Add(reducePath);
                        }
                    }

                    // Keep the resting alternative: the unmutated path is a
                    // valid candidate end-state for this step.
                    if (canRest)
                    {
                        result.NewPaths.Add(pendingPath);
                    }
                }

                pending = MergeParserPaths(nextPending);

                // Bound transient path growth from ambiguous reduction chains
                // within a single step; Step() applies the final budget.
                if (pending.Count > MaxIntraStepPaths)
                {
                    pending.Sort(ComparePathsForPruning);
                    pending.RemoveRange(MaxIntraStepPaths, pending.Count - MaxIntraStepPaths);
                }
            }

            // Guard exhausted with paths still pending: their reduction chains
            // could not finish within the bound, so keep them (marked
            // invalid) to let the step loop terminate.
            foreach (var pendingPath in pending)
            {
                pendingPath.IsValid = false;
                result.NewPaths.Add(pendingPath);
            }

            // Safety net: if no action was possible at all, keep the path
            // alive but marked invalid.
            if (result.NewPaths.Count == 0)
            {
                path.IsValid = false;
                result.NewPaths.Add(path);
            }

            return result;
        }

        /// <summary>
        /// Shift the current token onto the parse stack (in place)
        /// </summary>
        private void ApplyShift(ParserPath path, StepToken token)
        {
            // Create node in CognitiveGraph
            var properties = new List<(string key, PropertyValueType type, object value)>
            {
                ("TokenType", PropertyValueType.String, token.Type),
                ("TokenValue", PropertyValueType.String, token.Value),
                ("Context", PropertyValueType.String, token.Context),
                ("IsTerminal", PropertyValueType.Boolean, _boxedTrue)
            };

            var nodeOffset = _graphBuilder.WriteSymbolNode(
                symbolId: _nextSymbolId++,
                nodeType: 100, // Terminal node type
                sourceStart: (uint)token.Location.StartColumn, // Use column as position
                sourceLength: (uint)token.Value.Length,
                properties: properties
            );

            var nodeRef = new GraphNodeRef(
                nodeOffset, 
                (ushort)(_nextSymbolId - 1),
                100,
                token.Type, 
                token.Value, 
                token.Location
            );

            path.PushSymbol(nodeRef);
            path.AddNodeOffset(nodeOffset);
            path.TokenPosition++;
            path.Score *= 0.95f; // Slight penalty for each shift
        }

        /// <summary>
        /// Check if we can accept a token in the current parse state
        /// </summary>
        private bool CanAcceptToken(ParserPath path, StepToken token)
        {
            // Look for rules that expect this token type
            foreach (var rule in _grammar)
            {
                if (rule.RightHandSide.Contains(token.Type) &&
                    IsRuleApplicableInContext(rule, token.Context) &&
                    (rule.Precondition?.Invoke(_context) ?? true))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Check if a reduction can be applied
        /// </summary>
        private bool CanApplyReduction(ParserPath path, ProductionRule rule, StepToken? currentToken, bool isEndOfInput = false)
        {
            if (path.StackDepth < rule.RightHandSide.Count)
                return false;

            // Root rules (whose result is referenced by no other rule) may
            // only collapse the stack at end of input. Mid-parse, their
            // lookahead stand-in is a real token: reducing the root early
            // produces stacks like [PLUS, start] that then shift further
            // tokens and crowd out the genuine parse paths (issue #80).
            if (!isEndOfInput && GetRootRuleNames().Contains(rule.Name))
            {
                return false;
            }

            // The lookahead token is null during the end-of-input
            // reduction phase (FinalizeEndOfInputReductions); use the
            // context of the last consumed token in that case.
            if (!IsRuleApplicableInContext(rule, currentToken?.Context ?? string.Empty))
                return false;

            if (rule.Precondition != null && !rule.Precondition(_context))
                return false;

            // Check if top stack elements match rule RHS (in reverse order).
            // The persistent stack enumerates top to bottom, which is exactly
            // the order the reversed right-hand side must match, so no
            // intermediate array is needed.
            int index = 0;
            foreach (var stackItem in path.StackTopFirst)
            {
                var expectedType = rule.RightHandSide[rule.RightHandSide.Count - 1 - index];
                if (expectedType != stackItem.RuleName)
                {
                    return false;
                }

                index++;
                if (index == rule.RightHandSide.Count)
                {
                    break;
                }
            }

            return true;
        }

        /// <summary>
        /// Apply a production rule reduction to the path (in place)
        /// </summary>
        private bool ApplyReduction(ParserPath path, ProductionRule rule)
        {
            // Pop RHS elements from stack. CanApplyReduction already verified
            // the stack depth; re-check here so a failed reduction can never
            // leave the path partially mutated.
            if (path.StackDepth < rule.RightHandSide.Count)
            {
                return false; // Invalid reduction
            }

            var children = new List<GraphNodeRef>(rule.RightHandSide.Count);
            var childNodeOffsets = new List<uint>(rule.RightHandSide.Count);
            for (int i = 0; i < rule.RightHandSide.Count; i++)
            {
                var childRef = path.PopSymbol();
                children.Insert(0, childRef);
                childNodeOffsets.Insert(0, childRef.NodeOffset);
            }

            // Determine source span for the new node
            var location = children.Count > 0 ? children[0].Location : new CodeLocation();
            var sourceStart = children.Count > 0 ? (uint)children[0].Location.StartColumn : 0u;
            var sourceEnd = children.Count > 0 ? (uint)children[children.Count - 1].Location.EndColumn : 0u;
            var sourceLength = sourceEnd > sourceStart ? sourceEnd - sourceStart : 0u;

            // Create properties for the non-terminal node
            var properties = new List<(string key, PropertyValueType type, object value)>
            {
                ("RuleName", PropertyValueType.String, rule.Name),
                ("Context", PropertyValueType.String, rule.Context),
                ("IsTerminal", PropertyValueType.Boolean, _boxedFalse),
                ("Precedence", PropertyValueType.Int32, rule.Precedence),
                ("Associativity", PropertyValueType.String, rule.Associativity)
            };

            // Create packed node for the reduction if there are children
            uint packedNodeOffset = 0;
            if (childNodeOffsets.Count > 0)
            {
                packedNodeOffset = _graphBuilder.WritePackedNode(
                    ruleId: (ushort)(_ruleIndices[rule] + 1),
                    childNodeOffsets: childNodeOffsets
                );
            }

            // Create the symbol node
            var packedNodes = packedNodeOffset != 0 ? new List<uint> { packedNodeOffset } : null;
            var nodeOffset = _graphBuilder.WriteSymbolNode(
                symbolId: _nextSymbolId++,
                nodeType: 200, // Non-terminal node type
                sourceStart: sourceStart,
                sourceLength: sourceLength,
                packedNodeOffsets: packedNodes,
                properties: properties
            );

            var newNodeRef = new GraphNodeRef(
                nodeOffset,
                (ushort)(_nextSymbolId - 1),
                200,
                rule.Name,
                "",
                location
            );

            // Execute semantic action if present
            try
            {
                rule.SemanticAction?.Invoke(newNodeRef, children, _graphBuilder);
            }
            catch (Exception ex)
            {
                // Log semantic action error and continue
                Console.WriteLine($"Semantic action error for rule {rule.Name}: {ex.Message}");
            }

            path.PushSymbol(newNodeRef);
            path.AddNodeOffset(nodeOffset);
            path.Score *= 1.1f; // Reward successful reductions
            
            return true;
        }

        /// <summary>
        /// Check if rule is applicable in current context
        /// </summary>
        private bool IsRuleApplicableInContext(ProductionRule rule, string currentContext)
        {
            if (string.IsNullOrEmpty(rule.Context))
                return true;

            return rule.Context == currentContext || _context.ContextStack.Contains(rule.Context);
        }

        /// <summary>
        /// Merge similar parser paths to reduce explosion
        /// </summary>
        private List<ParserPath> MergeParserPaths(List<ParserPath> paths)
        {
            var merged = new Dictionary<string, ParserPath>();

            foreach (var path in paths.Where(p => p.IsValid))
            {
                var key = GeneratePathKey(path);
                if (!merged.ContainsKey(key) || merged[key].Score < path.Score)
                {
                    merged[key] = path;
                }
            }

            return merged.Values.ToList();
        }

        /// <summary>
        /// Generate a key for path merging
        /// </summary>
        private string GeneratePathKey(ParserPath path)
        {
            // The stack signature is an order-sensitive 128-bit hash cached
            // on the persistent stack nodes, so key generation is O(1) per
            // path instead of O(stack depth) per path per step.
            var (hash1, hash2) = path.StackSignature;
            return $"{path.TokenPosition}:{path.CurrentState}:{hash1:x16}{hash2:x16}";
        }

        /// <summary>
        /// Ordering used when pruning the path budget: paths that have
        /// consumed more input first (they are the ones that can still
        /// produce a full parse), then higher-scoring paths. Score-only
        /// pruning lets cheap resting prefixes crowd out the deep paths
        /// that still need to finish the input (issue #80).
        /// </summary>
        private static int ComparePathsForPruning(ParserPath a, ParserPath b)
        {
            int byPosition = b.TokenPosition.CompareTo(a.TokenPosition);
            return byPosition != 0 ? byPosition : b.Score.CompareTo(a.Score);
        }

        /// <summary>
        /// Finish pending reductions at the end of the token stream.
        /// </summary>
        /// <remarks>
        /// Reductions normally run while stepping over a lookahead token.
        /// When the last token has been consumed, no further lookahead
        /// exists, so pending reductions (for example
        /// <c>&lt;expr&gt; ::= &lt;expr&gt; '+' &lt;expr&gt;</c> left on the
        /// stack after shifting the final token) would never run and a
        /// complete parse could not collapse to a single stack entry
        /// (issue #80). This phase repeatedly applies reductions — with the
        /// last consumed token as the lookahead stand-in — until no further
        /// reduction applies, mirroring what <see cref="Step"/> does per
        /// token.
        /// </remarks>
        private void FinalizeEndOfInputReductions()
        {
            var lookahead = _context.Tokens.Count > 0 ? _context.Tokens[^1] : null;
            var maxRounds = (_context.Tokens.Count * 2) + 16;

            for (int round = 0; round < maxRounds && _activePaths.Count > 0; round++)
            {
                var newPaths = new List<ParserPath>();
                var progressed = false;

                foreach (var path in _activePaths.Where(p => p.IsValid))
                {
                    // Plan all applicable reductions against the current,
                    // unmutated path state (mirrors ProcessParserPath).
                    var applicableRules = new List<ProductionRule>();
                    foreach (var rule in _grammar)
                    {
                        if (CanApplyReduction(path, rule, lookahead, isEndOfInput: true))
                        {
                            applicableRules.Add(rule);
                        }
                    }

                    if (applicableRules.Count == 0)
                    {
                        newPaths.Add(path);
                        continue;
                    }

                    var remainingActions = applicableRules.Count;
                    var anyApplied = false;
                    foreach (var rule in applicableRules)
                    {
                        var reducePath = remainingActions > 1 ? path.Clone(_nextPathId++) : path;
                        remainingActions--;

                        if (ApplyReduction(reducePath, rule))
                        {
                            newPaths.Add(reducePath);
                            anyApplied = true;
                            progressed = true;
                        }
                    }

                    if (!anyApplied)
                    {
                        path.IsValid = false;
                        newPaths.Add(path);
                    }
                }

                _activePaths.Clear();
                _activePaths.AddRange(MergeParserPaths(newPaths));

                if (!progressed)
                {
                    break;
                }

                // Keep the same path budget as Step so the final phase cannot
                // explode on ambiguous grammars.
                if (_activePaths.Count > 10)
                {
                    _activePaths.Sort((a, b) => b.Score.CompareTo(a.Score));
                    _activePaths.RemoveRange(10, _activePaths.Count - 10);
                }
            }
        }
    }
}