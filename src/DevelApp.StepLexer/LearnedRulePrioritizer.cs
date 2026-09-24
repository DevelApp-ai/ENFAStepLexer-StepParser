using System;
using System.Collections.Generic;

namespace DevelApp.StepLexer
{
    /// <summary>
    /// ML-assisted ordering of token-rule evaluation (issue #74, candidate
    /// approach 1 of the issue #58 ML plan). A prioritizer predicts how
    /// likely a rule is to match at the current input position. The lexer
    /// uses the prediction <b>only</b> to choose the evaluation ORDER of the
    /// rules — it never decides match/no-match, so the emitted tokens,
    /// paths and diagnostics stay identical to the non-ML behavior.
    /// </summary>
    public interface ILearnedRulePrioritizer
    {
        /// <summary>
        /// Version of the trained model artifact. Must be non-empty so any
        /// ML-assisted run is attributable to a concrete, reproducible
        /// model (issue #58 constraint).
        /// </summary>
        string ModelVersion { get; }

        /// <summary>
        /// Predicted probability (0..1) that <paramref name="rule"/> matches
        /// at a position whose next input byte is <paramref name="firstByte"/>
        /// under <paramref name="currentContext"/>. Higher means evaluate
        /// earlier. The prediction must be deterministic for the same
        /// inputs and model version.
        /// </summary>
        double PredictMatchProbability(TokenRule rule, byte firstByte, string currentContext);
    }

    /// <summary>
    /// A managed linear model over cheap byte/rule features, used as the
    /// issue #74 prototype artifact. The shipped <see cref="Default"/>
    /// weights (model version <c>0.1.0</c>) are hand-initialized from the
    /// obvious first-byte/rule-kind heuristics and are intended to be
    /// replaced by weights trained offline on <c>ml-trace/1</c> corpus
    /// traces (PR #64 harness); a custom artifact can be loaded with
    /// <see cref="LearnedRulePrioritizer(string, IReadOnlyList{double})"/>.
    /// The model is pure arithmetic over the rule pattern string and the
    /// next input byte: no allocations, no network, fully deterministic.
    /// </summary>
    public sealed class LearnedRulePrioritizer : ILearnedRulePrioritizer
    {
        /// <summary>Number of features the model consumes (see <see cref="ExtractFeatures"/>).</summary>
        public const int FeatureCount = 10;

        private static readonly double[] DefaultWeights =
        {
            // bias, literalFirstByte, regexDigit, regexIdent, regexWs,
            // regexGeneric, skippable, ctxRestricted, literalMiss, nonAscii
            -0.30, 2.00, 1.50, 1.50, 1.20, 0.10, 0.20, -0.20, -0.50, 0.20
        };

        private readonly double[] _weights;

        /// <summary>
        /// The default prototype artifact (model version <c>0.1.0</c>): the
        /// hand-initialized linear model shipped while corpus training data
        /// is being gathered.
        /// </summary>
        public static LearnedRulePrioritizer Default { get; } = new("0.1.0", DefaultWeights);

        /// <inheritdoc />
        public string ModelVersion { get; }

        /// <summary>
        /// Load a trained model artifact. <paramref name="weights"/> must
        /// contain exactly <see cref="FeatureCount"/> coefficients, in the
        /// order documented on <see cref="ExtractFeatures"/>.
        /// </summary>
        /// <param name="modelVersion">Non-empty model version, observable in diagnostics.</param>
        /// <param name="weights">Linear coefficients trained offline on ml-trace/1 records.</param>
        public LearnedRulePrioritizer(string modelVersion, IReadOnlyList<double> weights)
        {
            if (string.IsNullOrWhiteSpace(modelVersion))
            {
                throw new ArgumentException(
                    "A learned rule prioritizer requires a model version " +
                    "(issue #58: model version must be observable in diagnostics).",
                    nameof(modelVersion));
            }

            if (weights == null || weights.Count != FeatureCount)
            {
                throw new ArgumentException(
                    $"The rule-prioritization model expects exactly {FeatureCount} weights, got {weights?.Count ?? 0}.",
                    nameof(weights));
            }

            ModelVersion = modelVersion;
            _weights = new double[FeatureCount];
            for (var i = 0; i < FeatureCount; i++)
            {
                _weights[i] = weights[i];
            }
        }

        /// <inheritdoc />
        public double PredictMatchProbability(TokenRule rule, byte firstByte, string currentContext)
        {
            if (rule is null)
            {
                return 0.0;
            }

            var features = ExtractFeatures(rule, firstByte);
            var z = 0.0;
            for (var i = 0; i < FeatureCount; i++)
            {
                z += _weights[i] * features[i];
            }

            // Logistic squash so the score is a probability in (0, 1);
            // ordering only depends on the linear score, but keeping the
            // output calibrated lets thresholds be reasoned about later.
            return 1.0 / (1.0 + Math.Exp(-z));
        }

