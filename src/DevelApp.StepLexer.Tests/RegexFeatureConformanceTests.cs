using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Xunit.Abstractions;

namespace DevelApp.StepLexer.Tests
{
    /// <summary>
    /// Feature conformance suite for issue #104 (two-phase-regex article,
    /// "Feature Compatibility Analysis" table). Runs the committed corpus
    /// <c>Data/regex-conformance-corpus.tsv</c> through the two-phase engine
    /// (a single-rule greedy <see cref="StepLexer"/>, i.e. exactly what a
    /// grammar token rule compiles to) and through .NET <see cref="Regex"/>
    /// as the reference engine, diffs the observable result of every
    /// (pattern, input) case - match / no-match at position 0 and the matched
    /// text - and computes per-feature pass rates.
    ///
    /// CI wiring: this test runs in the normal <c>dotnet test</c> pipeline, so
    /// the per-feature floors below gate every PR. Additionally the
    /// <c>article-measurements</c> CI job sets <c>REGEX_CONFORMANCE_OUT</c> and
    /// uploads the per-case and per-feature CSVs as artifacts; run locally with:
    /// <code>
    ///   REGEX_CONFORMANCE_OUT=out dotnet test src/DevelApp.StepLexer.Tests \
    ///     --filter FullyQualifiedName~RegexFeatureConformanceTests
    /// </code>
    ///
    /// Semantics notes (pinned by the corpus, see the TSV header):
    /// - the engine rejects anchors, lookaround, backreferences, named groups
    ///   and scoped modifiers at compile time (forward-only NFA), so those
    ///   features measure as 0% support vs .NET - the "unsupported features
    ///   must be listed" outcome;
    /// - lazy quantifiers are normalized to "exactly the minimum" and
    ///   alternation takes the longest match (no backtracking), which can
    ///   diverge from .NET's leftmost-first backtracking; those two feature
    ///   tags are measured without a 100% floor;
    /// - zero-width reference matches pass iff the engine does not match
    ///   (zero-width matches are rejected by design, issue #111).
    /// </summary>
    public class RegexFeatureConformanceTests
    {
        private readonly ITestOutputHelper _output;

        public RegexFeatureConformanceTests(ITestOutputHelper output)
        {
            _output = output;
        }

        /// <summary>Features the engine must support exactly (100% pass floor).</summary>
        private static readonly string[] CoreFeatures =
        {
            "literal", "char-class", "dot", "escape-shorthand", "escape-hex",
            "escape-control", "unicode-class", "quantifier-greedy",
            "quantifier-bounded", "alternation", "group", "inline-modifier",
            "atomic-group", "quoted-literal", "comment-group",
        };

        /// <summary>Features rejected at compile time by design; the rejection itself is pinned.</summary>
        private static readonly string[] RejectedByDesign =
        {
            "anchor", "lookaround", "backreference", "named-group", "scoped-modifier",
        };

        private sealed record CorpusCase(string Feature, string Pattern, string Input, string Note, string? Expect);

        private sealed record CaseResult(
            string Feature, string Pattern, string Input,
            bool EngineCompiled, string? CompileError,
            bool EngineMatched, string EngineText,
            bool ReferenceInvalid, bool ReferenceDeclared, bool ReferenceMatched, string? ReferenceText,
            bool Pass, string? FailReason)
        {
            public string ReferenceTextOrEmpty => ReferenceMatched ? ReferenceText ?? "" : "";
            public string EngineTextOrEmpty => EngineMatched ? EngineText : "";
        }

