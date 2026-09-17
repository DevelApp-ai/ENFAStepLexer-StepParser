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
        /// Generate complete CognitiveGraphs from successful paths
        /// </summary>
        private List<CognitiveGraph.CognitiveGraph> GenerateCompleteCognitiveGraphs()
        {
            var completeGraphs = new List<CognitiveGraph.CognitiveGraph>();

            var successfulPaths = _activePaths.Where(p => p.IsValid && p.StackDepth == 1).ToList();

            if (successfulPaths.Count == 1)
            {
                // Single successful parse - create simple graph
                var path = successfulPaths[0];
                var rootNodeRef = path.PeekSymbol();
                var buffer = _graphBuilder.Build(rootNodeRef.NodeOffset, _sourceText);
                completeGraphs.Add(new CognitiveGraph.CognitiveGraph(buffer));
            }
            else if (successfulPaths.Count > 1)
            {
                // Multiple successful parses - create ambiguous graph with packed nodes
                var ambiguousNodeOffsets = successfulPaths.Select(p => p.PeekSymbol().NodeOffset).ToList();
                
                // Create packed nodes for each interpretation
                var packedNodeOffsets = new List<uint>();
                for (int i = 0; i < successfulPaths.Count; i++)
                {
                    var packedNodeOffset = _graphBuilder.WritePackedNode(
                        ruleId: (ushort)(i + 1),
                        childNodeOffsets: new List<uint> { ambiguousNodeOffsets[i] }
                    );
                    packedNodeOffsets.Add(packedNodeOffset);
                }

                // Create ambiguous root node
                var ambiguousProperties = new List<(string key, PropertyValueType type, object value)>
                {
                    ("NodeType", PropertyValueType.String, "AmbiguousRoot"),
                    ("ParseCount", PropertyValueType.Int32, successfulPaths.Count),
                    ("IsAmbiguous", PropertyValueType.Boolean, true)
                };

                var ambiguousRootOffset = _graphBuilder.WriteSymbolNode(
                    symbolId: _nextSymbolId++,
                    nodeType: 300, // Ambiguous root node type
                    sourceStart: 0,
                    sourceLength: (uint)_sourceText.Length,
                    packedNodeOffsets: packedNodeOffsets,
                    properties: ambiguousProperties
                );

                var buffer = _graphBuilder.Build(ambiguousRootOffset, _sourceText);
                completeGraphs.Add(new CognitiveGraph.CognitiveGraph(buffer));
            }

            return completeGraphs;
        }

        /// <summary>
        /// Select the best CognitiveGraph based on path scores
        /// </summary>
        public CognitiveGraph.CognitiveGraph? SelectBestParseGraph()
        {
            var completeGraphs = GenerateCompleteCognitiveGraphs();
            if (!completeGraphs.Any())
                return null;

            // For now, return the first available graph
            // In the future, we could implement scoring based on graph properties
            return completeGraphs.First();
        }
    }
}