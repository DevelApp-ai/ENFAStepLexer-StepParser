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

        /// <summary>
        /// Process a single parser path
        /// </summary>
        private ParserPathResult ProcessParserPath(ParserPath path, StepToken currentToken)
        {
            var result = new ParserPathResult();

            // Try to shift (accept current token)
            var shiftResult = TryShift(path, currentToken);
            if (shiftResult.success)
            {
                result.NewPaths.Add(shiftResult.path);
            }

            // Try to reduce (apply production rules)
            var reductionResults = TryReduce(path, currentToken);
            result.NewPaths.AddRange(reductionResults.Select(r => r.path));
            result.Reductions.AddRange(reductionResults.Select(r => r.reduction));

            // If no actions possible, mark path as invalid
            if (!shiftResult.success && !reductionResults.Any())
            {
                path.IsValid = false;
                result.NewPaths.Add(path);
            }

            return result;
        }

        /// <summary>
        /// Try to shift current token onto parse stack
        /// </summary>
        private (bool success, ParserPath path) TryShift(ParserPath path, StepToken token)
        {
            // Check if we can accept this token type
            if (CanAcceptToken(path, token))
            {
                var newPath = path.Clone(_nextPathId++);
                
                // Create node in CognitiveGraph
                var properties = new List<(string key, PropertyValueType type, object value)>
                {
                    ("TokenType", PropertyValueType.String, token.Type),
                    ("TokenValue", PropertyValueType.String, token.Value),
                    ("Context", PropertyValueType.String, token.Context),
                    ("IsTerminal", PropertyValueType.Boolean, true)
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

                newPath.ParseStack.Push(nodeRef);
                newPath.NodeOffsets.Add(nodeOffset);
                newPath.TokenPosition++;
                newPath.Score *= 0.95f; // Slight penalty for each shift
                return (true, newPath);
            }

            return (false, path);
        }

        /// <summary>
        /// Try to reduce using available production rules
        /// </summary>
        private List<(ParserPath path, string reduction)> TryReduce(ParserPath path, StepToken currentToken)
        {
            var results = new List<(ParserPath path, string reduction)>();

            foreach (var rule in _grammar)
            {
                if (CanApplyReduction(path, rule, currentToken))
                {
                    var reducedPath = ApplyReduction(path, rule);
                    if (reducedPath != null)
                    {
                        results.Add((reducedPath, rule.ToString()));
                    }
                }
            }

            return results;
        }

        /// <summary>
        /// Check if we can accept a token in the current parse state
        /// </summary>
        private bool CanAcceptToken(ParserPath path, StepToken token)
        {
            // Look for rules that expect this token type
            return _grammar.Any(rule => 
                rule.RightHandSide.Contains(token.Type) && 
                IsRuleApplicableInContext(rule, token.Context) &&
                (rule.Precondition?.Invoke(_context) ?? true));
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

            // Check if top stack elements match rule RHS (in reverse order)
            var stackItems = path.ParseStack.Take(rule.RightHandSide.Count).ToArray();
            for (int i = 0; i < rule.RightHandSide.Count; i++)
            {
                var expectedType = rule.RightHandSide[rule.RightHandSide.Count - 1 - i];
                var actualType = stackItems[i].RuleName;
                
                if (expectedType != actualType)
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Apply a production rule reduction
        /// </summary>
        private ParserPath? ApplyReduction(ParserPath path, ProductionRule rule)
        {
            var newPath = path.Clone(_nextPathId++);
            
            // Pop RHS elements from stack
            var children = new List<GraphNodeRef>();
            var childNodeOffsets = new List<uint>();
            for (int i = 0; i < rule.RightHandSide.Count; i++)
            {
                if (newPath.ParseStack.Count > 0)
                {
                    var childRef = newPath.ParseStack.Pop();
                    children.Insert(0, childRef);
                    childNodeOffsets.Insert(0, childRef.NodeOffset);
                }
                else
                {
                    return null; // Invalid reduction
                }
            }

            // Determine source span for the new node
            var location = children.Count > 0 ? children[0].Location : new CodeLocation();
            var sourceStart = children.Count > 0 ? (uint)children[0].Location.StartColumn : 0u;
            var sourceEnd = children.Count > 0 ? (uint)children.Last().Location.EndColumn : 0u;
            var sourceLength = sourceEnd > sourceStart ? sourceEnd - sourceStart : 0u;

            // Create properties for the non-terminal node
            var properties = new List<(string key, PropertyValueType type, object value)>
            {
                ("RuleName", PropertyValueType.String, rule.Name),
                ("Context", PropertyValueType.String, rule.Context),
                ("IsTerminal", PropertyValueType.Boolean, false),
                ("Precedence", PropertyValueType.Int32, rule.Precedence),
                ("Associativity", PropertyValueType.String, rule.Associativity)
            };

            // Create packed node for the reduction if there are children
            uint packedNodeOffset = 0;
            if (childNodeOffsets.Any())
            {
                packedNodeOffset = _graphBuilder.WritePackedNode(
                    ruleId: (ushort)(_grammar.IndexOf(rule) + 1),
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

            newPath.ParseStack.Push(newNodeRef);
            newPath.NodeOffsets.Add(nodeOffset);
            newPath.Score *= 1.1f; // Reward successful reductions
            
            return newPath;
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