        [Fact]
        public void Corpus_AgreesWithDotNetRegex_PerFeaturePassRates()
        {
            var corpusPath = Path.Combine(AppContext.BaseDirectory, "Data", "regex-conformance-corpus.tsv");
            Assert.True(File.Exists(corpusPath), $"conformance corpus missing at {corpusPath}");

            var cases = LoadCorpus(corpusPath);
            Assert.True(cases.Count >= 60, $"corpus too small: {cases.Count} cases");

            var results = cases.Select(RunCase).ToList();
            foreach (var r in results)
            {
                _output.WriteLine(
                    $"{r.Feature,-24} {(r.Pass ? "PASS" : "FAIL"),-4} engine={(r.EngineCompiled ? (r.EngineMatched ? "'" + r.EngineText + "'" : "no-match") : "reject")} " +
                    $"ref={(r.ReferenceInvalid ? "invalid" : r.ReferenceMatched ? "'" + r.ReferenceText + "'" : "no-match")} pattern=/{r.Pattern}/ input={EscapeForLog(r.Input)}" +
                    (r.FailReason == null ? "" : $"  ({r.FailReason})"));
            }

            WriteCsvIfRequested(results);

            var scorable = results.Where(r => !r.ReferenceInvalid).ToList();
            foreach (var r in results.Where(r => r.ReferenceInvalid))
            {
                Assert.True(false, $"reference engine rejected corpus pattern /{r.Pattern}/ - fix the corpus");
            }
            Assert.True(scorable.Count >= 60, $"too few scorable cases: {scorable.Count}");

            var byFeature = scorable
                .GroupBy(r => r.Feature)
                .ToDictionary(g => g.Key, g => (Total: g.Count(), Passed: g.Count(r => r.Pass), Failures: g.Where(r => !r.Pass).ToList()));

            foreach (var feature in byFeature.Keys.OrderBy(k => k, StringComparer.Ordinal))
            {
                var f = byFeature[feature];
                _output.WriteLine($"feature {feature,-24} pass {f.Passed}/{f.Total} = {(f.Total == 0 ? 0 : 100.0 * f.Passed / f.Total):F1}%");
            }

            // 1. Core features must be exact: 100% agreement with .NET Regex.
            foreach (var feature in CoreFeatures)
            {
                Assert.True(byFeature.TryGetValue(feature, out var f),
                    $"corpus has no cases for core feature '{feature}'");
                Assert.True(f.Passed == f.Total,
                    $"core feature '{feature}' must pass 100% but got {f.Passed}/{f.Total}: " +
                    string.Join("; ", f.Failures.Select(r => $"/{r.Pattern}/ on {EscapeForLog(r.Input)} -> {r.FailReason}")));
            }

            // 2. Rejected-by-design features: the engine must observably diverge
            //    from the .NET reference on every such case (0% support, listed).
            foreach (var feature in RejectedByDesign)
            {
                Assert.True(byFeature.TryGetValue(feature, out _),
                    $"corpus has no cases for rejected feature '{feature}'");
                foreach (var r in results.Where(r => r.Feature == feature))
                {
                    Assert.False(r.Pass,
                        $"feature '{feature}' pattern /{r.Pattern}/ unexpectedly agrees with the reference - " +
                        $"if the engine now supports it, move the tag out of RejectedByDesign and into CoreFeatures");
                }
            }

            // 3. Sanity floor on the overall pass rate (rejected features are a
            //    known, documented minority of the corpus).
            var overall = (double)scorable.Count(r => r.Pass) / scorable.Count;
            _output.WriteLine($"overall pass rate: {scorable.Count(r => r.Pass)}/{scorable.Count} = {100.0 * overall:F1}%");
            Assert.True(overall >= 0.5, $"overall pass rate {overall:P1} below sanity floor 50%");
        }

        /// <summary>
        /// Run one corpus case through both engines and diff the result.
        /// </summary>
        private static CaseResult RunCase(CorpusCase c)
        {
            var fullPattern = "/" + c.Pattern + "/";

            // Engine side: compile check (same path grammar loading uses), then a
            // single-rule greedy lex over the input; a token at position 0 is
            // the engine's "match at 0" answer.
            var engineCompiled = StepLexer.TryValidateRulePattern(fullPattern, out var compileError);
            var engineMatched = false;
            var engineText = "";
            if (engineCompiled)
            {
                var lexer = new StepLexer { Mode = LexingMode.GreedyLongestMatch };
                lexer.AddRule(new TokenRule("T", fullPattern));
                var bytes = Encoding.UTF8.GetBytes(c.Input);
                lexer.Initialize(bytes, "conformance.tsv");
                StepToken? first = null;
                var guard = 0;
                while (lexer.ActivePaths.Any(p => p.IsValid && p.Position < bytes.Length))
                {
                    var step = lexer.Step();
                    if (first == null)
                    {
                        first = step.NewTokens.FirstOrDefault(t => t.StartPosition == 0 && t.Length > 0);
                    }
                    if (++guard > 1_000_000)
                    {
                        break;
                    }
                }
                if (first != null)
                {
                    engineMatched = true;
                    engineText = first.Value;
                }
            }

            // Reference side: .NET Regex anchored at position 0. Patterns .NET
            // cannot compile at all (possessive quantifiers, \Q...\E) fall back
            // to the corpus-declared expect column (PCRE semantics).
            var referenceInvalid = false;
            var referenceDeclared = false;
            var referenceMatched = false;
            var referenceZeroWidth = false;
            string? referenceText = null;
            try
            {
                var regex = new Regex("\\A(?:" + c.Pattern + ")", RegexOptions.None, TimeSpan.FromSeconds(10));
                var m = regex.Match(c.Input);
                referenceMatched = m.Success;
                referenceZeroWidth = m.Success && m.Length == 0;
                referenceText = m.Success ? m.Value : null;
            }
            catch (ArgumentException) when (c.Expect != null)
            {
                referenceDeclared = true;
                referenceMatched = c.Expect.StartsWith("match:", StringComparison.Ordinal);
                referenceText = referenceMatched ? c.Expect.Substring("match:".Length) : null;
            }
            catch (RegexMatchTimeoutException) when (c.Expect != null)
            {
                referenceDeclared = true;
                referenceMatched = c.Expect.StartsWith("match:", StringComparison.Ordinal);
                referenceText = referenceMatched ? c.Expect.Substring("match:".Length) : null;
            }
            catch (ArgumentException)
            {
                referenceInvalid = true;
            }
            catch (RegexMatchTimeoutException)
            {
                referenceInvalid = true;
            }

            bool pass;
            string? failReason;
            if (referenceInvalid)
            {
                pass = false;
                failReason = "reference engine rejected the pattern (excluded from scoring)";
            }
            else if (!engineCompiled)
            {
                pass = false;
                failReason = "engine rejected at compile time: " + compileError;
            }
            else if (referenceZeroWidth)
            {
                // Zero-width reference match: the engine rejects zero-width matches
                // by design (issue #111), so observable behavior agrees iff the
                // engine does not match.
                pass = !engineMatched;
                failReason = pass ? null : "engine matched where the reference matches zero-width";
            }
            else
            {
                pass = engineMatched == referenceMatched && (!referenceMatched || engineText == referenceText);
                failReason = pass ? null
                    : $"engine={(engineMatched ? "'" + engineText + "'" : "no-match")} vs ref={(referenceMatched ? "'" + referenceText + "'" : "no-match")}";
            }

            return new CaseResult(c.Feature, c.Pattern, c.Input,
                engineCompiled, compileError, engineMatched, engineText,
                referenceInvalid, referenceDeclared, referenceMatched, referenceText, pass, failReason);
        }

