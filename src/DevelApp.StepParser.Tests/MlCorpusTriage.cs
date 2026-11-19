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
        /// Grammars excluded by default: measured pathological at parse
        /// level on 2026-11-18 (one subprocess-isolated parse per
        /// grammar over that grammar's own corpus example file, 75 s
        /// hard cap; see the class summary for the method). Grammars
        /// missing from the corpus but present in this list are no-ops.
        /// </summary>
        internal static readonly IReadOnlyList<string> DefaultSkipList = new[]
        {
            // Over the 75 s cap before completing (parse-level blowup):
            "ABNF", "AdrSpec", "ApacheAccessCombined", "ApiSpec", "AsciiDoc",
            "Austrian", "Base64", "Bash", "Bulgarian", "C17", "ChefRecipe",
            "Clojure", "Cpp20", "Croatian", "Cue", "Czech", "Danish", "Dart",
            "Dhall", "Dockerfile", "Dutch", "EDN", "Elixir", "Erlang",
            "Estonian", "Fix44", "French", "GB_English", "German", "GitDiff",
            "GitHubActionsWorkflow", "Go119", "HOCON", "HTML", "Haskell",
            "Hungarian", "Italian", "Java17", "JavaScriptES2022", "Jsonnet",
            "KDL", "Kotlin", "LESS", "LaTeX", "Latin", "Lua", "Markdown",
            "MermaidJs", "Modern_Greek", "NomadHcl", "NsSpec", "PHP85",
            "Perl", "PlantUML", "Polish", "Portuguese", "PowerShell",
            "PrismaSchema", "PuppetManifest", "Python311", "RLang",
            "Romanian", "Ruby", "Rust2021", "SASS", "SPARQL", "SQL2023",
            "Scala", "Slovak", "Solidity", "SolutionManifest", "Spanish",
            "SrsSpec", "Swiss_German", "Swift", "Syslog", "TSQL2022",
            "TestSpec", "TypeScript", "US_English", "UnixLsOutput",
            "UnixPsOutput", "Vagrantfile", "WindowsDirOutput", "Xsd11",
            // Completed but too slow (>5 s) to multiply across capture and
            // the 2 arms x 3 runs of the end-to-end evaluation:
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
                reason = "default parse-level pathology skip list (measured 2026-11-18; see MlCorpusTriage summary)";
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
