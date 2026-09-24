using System;
using System.Text;
using DevelApp.StepParser;
using Xunit;

namespace DevelApp.StepParser.Tests
{
    /// <summary>
    /// Guard rails for ML-assisted optimization work (issue #58, sub-issue
    /// #77):
    ///  1. The ML-assist switch must default to fully disabled and stay
    ///     observable/disable-able at runtime.
    ///  2. GLR path handling on an ambiguous grammar must keep allocations
    ///     bounded (linear growth per input term) so the later learned
    ///     path-pruning prototype (#75) has a hard regression baseline.
    ///
    /// The flag tests deliberately run as one sequential [Fact] because they
    /// mutate the process-wide static configuration.
    /// </summary>
    public class MlGuardRailTests : IDisposable
    {
        public MlGuardRailTests()
        {
            MlAssistOptions.ResetForTest();
            Environment.SetEnvironmentVariable(MlAssistOptions.DisableAllEnvVar, null);
        }

        public void Dispose()
        {
            MlAssistOptions.ResetForTest();
            Environment.SetEnvironmentVariable(MlAssistOptions.DisableAllEnvVar, null);
        }

        // ------------------------------------------------------------------
        // 1. Switch behavior
        // ------------------------------------------------------------------

        [Fact]
        public void MlAssistSwitch_DefaultsOff_AndCanBeForcedOffAtRuntime()
        {
            // Default: everything disabled, no model versions reported.
            foreach (var feature in Enum.GetValues<MlAssistFeature>())
            {
                Assert.False(MlAssistOptions.IsEnabled(feature),
                    $"{feature} must be disabled by default (issue #58: deterministic behavior ships by default)");
                Assert.Equal(MlAssistOptions.NoModelVersion, MlAssistOptions.GetModelVersion(feature));
            }

            var status = MlAssistOptions.GetStatus();
            Assert.False(status.DisableAll);
            Assert.Empty(status.ActiveFeatures);
            Assert.Empty(status.ModelVersions);
            Assert.Contains("disabled", MlAssistOptions.Describe());

            // Enabling requires an attributable model version.
            Assert.Throws<ArgumentException>(
                () => MlAssistOptions.Enable(MlAssistFeature.LearnedPathPruning, "  "));

            // Individual enable with a version is observable in status and diagnostics.
            MlAssistOptions.Enable(MlAssistFeature.LearnedPathPruning, "1.2.0");
            Assert.True(MlAssistOptions.IsEnabled(MlAssistFeature.LearnedPathPruning));
            Assert.Equal("1.2.0", MlAssistOptions.GetModelVersion(MlAssistFeature.LearnedPathPruning));
            Assert.Contains("LearnedPathPruning=1.2.0", MlAssistOptions.Describe());

            // Master switch overrides individual flags.
            MlAssistOptions.DisableAll = true;
            Assert.False(MlAssistOptions.IsEnabled(MlAssistFeature.LearnedPathPruning));
            Assert.True(MlAssistOptions.GetStatus().DisableAll);
            Assert.Empty(MlAssistOptions.GetStatus().ActiveFeatures);
            Assert.Contains("disable-all", MlAssistOptions.Describe());

            // Runtime escape hatch without a rebuild: the environment variable.
            MlAssistOptions.DisableAll = false;
            MlAssistOptions.Enable(MlAssistFeature.LearnedRulePrioritization, "0.1.0");
            Assert.True(MlAssistOptions.IsEnabled(MlAssistFeature.LearnedRulePrioritization));
            Environment.SetEnvironmentVariable(MlAssistOptions.DisableAllEnvVar, "1");
            Assert.False(MlAssistOptions.IsEnabled(MlAssistFeature.LearnedRulePrioritization),
                "DEVELAPP_STEPML_DISABLE_ALL must force every feature off");
            Assert.True(MlAssistOptions.GetStatus().DisableAll);
            Environment.SetEnvironmentVariable(MlAssistOptions.DisableAllEnvVar, "TRUE");
            Assert.False(MlAssistOptions.IsEnabled(MlAssistFeature.LearnedRulePrioritization));
            Environment.SetEnvironmentVariable(MlAssistOptions.DisableAllEnvVar, null);
            Assert.True(MlAssistOptions.IsEnabled(MlAssistFeature.LearnedRulePrioritization),
                "Clearing the environment variable restores the configured state");
        }

        [Fact]
        public void StepParserEngine_ReportsMlAssistStatusInDiagnostics()
        {
            using var engine = new StepParserEngine();
            Assert.Contains("ml-assist: disabled", engine.MlAssistStatus);

            MlAssistOptions.Enable(MlAssistFeature.HotspotStrategySelection, "0.3.1");
            Assert.Contains("HotspotStrategySelection=0.3.1", engine.MlAssistStatus);
        }

        // ------------------------------------------------------------------
        // 2. Allocation guard for GLR path handling on ambiguous grammars
        // ------------------------------------------------------------------

        private const string AmbiguousExprGrammar = @"
Grammar: AmbiguousExpr
<NUMBER> ::= /[0-9]+/
<PLUS> ::= '+'
<expr> ::= <expr> '+' <expr>
<expr> ::= <NUMBER>
<start> ::= <expr>
";

        private static string GenerateSum(int terms)
        {
            var sb = new StringBuilder();
            for (var i = 0; i < terms; i++)
            {
                if (i > 0)
                {
                    sb.Append('+');
                }
                sb.Append((i * 7) % 10);
            }
            return sb.ToString();
        }

        /// <summary>
        /// Measure bytes allocated for one full parse of a sum with the given
        /// number of terms, using a fresh engine per measurement. The minimum
        /// of two runs is taken to tolerate noise from parallel tests.
        /// </summary>
        private static double MeasureAllocatedPerTerm(int terms)
        {
            double best = double.MaxValue;
            for (var run = 0; run < 2; run++)
            {
                using var engine = new StepParserEngine();
                engine.LoadGrammarFromContent(AmbiguousExprGrammar);

                // Warm up the code paths outside the measurement window so
                // first-run JIT/lazy-init allocations do not skew the result.
                engine.Parse(GenerateSum(Math.Min(terms, 4)), "warmup.txt");

                long allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
                var result = engine.Parse(GenerateSum(terms), "measure.txt");
                long allocated = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;

                Assert.True(result.Success,
                    $"Ambiguous parse of {terms} terms must succeed (guard-rail test sanity), errors: " +
                    string.Join("; ", result.Errors));

                best = Math.Min(best, (double)allocated / terms);
            }

            return best;
        }

        [Fact]
        public void GlrPathHandling_AllocationsGrowLinearlyWithAmbiguousInput()
        {
            // Sub-issue #77 baseline for the learned path-pruning prototype
            // (#75): allocation per input term on an ambiguous grammar must
            // stay (generously) sub-quadratic. GLR on E ::= E '+' E packs
            // the ambiguity instead of materializing every parse tree; if
            // that packing regresses, per-term cost explodes long before
            // correctness tests notice.
            var small = MeasureAllocatedPerTerm(25);
            var large = MeasureAllocatedPerTerm(100);

            Assert.True(large <= small * 2.5,
                $"Per-term allocation grew super-linearly on the ambiguous grammar: " +
                $"{small:F0} bytes/term at 25 terms vs {large:F0} bytes/term at 100 terms");
        }
    }
}