        private static List<CorpusCase> LoadCorpus(string path)
        {
            var cases = new List<CorpusCase>();
            foreach (var rawLine in File.ReadAllLines(path))
            {
                var line = rawLine.TrimEnd('\r');
                if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal))
                {
                    continue;
                }
                var parts = line.Split('\t');
                Assert.True(parts.Length == 4 || parts.Length == 5, $"bad corpus line ({parts.Length} fields): {line}");
                if (parts[0] == "feature")
                {
                    continue; // header row
                }
                cases.Add(new CorpusCase(parts[0], parts[1], DecodeInputEscapes(parts[2]), parts[3],
                    parts.Length == 5 ? parts[4] : null));
            }
            return cases;
        }

        /// <summary>Decode the harness-level input escapes (\\, \n, \t) in corpus input fields.</summary>
        private static string DecodeInputEscapes(string input)
        {
            if (input.IndexOf('\\') < 0)
            {
                return input;
            }
            var sb = new StringBuilder(input.Length);
            for (var i = 0; i < input.Length; i++)
            {
                if (input[i] == '\\' && i + 1 < input.Length)
                {
                    switch (input[i + 1])
                    {
                        case '\\': sb.Append('\\'); i++; continue;
                        case 'n': sb.Append('\n'); i++; continue;
                        case 't': sb.Append('\t'); i++; continue;
                    }
                }
                sb.Append(input[i]);
            }
            return sb.ToString();
        }

        private static string EscapeForLog(string text) =>
            text.Replace("\\", "\\\\").Replace("\n", "\\n").Replace("\t", "\\t").Replace("\r", "\\r");

        private static string Csv(string field) =>
            "\"" + field.Replace("\"", "\"\"").Replace("\n", "\\n").Replace("\r", "\\r") + "\"";

        private void WriteCsvIfRequested(List<CaseResult> results)
        {
            var outDir = Environment.GetEnvironmentVariable("REGEX_CONFORMANCE_OUT");
            if (string.IsNullOrEmpty(outDir))
            {
                return;
            }
            Directory.CreateDirectory(outDir);

            var casesCsv = new StringBuilder();
            casesCsv.AppendLine("feature,pattern,input,engine_compiled,compile_error,engine_matched,engine_text,reference_declared,reference_matched,reference_text,pass,fail_reason");
            foreach (var r in results)
            {
                casesCsv.AppendLine(string.Join(",",
                    Csv(r.Feature), Csv("/" + r.Pattern + "/"), Csv(r.Input),
                    r.EngineCompiled ? "true" : "false", Csv(r.CompileError ?? ""),
                    r.EngineMatched ? "true" : "false", Csv(r.EngineTextOrEmpty),
                    r.ReferenceDeclared ? "declared" : "dotnet",
                    r.ReferenceInvalid ? "invalid" : r.ReferenceMatched ? "true" : "false",
                    Csv(r.ReferenceTextOrEmpty),
                    r.Pass ? "true" : "false", Csv(r.FailReason ?? "")));
            }
            File.WriteAllText(Path.Combine(outDir, "regex-conformance-cases.csv"), casesCsv.ToString());

            var scorable = results.Where(r => !r.ReferenceInvalid).ToList();
            var summaryCsv = new StringBuilder();
            summaryCsv.AppendLine("feature,cases,passed,pass_rate_pct");
            foreach (var g in scorable.GroupBy(r => r.Feature).OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                var total = g.Count();
                var passed = g.Count(r => r.Pass);
                summaryCsv.AppendLine($"{Csv(g.Key)},{total},{passed},{100.0 * passed / total:F2}");
            }
            summaryCsv.AppendLine($"{Csv("OVERALL")},{scorable.Count},{scorable.Count(r => r.Pass)},{100.0 * scorable.Count(r => r.Pass) / scorable.Count:F2}");
            File.WriteAllText(Path.Combine(outDir, "regex-feature-summary.csv"), summaryCsv.ToString());
        }
    }
}
