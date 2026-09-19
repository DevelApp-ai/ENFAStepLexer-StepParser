using System.Collections.Generic;
using CognitiveGraph;
using CognitiveGraph.Builder;
using CognitiveGraph.Schema;
using DevelApp.StepParser;

namespace DevelApp.StepParser.Tests
{
    /// <summary>
    /// Tests for the advanced CognitiveGraph analytics
    /// (<see cref="CognitiveGraphAnalytics"/>): structural statistics,
    /// ambiguity rates, hotspots, source coverage and the composite
    /// complexity score. Deeper graphs are constructed with the
    /// CognitiveGraphBuilder directly because simple grammars produce
    /// shallow single-node graphs.
    /// </summary>
    public class CognitiveGraphAnalyticsTests
    {
        private static readonly List<(string Key, PropertyValueType Type, object Value)> NoProperties = new();

        /// <summary>
        /// Build a synthetic expression graph for the source "1+2+3":
        /// a root expression over "1+2+3" with an inner expression over
        /// "1+2" and five terminal leaves (7 nodes, depth 2).
        /// </summary>
        private static CognitiveGraph.CognitiveGraph BuildExpressionGraph()
        {
            var builder = new CognitiveGraphBuilder();

            var numberOne = builder.WriteSymbolNode(1, 100, 0, 1, null, NoProperties);
            var plusOne = builder.WriteSymbolNode(2, 101, 1, 1, null, NoProperties);
            var numberTwo = builder.WriteSymbolNode(3, 100, 2, 1, null, NoProperties);
            var plusTwo = builder.WriteSymbolNode(4, 101, 3, 1, null, NoProperties);
            var numberThree = builder.WriteSymbolNode(5, 100, 4, 1, null, NoProperties);

            var innerPacked = builder.WritePackedNode(1, new List<uint> { numberOne, plusOne, numberTwo });
            var innerExpression = builder.WriteSymbolNode(6, 200, 0, 3, new List<uint> { innerPacked }, NoProperties);

            var rootPacked = builder.WritePackedNode(1, new List<uint> { innerExpression, plusTwo, numberThree });
            var root = builder.WriteSymbolNode(7, 200, 0, 5, new List<uint> { rootPacked }, NoProperties);

            var buffer = builder.Build(root, "1+2+3");
            return new CognitiveGraph.CognitiveGraph(buffer);
        }

        /// <summary>
        /// Build a synthetic graph whose root has two packed nodes (two
        /// alternative derivations) sharing the same single child.
        /// </summary>
        private static CognitiveGraph.CognitiveGraph BuildAmbiguousGraph()
        {
            var builder = new CognitiveGraphBuilder();

            var leaf = builder.WriteSymbolNode(1, 100, 0, 1, null, NoProperties);
            var firstPacked = builder.WritePackedNode(1, new List<uint> { leaf });
            var secondPacked = builder.WritePackedNode(2, new List<uint> { leaf });
            var root = builder.WriteSymbolNode(2, 200, 0, 1, new List<uint> { firstPacked, secondPacked }, NoProperties);

            var buffer = builder.Build(root, "x");
            return new CognitiveGraph.CognitiveGraph(buffer);
        }

        [Fact]
        public void Analyze_ComputesStructuralStatistics()
        {
            var report = CognitiveGraphAnalytics.Analyze(BuildExpressionGraph());

            Assert.NotNull(report);
            Assert.Equal(7, report.NodeCount);
            Assert.Equal(2, report.MaxDepth);
            Assert.Equal(3, report.MaxFanout);
            Assert.Equal(3.0, report.AverageFanout, 3);
            Assert.Equal(0.0, report.AmbiguityRate, 5);
            Assert.Equal(1.0, report.SourceCoverage, 3);
            Assert.InRange(report.ComplexityScore, 0.0, 1.0);
        }

        [Fact]
        public void Analyze_ComputesNodeTypeDistribution()
        {
            var report = CognitiveGraphAnalytics.Analyze(BuildExpressionGraph());

            Assert.NotNull(report);
            Assert.Equal(3, report.NodeTypeDistribution[100]);
            Assert.Equal(2, report.NodeTypeDistribution[101]);
            Assert.Equal(2, report.NodeTypeDistribution[200]);
        }

        [Fact]
        public void Analyze_ReportsHotspotsOrderedByChildCount()
        {
            var report = CognitiveGraphAnalytics.Analyze(BuildExpressionGraph());

            Assert.NotNull(report);
            Assert.Equal(2, report.Hotspots.Count);
            Assert.All(report.Hotspots, hotspot => Assert.Equal(3, hotspot.ChildCount));
            // The root (source start 0, depth 0) and the inner expression
            // (depth 1) tie on child count; ties break by source start and
            // both start at 0, so just check both are non-terminal nodes.
            Assert.All(report.Hotspots, hotspot => Assert.Equal(200, hotspot.NodeType));
        }

        [Fact]
        public void Analyze_LimitsHotspotsToRequestedMaximum()
        {
            var report = CognitiveGraphAnalytics.Analyze(BuildExpressionGraph(), maxHotspots: 1);

            Assert.NotNull(report);
            Assert.Single(report.Hotspots);
        }

        [Fact]
        public void Analyze_CountsAmbiguousNodesFromPackedAlternatives()
        {
            var report = CognitiveGraphAnalytics.Analyze(BuildAmbiguousGraph());

            Assert.NotNull(report);
            Assert.Equal(2, report.NodeCount);
            Assert.Equal(1, report.MaxDepth);
            Assert.Equal(0.5, report.AmbiguityRate, 5);
        }

        [Fact]
        public void Analyze_AmbiguousGraph_DeduplicatesSharedChildren()
        {
            var report = CognitiveGraphAnalytics.Analyze(BuildAmbiguousGraph());

            Assert.NotNull(report);
            Assert.Equal(1, report.MaxFanout);
            Assert.Single(report.Hotspots);
            Assert.Equal(1, report.Hotspots[0].ChildCount);
        }

        [Fact]
        public void Analyze_RealParseResult_ProducesReport()
        {
            var engine = new StepParserEngine();
            engine.LoadGrammarFromContent(TestGrammars.Get("test-grammars/step-parser-tests/CognitiveGraphAnalyticsTests/TestGrammar.grammar"));
            var result = engine.Parse("123", "test.txt");

            Assert.True(result.Success);
            var report = CognitiveGraphAnalytics.Analyze(result);
            Assert.NotNull(report);
            Assert.True(report.NodeCount >= 1);
            Assert.InRange(report.ComplexityScore, 0.0, 1.0);
        }

        [Fact]
        public void Analyze_NullInputs_ReturnNull()
        {
            CognitiveGraph.CognitiveGraph? nullGraph = null;
            Assert.Null(CognitiveGraphAnalytics.Analyze(nullGraph));

            StepParsingResult? nullResult = null;
            Assert.Null(CognitiveGraphAnalytics.Analyze(nullResult));

            var resultWithoutGraph = new StepParsingResult();
            Assert.Null(CognitiveGraphAnalytics.Analyze(resultWithoutGraph));
        }
    }
}
