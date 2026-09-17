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

        /// <summary>Cached boxed false value to avoid per-reduction boxing allocations.</summary>
        private static readonly object _boxedFalse = false;

        /// <summary>
        /// Process a single parser path
        /// </summary>
        private ParserPathResult ProcessParserPath(ParserPath path, StepToken currentToken)
        {
            var result = new ParserPathResult();

            // Plan all available actions against the current, unmutated path state.
            // Determining the actions up front allows the original path to be
            // reused in place when exactly one action is possible, avoiding a
            // full clone (with its O(stack) copy) on every shift and reduction.
            bool canShift = CanAcceptToken(path, currentToken);

            var applicableRules = new List<ProductionRule>();
            foreach (var rule in _grammar)
            {
                if (CanApplyReduction(path, rule, currentToken))
                {
                    applicableRules.Add(rule);
                }
            }

            int remainingActions = (canShift ? 1 : 0) + applicableRules.Count;

            // If no actions possible, mark path as invalid
            if (remainingActions == 0)
            {
                path.IsValid = false;
                result.NewPaths.Add(path);
                return result;
            }

            // Try to shift (accept current token)
            if (canShift)
            {
                // When further actions follow, the shift must run on a clone so
                // its result snapshots the path state before any mutation.
                var shiftPath = remainingActions > 1 ? path.Clone(_nextPathId++) : path;
                remainingActions--;

                ApplyShift(shiftPath, currentToken);
                result.NewPaths.Add(shiftPath);
            }

            // Try to reduce (apply production rules)
            foreach (var rule in applicableRules)
            {
                var reducePath = remainingActions > 1 ? path.Clone(_nextPathId++) : path;
                remainingActions--;

                if (ApplyReduction(reducePath, rule))
                {
                    result.NewPaths.Add(reducePath);
                    result.Reductions.Add(rule.ToString());
                }
            }

            // Safety net: if every planned action failed to apply (which cannot
            // happen after successful planning, since ApplyReduction validates
            // before mutating), keep the path alive but marked invalid.
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

            path.ParseStack.Push(nodeRef);
            path.NodeOffsets.Add(nodeOffset);
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
        private bool CanApplyReduction(ParserPath path, ProductionRule rule, StepToken currentToken)
        {
            if (path.ParseStack.Count < rule.RightHandSide.Count)
                return false;

            if (!IsRuleApplicableInContext(rule, currentToken.Context))
                return false;

            if (rule.Precondition != null && !rule.Precondition(_context))
                return false;

            // Check if top stack elements match rule RHS (in reverse order).
            // Stack<GraphNodeRef> enumerates top to bottom, which is exactly
            // the order the reversed right-hand side must match, so no
            // intermediate array is needed.
            int index = 0;
            foreach (var stackItem in path.ParseStack)
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
            if (path.ParseStack.Count < rule.RightHandSide.Count)
            {
                return false; // Invalid reduction
            }

            var children = new List<GraphNodeRef>(rule.RightHandSide.Count);
            var childNodeOffsets = new List<uint>(rule.RightHandSide.Count);
            for (int i = 0; i < rule.RightHandSide.Count; i++)
            {
                var childRef = path.ParseStack.Pop();
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

            path.ParseStack.Push(newNodeRef);
            path.NodeOffsets.Add(nodeOffset);
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
            var stackSignature = string.Join(",", path.ParseStack.Select(n => n.RuleName));
            return $"{path.TokenPosition}:{path.CurrentState}:{stackSignature}";
        }
    }
}