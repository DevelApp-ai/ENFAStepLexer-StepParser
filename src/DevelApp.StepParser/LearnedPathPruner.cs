using System;
using System.Collections.Generic;

namespace DevelApp.StepParser
{
    /// <summary>
    /// Prototype of learned GLR path pruning (issue #75, candidate approach 2
    /// of the issue #58 ML plan): a managed linear model scores how likely a
    /// GLR parse path is doomed, and paths the model is highly confident
    /// about are pruned before the deterministic path budget prune. The
    /// prototype is shipped behind the
    /// <see cref="MlAssistFeature.LearnedPathPruning"/> gate (default off —
    /// full GLR is the default behavior) and never acts below its confidence
    /// threshold or its minimum path count (the safety valve with full-GLR
    /// fallback).
    /// </summary>
    public class LearnedPathPruner
    {
        /// <summary>Number of features the model consumes (see <see cref="ExtractFeatures"/>).</summary>
        public const int FeatureCount = 6;

        private static readonly double[] DefaultWeights =
        {
            // bias, scoreDeficit, positionDeficit, stalled, lowScore, age
            -3.00, 2.00, 3.00, 1.50, 1.00, 0.10
        };

        /// <summary>
        /// The default prototype artifact (model version <c>0.1.0</c>):
        /// hand-initialized weights, to be replaced by a classifier trained
        /// offline on "which path ultimately succeeded" labels from
        /// <c>ml-trace/1</c> corpus traces.
        /// </summary>
        public static LearnedPathPruner Default { get; } = new("0.1.0");

        private readonly double[] _weights;

        /// <summary>Version of the trained model artifact (observable in diagnostics).</summary>
        public string ModelVersion { get; }

        /// <summary>
        /// Minimum predicted doom probability (0..1) required before a path
        /// may be pruned. High by default (0.9): the model must be highly
        /// confident, otherwise the path survives and full GLR continues.
        /// </summary>
        public double ConfidenceThreshold { get; }

        /// <summary>
        /// The pruner never acts while the active path count is at or below
        /// this value (the deterministic path budget). Pruning only kicks in
        /// in the quadratic-blowup regime above the budget.
        /// </summary>
        public int MinPathCount { get; }

        /// <summary>
        /// Number of paths pruned since the last <see cref="ResetCounters"/>.
        /// For diagnostics and the #75 evaluation measurements.
        /// </summary>
        public long PathsPruned { get; private set; }

        /// <summary>
        /// Number of pruning decisions the pruner was consulted for.
        /// </summary>
        public long Consultations { get; private set; }

        /// <summary>
        /// Load a model artifact. <paramref name="weights"/> must contain
        /// exactly <see cref="FeatureCount"/> coefficients in the order
        /// documented on <see cref="ExtractFeatures"/>.
        /// </summary>
        /// <param name="modelVersion">Non-empty model version.</param>
        /// <param name="confidenceThreshold">Minimum doom confidence to prune (default 0.9).</param>
        /// <param name="minPathCount">Minimum path count before pruning is considered (default 10, the path budget).</param>
        /// <param name="weights">Linear coefficients trained offline; defaults to the shipped 0.1.0 weights when omitted.</param>
        public LearnedPathPruner(
            string modelVersion,
            double confidenceThreshold = 0.9,
            int minPathCount = 10,
            double[]? weights = null)
        {
            if (string.IsNullOrWhiteSpace(modelVersion))
            {
                throw new ArgumentException(
                    "A learned path pruner requires a model version " +
                    "(issue #58: model version must be observable in diagnostics).",
                    nameof(modelVersion));
            }

            if (confidenceThreshold < 0.0 || confidenceThreshold > 1.0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(confidenceThreshold), confidenceThreshold,
                    "The confidence threshold is a probability in [0, 1].");
            }

            if (minPathCount < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(minPathCount));
            }

