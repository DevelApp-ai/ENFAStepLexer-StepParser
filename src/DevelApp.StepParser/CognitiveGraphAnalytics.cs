using System;
using System.Collections.Generic;
using System.Linq;
using CognitiveGraph;
using CognitiveGraph.Accessors;

namespace DevelApp.StepParser
{
    /// <summary>
    /// A structural hotspot in a CognitiveGraph: a node with an unusually
    /// high number of distinct child nodes, useful for locating complex or
    /// deeply nested grammar constructs in large parses.
    /// </summary>
    public class NodeHotspot
    {
        /// <summary>Gets the symbol identifier of the hotspot node.</summary>
        public ushort SymbolID { get; init; }

        /// <summary>Gets the node type of the hotspot node.</summary>
        public ushort NodeType { get; init; }

        /// <summary>Gets the source start offset (in characters) of the hotspot node.</summary>
        public uint SourceStart { get; init; }

        /// <summary>Gets the source length (in characters) of the hotspot node.</summary>
        public uint SourceLength { get; init; }

        /// <summary>Gets the number of distinct child nodes of the hotspot node.</summary>
        public int ChildCount { get; init; }

        /// <summary>Gets the depth of the hotspot node (the root has depth 0).</summary>
        public int Depth { get; init; }
    }

    /// <summary>
    /// The result of analyzing a CognitiveGraph: structural statistics,
    /// ambiguity information, hotspots and a composite complexity score.
    /// </summary>
    public class GraphAnalyticsReport
    {
        /// <summary>Gets the total number of symbol nodes in the graph.</summary>
        public int NodeCount { get; init; }

        /// <summary>Gets the total number of edges in the graph.</summary>
        public int EdgeCount { get; init; }

        /// <summary>Gets the maximum depth of the parse tree (the root alone has depth 0).</summary>
        public int MaxDepth { get; init; }

        /// <summary>Gets the average number of distinct children over all non-leaf nodes.</summary>
        public double AverageFanout { get; init; }

        /// <summary>Gets the maximum number of distinct children of any node.</summary>
        public int MaxFanout { get; init; }

        /// <summary>Gets the fraction of nodes that are ambiguous (0..1).</summary>
        public double AmbiguityRate { get; init; }

        /// <summary>Gets the number of symbol nodes per kilobyte of source.</summary>
        public double NodesPerKb { get; init; }

        /// <summary>Gets the number of edges per kilobyte of source.</summary>
        public double EdgesPerKb { get; init; }

        /// <summary>
        /// Gets the fraction of the source text covered by leaf nodes
        /// (0..1, clamped). Overlapping leaves may cause double counting,
        /// so the value is clamped to the valid range.
        /// </summary>
        public double SourceCoverage { get; init; }

        /// <summary>Gets the nodes with the highest child counts, ordered by child count descending.</summary>
        public IReadOnlyList<NodeHotspot> Hotspots { get; init; } = Array.Empty<NodeHotspot>();

        /// <summary>Gets the number of nodes per node type (node type to count).</summary>
        public IReadOnlyDictionary<ushort, int> NodeTypeDistribution { get; init; }
            = new Dictionary<ushort, int>();

        /// <summary>
        /// Gets a composite complexity score in the range 0..1 combining
        /// normalized depth, fan-out, ambiguity and hotspot concentration.
        /// </summary>
        public double ComplexityScore { get; init; }
    }

    /// <summary>
    /// Advanced analytics over parsed CognitiveGraphs: computes structural
    /// statistics (depth, fan-out, coverage), ambiguity rates, structural
    /// hotspots and a composite complexity score for IDE diagnostics,
    /// refactoring guidance and large-scale code analysis.
    /// </summary>
    /// <remarks>
    /// The analyzer traverses the graph as a DAG: shared child nodes are
    /// visited once, and fan-out counts distinct children. The graphs
    /// produced by <see cref="StepParser"/> for simple grammars are shallow
    /// (a single terminal root node); use the CognitiveGraphBuilder
    /// directly to construct deeper graphs when testing analytics.
    /// </remarks>
    public static class CognitiveGraphAnalytics
    {
        /// <summary>Normalizing divisor for the depth component of the complexity score.</summary>
        private const double DepthNormalization = 20.0;

        /// <summary>Normalizing divisor for the fan-out component of the complexity score.</summary>
        private const double FanoutNormalization = 20.0;

