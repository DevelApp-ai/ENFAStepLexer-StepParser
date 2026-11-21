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
            // States (token position, parser state, stack signature)
            // already explored in this call; used for fixed-point
            // termination of the pending-reduction loop.
            var seenStates = new HashSet<string>();
            seenStates.Add(GeneratePathKey(path));

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

                    // If no actions possible, mark path as invalid — unless
                    // the token is one this path must skip: a sibling
                    // interpretation of a span it already consumed, or an
                    // alternative whose sibling follows in the stream (the
                    // path may be able to shift the sibling instead).
                    if (remainingActions == 0)
                    {
                        if (currentToken.StartPosition <= pendingPath.LastConsumedStart
                            || HasSiblingAlternative())
                        {
                            result.NewPaths.Add(pendingPath);
                        }
                        else
                        {
                            pendingPath.IsValid = false;
                            result.NewPaths.Add(pendingPath);
                        }
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

                var mergedPaths = MergeParserPaths(nextPending);

                // Fixed-point termination (issue #157, part 1 follow-up):
                // a merged state whose key (token position, parser state,
                // stack signature) was already explored earlier in this
                // call cannot produce new outcomes — a state's key fully
                // determines its future transitions, because reduction
                // applicability and the key are computed from exactly
                // those fields. Drop recycled states from the pending
                // list instead of re-processing them: re-processing only
                // re-derived identical shifts and reductions, and every
                // re-derivation wrote duplicate nodes into the
                // append-only graph builder, which is what OOM-killed
                // the test host on long ambiguous chains (issue #157,
                // part 4): a 1,600-term SimpleExpr chain accumulated
                // ~2.3 million graph node writes (>99% duplicates)
                // before the builder's buffer doubling ran out of
                // memory. Scores are unaffected in practice:
                // MergeParserPaths already collapses per-key variants,
                // so a recycled path is by construction the same state
                // as the one explored earlier.
                var anyNewState = false;
                var unexploredPaths = new List<ParserPath>(mergedPaths.Count);
                foreach (var mergedPath in mergedPaths)
                {
                    var key = GeneratePathKey(mergedPath);
                    if (seenStates.Add(key))
                    {
                        unexploredPaths.Add(mergedPath);
                        anyNewState = true;
                    }
                }

                // Assign before the break as well: the tail below marks
                // whatever remains in pending invalid, and the stale
                // round's list must not be re-flagged there.
                pending = unexploredPaths;

                if (!anyNewState)
                {
                    break;
                }

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
            // Memoized shift (issue #157, part 4): the terminal node a
            // shift writes depends only on the token, not on the shifting
            // path, so every path that shifts the same token gets the
            // same node. Without this, ambiguous chains re-shifted each
            // token once per exploration state — a 1,600-term SimpleExpr
            // chain wrote ~2 million duplicate terminal nodes into the
            // append-only builder, and the builder's capacity doubling
            // then OOM-crashed the host. Sharing the node is exactly what
            // the completed graph already implies: a traversal from the
            // root only ever reaches one copy per (path, token) anyway,
            // and identical tokens produced as ambiguous-lex
            // alternatives are distinct StepToken instances, so the
            // reference-keyed memo never conflates them.
            if (_shiftNodeMemo.TryGetValue(token, out var memoNodeRef))
            {
                path.PushSymbol(memoNodeRef);
                path.AddNodeOffset(memoNodeRef.NodeOffset);
                path.TokenPosition++;
                path.LastConsumedStart = token.StartPosition;
                path.AddConsumedToken(token);
                path.Score *= 0.95f; // Slight penalty for each shift
                return;
            }

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
                sourceStart: (uint)token.StartPosition, // 0-based absolute offset (issue #98; column was 1-based)
                sourceLength: (uint)token.Value.Length,
                properties: properties
            );

            var nodeRef = new GraphNodeRef(
                nodeOffset, 
                (ushort)(_nextSymbolId - 1),
                100,
                token.Type, 
                token.Value, 
                token.Location,
                token.StartPosition,
                token.StartPosition + token.Value.Length
            );

            path.PushSymbol(nodeRef);
            path.AddNodeOffset(nodeOffset);
            _graphNodeWrites++;
            _shiftNodeMemo[token] = nodeRef;
            path.TokenPosition++;
            path.LastConsumedStart = token.StartPosition;
            // Issue #112: record the shifted token so the winning
            // derivation's flattened token sequence (ResolvedTokens) can be
            // reported without re-deriving it from the graph.
            path.AddConsumedToken(token);
            path.Score *= 0.95f; // Slight penalty for each shift
        }

        /// <summary>
        /// Whether the current token has a sibling interpretation in the
        /// token stream: ambiguous lexing emits alternative tokens for one
        /// source span adjacently, so the token immediately after the
        /// current one shares its <see cref="StepToken.StartPosition"/>.
        /// A path that cannot use the current interpretation may skip it
        /// and try the sibling (issue #83).
        /// </summary>
        private bool HasSiblingAlternative()
        {
            var tokens = _context.Tokens;
            var index = _context.CurrentTokenIndex;
            return index + 1 < tokens.Count
                && tokens[index + 1].StartPosition == tokens[index].StartPosition;
        }

        /// <summary>
        /// Check if we can accept a token in the current parse state
        /// </summary>
        private bool CanAcceptToken(ParserPath path, StepToken token)
        {
            // A token whose source span this path has already consumed (a
            // sibling interpretation of the same bytes, emitted in parallel
            // by an ambiguous lexer run) must never be shifted a second
            // time; the caller keeps the path alive and skips it instead.
            if (token.StartPosition <= path.LastConsumedStart)
            {
                return false;
            }

            // The token may be shifted only when it can start a production
            // right-hand side, or continue one whose preceding symbols are
            // already on the stack top. The previous looser check ("any rule
            // mentions the token type") let doomed paths shift forever after
            // a wrong reduction, and those dead forks then crowded out the
            // genuine parses in the path budget once grammars grew
            // ambiguous through CEBNF normalization (issue #83): a list
            // grammar with optional/repeated items failed from the third
            // item on because the surviving path never collapsed.
            foreach (var rule in _grammar)
            {
                if (!IsRuleApplicableInContext(rule, token.Context) ||
                    !(rule.Precondition?.Invoke(_context) ?? true))
                {
                    continue;
                }

                var rhs = rule.RightHandSide;
                for (int i = 0; i < rhs.Count; i++)
                {
                    if (rhs[i] != token.Type)
                    {
                        continue;
                    }

                    // Token starts the right-hand side: always shiftable.
                    if (i == 0)
                    {
                        return true;
                    }

                    // Otherwise the i symbols before the token must already
                    // sit on the stack top (reductions run before shifts in
                    // the pending loop, so partially reduced stacks are also
                    // evaluated).
                    if (path.StackDepth < i)
                    {
                        continue;
                    }

                    int matched = 0;
                    bool prefixOnStack = true;
                    foreach (var stackItem in path.StackTopFirst)
                    {
                        if (stackItem.RuleName != rhs[i - 1 - matched])
                        {
                            prefixOnStack = false;
                            break;
                        }

                        matched++;
                        if (matched == i)
                        {
                            break;
                        }
                    }

                    if (prefixOnStack)
                    {
                        return true;
                    }
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

            // Memoization key, captured before any mutation: the path's
            // state key (token position, parser state, stack signature)
            // plus the rule fully determine the reduction's outcome —
            // the same (state, rule) pair pops the same children and
            // writes the same nodes. Long ambiguous chains re-derive the
            // same reduction states across many steps: on a 1,600-term
            // SimpleExpr chain more than 75% of reduction derivations
            // were re-derivations of states already explored in an
            // earlier step, and every re-derivation re-wrote duplicate
            // nodes into the append-only graph builder until the test
            // host OOM-aborted (issue #157, part 4). Reusing the node
            // written by the first derivation keeps the parse graph
            // byte-identical while bounding node writes by the number of
            // distinct derivation states.
            var memoKey = GeneratePathKey(path) + ":" + _ruleIndices[rule];
            if (_reductionNodeMemo.TryGetValue(memoKey, out var memoizedNodeRef))
            {
                for (int i = 0; i < rule.RightHandSide.Count; i++)
                {
                    path.PopSymbol();
                }

                path.PushSymbol(memoizedNodeRef);
                path.AddNodeOffset(memoizedNodeRef.NodeOffset);
                path.Score *= 1.1f; // Reward successful reductions
                return true;
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
            var sourceStart = children.Count > 0 ? (uint)children[0].StartPosition : 0u;
            var sourceEnd = children.Count > 0 ? (uint)children[children.Count - 1].EndPosition : 0u;
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
                location,
                (int)sourceStart,
                (int)sourceEnd
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
            // First derivation of this (state, rule) pair: record the node
            // so re-derivations reuse it instead of writing a duplicate
            // (issue #157, part 4).
            _reductionNodeMemo[memoKey] = newNodeRef;
            // A reduction writes a packed node (when children exist)
            // plus the symbol node; count both (issue #163 bookkeeping).
            _graphNodeWrites += childNodeOffsets.Count > 0 ? 2 : 1;
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
        internal static int ComparePathsForPruning(ParserPath a, ParserPath b)
        {
            int byPosition = b.TokenPosition.CompareTo(a.TokenPosition);
            return byPosition != 0 ? byPosition : b.Score.CompareTo(a.Score);
        }

        /// <summary>
        /// Apply the deterministic active-path budget (issue #157, part 3).
        /// Paths are ordered by <see cref="ComparePathsForPruning"/> (input
        /// position first, then score) and truncated to
        /// <see cref="PathBudget"/> — but the single highest-scoring path is
        /// always kept, even when the position-first ordering would evict it.
        /// The hard cap alone pruned the only path that could still complete
        /// the parse on ambiguous grammars (e.g. Norwegian_Nynorsk).
        /// </summary>
        internal void PruneToPathBudget(List<ParserPath> paths)
        {
            var budget = PathBudget;
            if (budget < 1 || paths.Count <= budget)
            {
                return;
            }

            paths.Sort(ComparePathsForPruning);

            int bestIdx = 0;
            for (int i = 1; i < paths.Count; i++)
            {
                if (paths[i].Score > paths[bestIdx].Score)
                {
                    bestIdx = i;
                }
            }

            if (bestIdx < budget)
            {
                // Top scorer already inside the kept prefix: plain truncate.
                paths.RemoveRange(budget, paths.Count - budget);
                return;
            }

            // Move the top scorer into the last kept slot so the survivor is
            // never evicted.
            var survivor = paths[bestIdx];
            paths.RemoveAt(bestIdx);
            paths.RemoveRange(budget - 1, paths.Count - (budget - 1));
            paths.Insert(budget - 1, survivor);
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
            // States already explored in an earlier round of this phase.
            // Processing a state (applying its reductions) is deterministic
            // given the constant end-of-input lookahead, so an already
            // explored state can only re-produce outcomes that were derived
            // before. Re-processing also re-wrote duplicate nodes into the
            // append-only graph builder (issue #157, part 4).
            var seenFinalStates = new HashSet<string>();
            var lookahead = _context.Tokens.Count > 0 ? _context.Tokens[^1] : null;
            var maxRounds = (_context.Tokens.Count * 2) + 16;

            for (int round = 0; round < maxRounds && _activePaths.Count > 0; round++)
            {
                var newPaths = new List<ParserPath>();
                var passedThrough = new List<ParserPath>();
                var progressed = false;
                var exploredThisRound = 0;

                foreach (var path in _activePaths.Where(p => p.IsValid))
                {
                    // Already explored in an earlier round: keep the path
                    // as a candidate final state but do not re-process it.
                    if (!seenFinalStates.Add(GeneratePathKey(path)))
                    {
                        passedThrough.Add(path);
                        continue;
                    }

                    exploredThisRound++;
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
                newPaths.AddRange(passedThrough);
                _activePaths.AddRange(MergeParserPaths(newPaths));

                // Fixed-point termination (issue #157, part 1 follow-up):
                // a round that explored no unexplored state has nothing
                // left to derive; without processing there are no new
                // outcomes, so further rounds would only cycle.
                if (exploredThisRound == 0)
                {
                    break;
                }

                if (!progressed)
                {
                    break;
                }

                // Keep the same path budget as Step so the final phase cannot
                // explode on ambiguous grammars.
                if (_activePaths.Count > PathBudget)
                {
                    _activePaths.Sort((a, b) => b.Score.CompareTo(a.Score));
                    _activePaths.RemoveRange(PathBudget, _activePaths.Count - PathBudget);
                }
            }
        }
    }
}