            if (weights != null && weights.Length != FeatureCount)
            {
                throw new ArgumentException(
                    $"The path-pruning model expects exactly {FeatureCount} weights, got {weights.Length}.",
                    nameof(weights));
            }

            ModelVersion = modelVersion;
            ConfidenceThreshold = confidenceThreshold;
            MinPathCount = minPathCount;
            _weights = weights ?? DefaultWeights;
        }

        /// <summary>
        /// Prune high-confidence doomed paths from <paramref name="paths"/>
        /// (in place). Safety valves: nothing is pruned while
        /// <paramref name="paths"/>.Count is at or below
        /// <see cref="MinPathCount"/>, and only paths whose predicted doom
        /// probability reaches <see cref="ConfidenceThreshold"/> are
        /// removed — everything else falls through to the caller's
        /// deterministic budget prune (full-GLR fallback).
        /// </summary>
        /// <param name="paths">Active parse paths.</param>
        /// <param name="totalTokens">Total number of tokens in the parse (for progress features).</param>
        public void PruneDoomedPaths(List<ParserPath> paths, int totalTokens)
        {
            if (paths == null)
            {
                return;
            }

            if (paths.Count <= MinPathCount)
            {
                return;
            }

            // Reference points over the live path set.
            var bestScore = float.MinValue;
            var bestPosition = int.MinValue;
            foreach (var path in paths)
            {
                if (path.Score > bestScore)
                {
                    bestScore = path.Score;
                }

                if (path.TokenPosition > bestPosition)
                {
                    bestPosition = path.TokenPosition;
                }
            }

            if (bestScore == float.MinValue)
            {
                return;
            }

            for (var i = paths.Count - 1; i >= 0; i--)
            {
                var probability = PredictDoomProbability(paths[i], bestScore, bestPosition, totalTokens);
                if (probability >= ConfidenceThreshold)
                {
                    paths[i].IsValid = false;
                    paths.RemoveAt(i);
                    PathsPruned++;
                }
            }
        }

        /// <summary>
        /// Predicted probability (0..1) that <paramref name="path"/> is
        /// doomed. Deterministic for the same inputs and model version.
        /// Virtual so tests can observe consultations.
        /// </summary>
        protected internal virtual double PredictDoomProbability(
            ParserPath path, float bestScore, int bestPosition, int totalTokens)
        {
            Consultations++;
            var features = ExtractFeatures(path, bestScore, bestPosition, totalTokens);
            var z = 0.0;
            for (var i = 0; i < FeatureCount; i++)
            {
                z += _weights[i] * features[i];
            }

            return 1.0 / (1.0 + Math.Exp(-z));
        }

        /// <summary>Reset the diagnostic counters (intended for tests/benchmarks).</summary>
        public void ResetCounters()
        {
            PathsPruned = 0;
            Consultations = 0;
        }

        /// <summary>
        /// The model's input features, in weight order:
        ///  0: bias (always 1),
        ///  1: score deficit vs. the best active path (0..1),
        ///  2: token-position deficit vs. the deepest active path (0..1),
        ///  3: stalled (has not consumed any token yet),
        ///  4: low absolute score (&lt; 0.5),
        ///  5: age (path id / (totalTokens + 1), older forks slightly more suspect).
        /// </summary>
        internal static double[] ExtractFeatures(
            ParserPath path, float bestScore, int bestPosition, int totalTokens)
        {
            var scoreDeficit = bestScore <= 0.0f ? 0.0 : Math.Clamp(1.0 - path.Score / bestScore, 0.0, 1.0);
            var positionDeficit = bestPosition <= 0 ? 0.0 : Math.Clamp((bestPosition - path.TokenPosition) / (double)bestPosition, 0.0, 1.0);

            return new[]
            {
                1.0,
                scoreDeficit,
                positionDeficit,
                path.LastConsumedStart < 0 ? 1.0 : 0.0,
                path.Score < 0.5f ? 1.0 : 0.0,
                (double)path.PathId / (totalTokens + 1)
            };
        }
    }
}
