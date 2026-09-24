using System;
using System.Collections.Generic;
using System.Linq;

namespace DevelApp.StepParser
{
    /// <summary>
    /// The ML-assisted optimization features that may be enabled individually
    /// (issue #58). Every feature is <b>OFF by default</b> and must remain
    /// observation-free until a prototype (sub-issues #74/#75/#76) lands
    /// behind this switch.
    /// </summary>
    public enum MlAssistFeature
    {
        /// <summary>Candidate approach 1: learned ordering of token-rule evaluation (sub-issue #74).</summary>
        LearnedRulePrioritization,

        /// <summary>Candidate approach 2: learned pruning of doomed GLR paths (sub-issue #75).</summary>
        LearnedPathPruning,

        /// <summary>Candidate approach 3: hotspot-guided parsing-strategy selection.</summary>
        HotspotStrategySelection,

        /// <summary>Candidate approach 4: learned token-reuse prediction for <see cref="RealTimeParserSession"/>.</summary>
        TokenReusePrediction
    }

    /// <summary>
    /// Immutable snapshot of the ML-assist configuration, for diagnostics and
    /// tests. <see cref="ActiveFeatures"/> is empty when everything is
    /// disabled (the default).
    /// </summary>
    public sealed record MlAssistStatus
    {
        /// <summary>Master switch value. When true, every feature is disabled regardless of individual flags.</summary>
        public bool DisableAll { get; init; }

        /// <summary>Names of the features currently enabled, in declaration order.</summary>
        public IReadOnlyList<string> ActiveFeatures { get; init; } = Array.Empty<string>();

        /// <summary>Model versions per enabled feature (feature name → version), for diagnostics.</summary>
        public IReadOnlyDictionary<string, string> ModelVersions { get; init; }
            = new Dictionary<string, string>();
    }

    /// <summary>
    /// Central guard-rail switch for ML-assisted parsing/lexing behavior
    /// (issue #58 / sub-issue #77).
    ///
    /// Contract (enforced by <c>MlGuardRailTests</c>):
    ///  - Every feature is disabled by default; the deterministic,
    ///    bit-identical non-ML behavior is what ships unless explicitly
    ///    opted in per feature.
    ///  - <see cref="Enable"/> requires a non-empty model version so any
    ///    ML-assisted run is attributable to a concrete, reproducible model.
    ///  - <see cref="DisableAll"/> and the <c>DEVELAPP_STEPML_DISABLE_ALL</c>
    ///    environment variable force every feature off, overriding individual
    ///    flags - a runtime escape hatch that requires no rebuild.
    ///  - ML prototypes (sub-issues #74/#75) MUST route every behavioral
    ///    decision through <see cref="IsEnabled"/> and must never change
    ///    token boundaries, parse trees or emitted diagnostics.
    /// </summary>
    public static class MlAssistOptions
    {
        /// <summary>
        /// Environment variable that forces all ML-assist features off when
        /// set to "1"/"true" (case-insensitive). Checked live so the escape
        /// hatch works without restarting the process where feasible.
        /// </summary>
        public const string DisableAllEnvVar = "DEVELAPP_STEPML_DISABLE_ALL";

        /// <summary>Model version reported when a feature is not enabled.</summary>
        public const string NoModelVersion = "none";

        private static readonly object Gate = new();

        private static readonly Dictionary<MlAssistFeature, bool> Enabled =
            new(Enum.GetValues<MlAssistFeature>().ToDictionary(f => f, _ => false));

        private static readonly Dictionary<MlAssistFeature, string> ModelVersions =
            new(Enum.GetValues<MlAssistFeature>().ToDictionary(f => f, _ => NoModelVersion));

        /// <summary>
        /// Master switch: when true (or when <c>DEVELAPP_STEPML_DISABLE_ALL</c>
        /// is set), <see cref="IsEnabled"/> reports false for every feature
        /// regardless of the individual flags.
        /// </summary>
        public static bool DisableAll { get; set; }

        /// <summary>
        /// Whether the given ML-assist feature is currently active. This is
        /// the ONLY gate ML prototypes may consult; default is always false.
        /// </summary>
        public static bool IsEnabled(MlAssistFeature feature)
        {
            if (DisableAll || EnvDisableAll())
            {
                return false;
            }

            lock (Gate)
            {
                return Enabled.TryGetValue(feature, out var on) && on;
            }
        }

        /// <summary>
        /// Model version associated with the feature (or
        /// <see cref="NoModelVersion"/> when not enabled), for diagnostics.
        /// </summary>
        public static string GetModelVersion(MlAssistFeature feature)
        {
            lock (Gate)
            {
                return IsEnabled(feature)
                    ? ModelVersions.GetValueOrDefault(feature, NoModelVersion)
                    : NoModelVersion;
            }
        }

        /// <summary>
        /// Enable a feature for the given model version. The version must be
        /// non-empty so every ML-assisted run is attributable to a concrete
        /// model artifact.
        /// </summary>
        public static void Enable(MlAssistFeature feature, string modelVersion)
        {
            if (string.IsNullOrWhiteSpace(modelVersion))
            {
                throw new ArgumentException(
                    "An ML-assist feature cannot be enabled without a model version " +
                    "(issue #58: model version must be observable in diagnostics).",
                    nameof(modelVersion));
            }

            lock (Gate)
            {
                Enabled[feature] = true;
                ModelVersions[feature] = modelVersion;
            }
        }

        /// <summary>Disable an individual feature.</summary>
        public static void Disable(MlAssistFeature feature)
        {
            lock (Gate)
            {
                Enabled[feature] = false;
                ModelVersions[feature] = NoModelVersion;
            }
        }

        /// <summary>
        /// Immutable snapshot of the current configuration (respects
        /// DisableAll and the environment override) for diagnostics.
        /// </summary>
        public static MlAssistStatus GetStatus()
        {
            var active = new List<string>();
            var versions = new Dictionary<string, string>();

            foreach (var feature in Enum.GetValues<MlAssistFeature>())
            {
                if (IsEnabled(feature))
                {
                    active.Add(feature.ToString());
                    versions[feature.ToString()] = GetModelVersion(feature);
                }
            }

            return new MlAssistStatus
            {
                DisableAll = DisableAll || EnvDisableAll(),
                ActiveFeatures = active,
                ModelVersions = versions
            };
        }

        /// <summary>
        /// One-line, human-readable status for logs and diagnostics, e.g.
        /// <c>ml-assist: disabled (all features off)</c> or
        /// <c>ml-assist: LearnedPathPruning=1.2.0</c>.
        /// </summary>
        public static string Describe()
        {
            var status = GetStatus();
            if (status.ActiveFeatures.Count == 0)
            {
                return status.DisableAll
                    ? "ml-assist: disabled (disable-all)"
                    : "ml-assist: disabled (all features off)";
            }

            return "ml-assist: " + string.Join(
                ", ",
                status.ActiveFeatures.Select(f => $"{f}={status.ModelVersions[f]}"));
        }

        /// <summary>
        /// Reset all flags to the default (everything off). Intended for
        /// tests; production code should not need it.
        /// </summary>
        public static void ResetForTest()
        {
            lock (Gate)
            {
                foreach (var feature in Enum.GetValues<MlAssistFeature>())
                {
                    Enabled[feature] = false;
                    ModelVersions[feature] = NoModelVersion;
                }
            }

            DisableAll = false;
        }

        private static bool EnvDisableAll()
        {
            var value = Environment.GetEnvironmentVariable(DisableAllEnvVar);
            return string.Equals(value, "1", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
        }
    }
}