        /// <summary>
        /// Analyze a CognitiveGraph and produce a report of structural
        /// statistics, hotspots and complexity metrics.
        /// </summary>
        /// <param name="graph">The graph to analyze. May be null, in which case a null report is returned.</param>
        /// <param name="maxHotspots">The maximum number of hotspots to report.</param>
        /// <returns>The analytics report, or <see langword="null"/> when the graph is null or has no root node.</returns>
        public static GraphAnalyticsReport? Analyze(CognitiveGraph.CognitiveGraph? graph, int maxHotspots = 5)
        {
            if (graph == null)
            {
                return null;
            }

            uint rootOffset;
            try
            {
                rootOffset = graph.GetRootNode().Offset;
            }
            catch
            {
                // A graph without a valid root node cannot be traversed.
                return null;
            }

            var statistics = graph.GetStatistics();

            // Breadth-first traversal over distinct nodes (tracked by buffer
            // offset, because symbol node accessors are ref structs);
            // children reached through several packed nodes are counted and
            // visited once.
            var visited = new HashSet<uint> { rootOffset };
            var queue = new Queue<(uint offset, int depth)>();
            queue.Enqueue((rootOffset, 0));

            int maxDepth = 0;
            int totalFanout = 0;
            int nodesWithChildren = 0;
            int maxFanout = 0;
            int ambiguousNodes = 0;
            long leafCoveredLength = 0;
            var typeDistribution = new Dictionary<ushort, int>();
            var hotspotCandidates = new List<NodeHotspot>();

            while (queue.Count > 0)
            {
                var (offset, depth) = queue.Dequeue();
                var node = graph.GetNodeAt(offset);

                if (depth > maxDepth)
                {
                    maxDepth = depth;
                }

                typeDistribution[node.NodeType] = typeDistribution.TryGetValue(node.NodeType, out var count) ? count + 1 : 1;

                var packedNodes = node.GetPackedNodes();
                bool ambiguous = node.IsAmbiguous || packedNodes.Count > 1;
                if (ambiguous)
                {
                    ambiguousNodes++;
                }

                // Collect the distinct children across all packed nodes
                // (one packed node per alternative derivation of the node).
                var childOffsets = new HashSet<uint>();
                for (int p = 0; p < packedNodes.Count; p++)
                {
                    var children = packedNodes[p].GetChildNodes();
                    for (int i = 0; i < children.Count; i++)
                    {
                        childOffsets.Add(children[i].Offset);
                    }
                }

                if (childOffsets.Count == 0)
                {
                    // Leaf node: it covers a span of source text directly.
                    leafCoveredLength += node.SourceLength;
                    continue;
                }

                totalFanout += childOffsets.Count;
                nodesWithChildren++;
                if (childOffsets.Count > maxFanout)
                {
                    maxFanout = childOffsets.Count;
                }

                hotspotCandidates.Add(new NodeHotspot
                {
                    SymbolID = node.SymbolID,
                    NodeType = node.NodeType,
                    SourceStart = node.SourceStart,
                    SourceLength = node.SourceLength,
                    ChildCount = childOffsets.Count,
                    Depth = depth
                });

                foreach (var childOffset in childOffsets)
                {
                    if (visited.Add(childOffset))
                    {
                        queue.Enqueue((childOffset, depth + 1));
                    }
                }
            }

            int nodeCount = visited.Count;
            double sourceLength = Math.Max(1.0, statistics.SourceLength);
            double sourceCoverage = Math.Min(1.0, leafCoveredLength / sourceLength);
            double ambiguityRate = nodeCount == 0 ? 0.0 : (double)ambiguousNodes / nodeCount;
            double averageFanout = nodesWithChildren == 0 ? 0.0 : (double)totalFanout / nodesWithChildren;

            var hotspots = hotspotCandidates
                .OrderByDescending(h => h.ChildCount)
                .ThenBy(h => h.SourceStart)
                .Take(Math.Max(0, maxHotspots))
                .ToList();

            double complexityScore = ComputeComplexityScore(
                maxDepth, maxFanout, ambiguityRate, hotspots.FirstOrDefault()?.ChildCount ?? 0, totalFanout);

            return new GraphAnalyticsReport
            {
                NodeCount = nodeCount,
                EdgeCount = (int)statistics.EdgeCount,
                MaxDepth = maxDepth,
                AverageFanout = averageFanout,
                MaxFanout = maxFanout,
                AmbiguityRate = ambiguityRate,
                NodesPerKb = statistics.NodesPerKb,
                EdgesPerKb = statistics.EdgesPerKb,
                SourceCoverage = sourceCoverage,
                Hotspots = hotspots,
                NodeTypeDistribution = typeDistribution,
                ComplexityScore = complexityScore
            };
        }

        /// <summary>
        /// Analyze the primary CognitiveGraph of a parsing result.
        /// </summary>
        /// <param name="result">The parsing result whose graph should be analyzed.</param>
        /// <param name="maxHotspots">The maximum number of hotspots to report.</param>
        /// <returns>The analytics report, or <see langword="null"/> when the result or its graph is null.</returns>
        public static GraphAnalyticsReport? Analyze(StepParsingResult? result, int maxHotspots = 5)
        {
            return result == null ? null : Analyze(result.CognitiveGraph, maxHotspots);
        }

        /// <summary>
        /// Compute the composite complexity score as the average of the
        /// normalized depth, fan-out, ambiguity rate and hotspot
        /// concentration components, each clamped to the range 0..1.
        /// </summary>
        /// <param name="maxDepth">The maximum graph depth.</param>
        /// <param name="maxFanout">The maximum fan-out.</param>
        /// <param name="ambiguityRate">The fraction of ambiguous nodes.</param>
        /// <param name="topHotspotChildCount">The child count of the largest hotspot.</param>
        /// <param name="totalFanout">The total number of child edges in the graph.</param>
        /// <returns>The composite complexity score in the range 0..1.</returns>
        private static double ComputeComplexityScore(int maxDepth, int maxFanout, double ambiguityRate, int topHotspotChildCount, int totalFanout)
        {
            double depthComponent = Math.Min(1.0, maxDepth / DepthNormalization);
            double fanoutComponent = Math.Min(1.0, maxFanout / FanoutNormalization);
            double hotspotConcentration = totalFanout == 0 ? 0.0 : Math.Min(1.0, (double)topHotspotChildCount / totalFanout);
            return (depthComponent + fanoutComponent + ambiguityRate + hotspotConcentration) / 4.0;
        }
    }
}
