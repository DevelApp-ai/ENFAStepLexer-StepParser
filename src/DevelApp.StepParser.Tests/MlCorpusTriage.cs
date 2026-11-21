using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace DevelApp.StepParser.Tests
{
    /// <summary>
    /// Containment boundary for the ML-assist corpus harnesses
    /// (<see cref="MlLabelTraceTests"/>, <see cref="MlAssistEvaluationTests"/>).
    ///
    /// The grammar repository contains a large share of grammars whose
    /// parse-level behavior on the step-based GLR is pathological: the
    /// engine burns minutes (and, under capture load, crashes the test
    /// host) on a few hundred bytes of input. Measured on 2026-11-18
    /// (Release, .NET 8, single subprocess per grammar, 75 s hard cap):
    /// of the 128 corpus grammars, 82 exceeded the cap on their own
    /// corpus example file (at most ~11 KB), and four more needed over
    /// 5 s. The worst measured case parsed a 611-byte example into
    /// 840,306 token observations in 47 s and still reported failure,
    /// and the same example OOM-killed the test host during label
    /// capture. These grammars are excluded from corpus-scale capture
    /// and evaluation runs by default so that training and end-to-end
    /// measurement always complete; the exclusion list is reported in
    /// the coverage file each harness writes next to its output, and
    /// skipped grammars stay available for the engine work that will
    /// eventually lift them out of this list.
    ///
    /// Re-triaged 2026-11-21 on main after the issue #157 fixes
    /// (#167, #168, #169 and the NL-morphology scale fix in #170;
    /// Release, .NET 8.0.412, single process per grammar, 75 s hard
    /// cap, 220 corpus example pairs): every grammar now completes on
    /// its own example file within 17 s and none exceeds any cap, and
    /// the former worst case (the GitHub Actions workflow example)
    /// parses in 0.1 s. The 82-name pathology list measured on
    /// 2026-11-18 is therefore emptied below, and only the
    /// natural-language morphology grammars (the most ambiguous
    /// grammars in the corpus, ~850 overlapping token rules each)
    /// stay excluded by default: their 5.7-17 s parse times are still
    /// too slow to multiply across label capture and the 2 arms x 3
    /// runs of the end-to-end evaluation.
    ///
    /// Environment variables (all optional):
    ///  - ML_SKIP_GRAMMARS: comma-separated grammar names (file name
    ///    without extension) to skip in ADDITION to the defaults; a
    ///    leading '+' makes the list exhaustive (skip ONLY these).
    ///  - ML_CAPTURE_BUDGET_MINUTES: when set, harnesses stop covering
    ///    further grammars once the wall-clock budget is spent and write
    ///    what was covered so far (keeps a run usable on slow machines).
    /// </summary>
    internal static class MlCorpusTriage
    {
        internal const string SkipListEnvVar = "ML_SKIP_GRAMMARS";
        internal const string BudgetMinutesEnvVar = "ML_CAPTURE_BUDGET_MINUTES";

        /// <summary>
        /// Grammars excluded by default: the natural-language morphology
        /// grammars measured at 5.7-17 s per parse on 2026-11-21 (see
        /// the class summary for the method and history). Every corpus
        /// grammar completes on its own example file within the 75 s
        /// cap since the issue #157 fixes, so no grammar is excluded
        /// for exceeding a cap anymore; these stay excluded only
        /// because their parse time is too slow to multiply across
        /// capture and the end-to-end evaluation. Grammars missing
        /// from the corpus but present in this list are no-ops.
        /// </summary>
        internal static readonly IReadOnlyList<string> DefaultSkipList = new[]
        {
            // NL morphology grammars: complete well under the 75 s cap
            // (re-triaged 2026-11-21; measured at 5.7-17 s on their own
            // corpus example file) but still too slow to multiply across
            // label capture and the 2 arms x 3 runs of the evaluation:
            "Spanish",
            "German",
            "Portuguese",
            "Danish",
            "Latin",
            "GB_English",
            "US_English",
            "Swiss_German",
            "Austrian",
            "Modern_Greek",
            "Ancient_Greek",
        };

        /// <summary>Returns whether the grammar should be excluded from the run.</summary>
        internal static bool ShouldSkip(string grammarFileName, out string reason)
        {
            var name = Path.GetFileNameWithoutExtension(grammarFileName ?? string.Empty);
            var env = Environment.GetEnvironmentVariable(SkipListEnvVar);
            if (!string.IsNullOrWhiteSpace(env))
            {
                var extra = env!
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .ToList();
                var exclusive = extra.Remove("+");
                var effective = exclusive ? extra : DefaultSkipList.Concat(extra).ToList();
                if (effective.Contains(name, StringComparer.Ordinal))
                {
                    reason = exclusive
                        ? $"explicitly skipped via {SkipListEnvVar}"
                        : $"skip-listed via {SkipListEnvVar}";
                    return true;
                }

                reason = string.Empty;
                return false;
            }

            if (DefaultSkipList.Contains(name, StringComparer.Ordinal))
            {
                reason = "default slow-to-multiply NL morphology skip list (measured 2026-11-21; see MlCorpusTriage summary)";
                return true;
            }

            reason = string.Empty;
            return false;
        }

        /// <summary>Returns true once the optional wall-clock budget is spent.</summary>
        internal static bool BudgetExceeded(DateTime startedUtc, out string message)
        {
            message = string.Empty;
            var raw = Environment.GetEnvironmentVariable(BudgetMinutesEnvVar);
            if (string.IsNullOrWhiteSpace(raw))
            {
                return false;
            }

            if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var minutes)
                || minutes <= 0)
            {
                return false;
            }

            var elapsed = DateTime.UtcNow - startedUtc;
            if (elapsed.TotalMinutes < minutes)
            {
                return false;
            }

            message = $"wall-clock budget of {minutes.ToString(CultureInfo.InvariantCulture)} min spent " +
                $"after {elapsed.TotalMinutes:F1} min - stopping corpus coverage here";
            return true;
        }
    }
}