        /// <summary>
        /// The model's input features, in weight order. The per-byte
        /// features are crossed with rule-kind indicators so they actually
        /// discriminate between rules at a position (a plain byte-class
        /// feature is constant across rules and cannot change ordering):
        ///  0: bias (always 1),
        ///  1: literal rule whose first literal character equals the next
        ///     input byte,
        ///  2: regex rule with a digit character class and a digit input byte,
        ///  3: regex rule with a letter character class and a letter input byte,
        ///  4: regex rule with a whitespace class and a whitespace input byte,
        ///  5: regex rule (generic, any input byte),
        ///  6: rule is skippable,
        ///  7: rule is restricted to a context,
        ///  8: literal rule whose first literal character differs from the
        ///     next input byte (miss penalty),
        ///  9: next input byte is non-ASCII (&gt;= 0x80).
        /// </summary>
        internal static double[] ExtractFeatures(TokenRule rule, byte firstByte)
        {
            var pattern = rule.Pattern ?? string.Empty;
            var isRegex = pattern.StartsWith("/", StringComparison.Ordinal)
                && pattern.EndsWith("/", StringComparison.Ordinal)
                && pattern.Length > 1;
            var literalFirst = false;
            var literalMiss = false;
            if (!isRegex)
            {
                var core = pattern;
                if (core.Length >= 2 &&
                    ((core[0] == '\'' && core[^1] == '\'') ||
                     (core[0] == '"' && core[^1] == '"')))
                {
                    core = core[1..^1];
                }

                if (core.Length > 0)
                {
                    var first = (byte)core[0];
                    if (first == firstByte)
                    {
                        literalFirst = true;
                    }
                    else
                    {
                        literalMiss = true;
                    }
                }
            }

            var digitByte = firstByte >= (byte)'0' && firstByte <= (byte)'9';
            var letterByte = (firstByte >= (byte)'a' && firstByte <= (byte)'z')
                || (firstByte >= (byte)'A' && firstByte <= (byte)'Z');
            var wsByte = firstByte == (byte)' ' || firstByte == (byte)'\t'
                || firstByte == (byte)'\r' || firstByte == (byte)'\n';

            return new[]
            {
                1.0,
                literalFirst ? 1.0 : 0.0,
                isRegex && digitByte && pattern.Contains("[0-9]") ? 1.0 : 0.0,
                isRegex && letterByte && pattern.Contains("[a-zA-Z]") ? 1.0 : 0.0,
                isRegex && wsByte && (pattern.Contains("\\s") || pattern.Contains(" \\t") || pattern.Contains("[ \\t")) ? 1.0 : 0.0,
                isRegex ? 1.0 : 0.0,
                rule.IsSkippable ? 1.0 : 0.0,
                string.IsNullOrEmpty(rule.Context) ? 0.0 : 1.0,
                literalMiss ? 1.0 : 0.0,
                firstByte >= 0x80 ? 1.0 : 0.0
            };
        }
    }

    /// <summary>
    /// Always-on counters for the rule-evaluation hot loop (issues #73/#74):
    /// how many match attempts were made in total, and how many of them
    /// failed before the first successful match at their position. The
    /// wasted-before-first-match count is the metric a learned ordering
    /// (issue #74) is supposed to shrink — ordering cannot reduce the
    /// total attempt count because every applicable rule is always
    /// evaluated.
    /// </summary>
    public static class RuleMatchDiagnostics
    {
        /// <summary>Total number of token-rule match attempts since the last reset.</summary>
        public static long TotalMatchAttempts { get; private set; }

        /// <summary>
        /// Match attempts that failed before the first success at their
        /// position since the last reset.
        /// </summary>
        public static long WastedMatchAttempts { get; private set; }

        /// <summary>
        /// <see cref="WastedMatchAttempts"/> divided by
        /// <see cref="TotalMatchAttempts"/> (0 when no attempts were made).
        /// </summary>
        public static double WastedAttemptRatio =>
            TotalMatchAttempts == 0 ? 0.0 : (double)WastedMatchAttempts / TotalMatchAttempts;

        /// <summary>
        /// Record the attempts of one lexer position (one
        /// <c>ProcessPath</c> evaluation round).
        /// </summary>
        /// <param name="attempts">Number of rule match attempts made.</param>
        /// <param name="wastedBeforeFirstMatch">
        /// Attempts that failed before the first successful match.
        /// </param>
        public static void RecordPosition(int attempts, int wastedBeforeFirstMatch)
        {
            TotalMatchAttempts += attempts;
            WastedMatchAttempts += Math.Max(0, wastedBeforeFirstMatch);
        }

        /// <summary>Reset the counters (intended for tests and benchmarks).</summary>
        public static void Reset()
        {
            TotalMatchAttempts = 0;
            WastedMatchAttempts = 0;
        }
    }
}
