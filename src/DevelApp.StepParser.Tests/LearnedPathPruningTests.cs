using System;
using System.Linq;
using DevelApp.StepParser;
using Xunit;

namespace DevelApp.StepParser.Tests
{
    /// <summary>
    /// Guard rails and behavior tests for the learned GLR path-pruning
    /// prototype (issue #75, candidate approach 2 of the #58 ML plan).
    /// Contract:
    ///  - The pruner is consulted only while
    ///    <see cref="MlAssistFeature.LearnedPathPruning"/> is enabled; by
    ///    default the parser runs full GLR and ignores any assigned artifact.
    ///  - When enabled, pruning requires high confidence and only acts
    ///    above the deterministic path budget (safety valve with full-GLR
    ///    fallback).
    ///  - No semantic changes: with the default (conservative) threshold the
    ///    parse result is identical with and without the feature.
    /// </summary>
    public class LearnedPathPruningTests : IDisposable
    {
        private const string AmbiguousGrammar = @"
Grammar: AmbiguousExpr
<NUMBER> ::= /[0-9]+/
<PLUS> ::= '+'
<expr> ::= <expr> '+' <expr>
<expr> ::= <NUMBER>
<start> ::= <expr>
";

        public LearnedPathPruningTests()
        {
            MlAssistOptions.ResetForTest();
        }

        public void Dispose()
        {
            MlAssistOptions.ResetForTest();
        }

        private static string GenerateSum(int terms)
        {
            return string.Join("+", Enumerable.Range(0, terms).Select(i => (i * 7) % 10));
        }

        private static StepParsingResult ParseBaseline(string input)
        {
            using var engine = new StepParserEngine();
            engine.LoadGrammarFromContent(AmbiguousGrammar, "AmbiguousExpr.grammar");
            return engine.Parse(input, "input.txt");
        }

        /// <summary>Test pruner that records consultations and can force pruning decisions.</summary>
        private sealed class RecordingPruner : LearnedPathPruner
        {
            public RecordingPruner(double threshold, int minPathCount)
                : base("test-0.1", threshold, minPathCount)
            {
            }

            protected internal override double PredictDoomProbability(
                ParserPath path, float bestScore, int bestPosition, int totalTokens)
            {
                return base.PredictDoomProbability(path, bestScore, bestPosition, totalTokens);
            }
        }

        [Fact]
        public void Pruning_DefaultsOff_EngineIgnoresAssignedPruner()
        {
            var input = GenerateSum(12);
            var baseline = ParseBaseline(input);
            Assert.True(baseline.Success, string.Join("; ", baseline.Errors));

            using var engine = new StepParserEngine();
            engine.LoadGrammarFromContent(AmbiguousGrammar, "AmbiguousExpr.grammar");
            var pruner = new RecordingPruner(threshold: 0.5, minPathCount: 0);
            engine.PathPruner = pruner;

            var gated = engine.Parse(input, "input.txt");
            Assert.True(gated.Success);

            // Feature disabled: the pruner must never be consulted.
            Assert.Equal(0, pruner.Consultations);
            Assert.Equal(0, pruner.PathsPruned);

            // Bit-identical parse result (tokens and surviving path count).
            Assert.Equal(baseline.Success, gated.Success);
            Assert.Equal(baseline.PathCount, gated.PathCount);
            Assert.Equal(
                string.Join("|", baseline.Tokens.Select(t => $"{t.Type}:{t.Value}@{t.StartPosition}")),
                string.Join("|", gated.Tokens.Select(t => $"{t.Type}:{t.Value}@{t.StartPosition}")));
        }

        [Fact]
        public void Pruning_EnabledWithDefaultThreshold_KeepsParseResultIdentical()
        {
            var input = GenerateSum(12);
            var baseline = ParseBaseline(input);

            MlAssistOptions.Enable(MlAssistFeature.LearnedPathPruning, "0.1.0");
            using var engine = new StepParserEngine();
            engine.LoadGrammarFromContent(AmbiguousGrammar, "AmbiguousExpr.grammar");
            engine.PathPruner = LearnedPathPruner.Default;

            Assert.Contains("LearnedPathPruning=0.1.0", engine.MlAssistStatus);

            var pruned = engine.Parse(input, "input.txt");
            Assert.Equal(baseline.Success, pruned.Success);
            Assert.Equal(
                string.Join("|", baseline.Tokens.Select(t => $"{t.Type}:{t.Value}@{t.StartPosition}")),
                string.Join("|", pruned.Tokens.Select(t => $"{t.Type}:{t.Value}@{t.StartPosition}")));
        }

        [Fact]
        public void Pruning_NeverActsAtOrBelowMinPathCount()
        {
            // A pathological pruner (threshold 0, min path count 20): with
            // the safety valve, no parse can ever lose paths while the
            // active count stays at/below the valve.
            var pruner = new LearnedPathPruner("test-0.1", confidenceThreshold: 0.0, minPathCount: 20);

            var paths = new System.Collections.Generic.List<ParserPath>();
            for (var i = 0; i < 20; i++)
            {
                paths.Add(new ParserPath(i));
            }

            pruner.PruneDoomedPaths(paths, totalTokens: 100);
            Assert.Equal(20, paths.Count);
            Assert.Equal(0, pruner.Consultations);

            // Above the valve, the pruner may act.
            paths.Add(new ParserPath(20));
            pruner.PruneDoomedPaths(paths, totalTokens: 100);
            Assert.True(paths.Count <= 21);
            Assert.True(pruner.Consultations > 0);
        }

        [Fact]
        public void Pruning_ModelValidatesArtifact()
        {
            Assert.Equal("0.1.0", LearnedPathPruner.Default.ModelVersion);
            Assert.Equal(0.9, LearnedPathPruner.Default.ConfidenceThreshold);
            Assert.Equal(10, LearnedPathPruner.Default.MinPathCount);

            Assert.Throws<ArgumentException>(() => new LearnedPathPruner(" "));
            Assert.Throws<ArgumentOutOfRangeException>(
                () => new LearnedPathPruner("1.0.0", confidenceThreshold: 1.5));
            Assert.Throws<ArgumentException>(
                () => new LearnedPathPruner("1.0.0", weights: new double[3]));

            // Deterministic: same path features produce the same prediction.
            var path = new ParserPath(1) { Score = 0.5f, TokenPosition = 2 };
            var p1 = LearnedPathPruner.Default.PredictDoomProbability(path, 1.0f, 5, 10);
            var p2 = LearnedPathPruner.Default.PredictDoomProbability(path, 1.0f, 5, 10);
            Assert.Equal(p1, p2);
            Assert.InRange(p1, 0.0, 1.0);
        }

        [Fact]
        public void Pruning_DisableAll_OverridesTheFeatureFlag()
        {
            MlAssistOptions.Enable(MlAssistFeature.LearnedPathPruning, "0.1.0");
            MlAssistOptions.DisableAll = true;

            using var engine = new StepParserEngine();
            engine.LoadGrammarFromContent(AmbiguousGrammar, "AmbiguousExpr.grammar");
            var pruner = new RecordingPruner(threshold: 0.0, minPathCount: 0);
            engine.PathPruner = pruner;

            Assert.Contains("disabled", engine.MlAssistStatus);
            var result = engine.Parse(GenerateSum(8), "input.txt");
            Assert.True(result.Success);
            Assert.Equal(0, pruner.Consultations);
        }
    }
}
