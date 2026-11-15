using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Xunit;
using Xunit.Abstractions;

namespace DevelApp.ReDos.Parser.Tests
{
    /// <summary>The parser-layer measurement entry for one grammar at one size.</summary>
    public sealed class ParserMeasurement
    {
        /// <summary>Catalog name of the grammar.</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>The input size (tokens for stress grammars, bytes for scaling grammars).</summary>
        public int Size { get; set; }

        /// <summary>Whether the parse completed within the hard timeout.</summary>
        public bool Completed { get; set; }

        /// <summary>Whether the input derived.</summary>
        public bool Success { get; set; }

        /// <summary>Active paths reported by the engine after the run.</summary>
        public int PathCount { get; set; }

        /// <summary>Tokens the lexer emitted.</summary>
        public int TokenCount { get; set; }

        /// <summary>Errors recorded by the engine.</summary>
        public int ErrorCount { get; set; }

        /// <summary>Measured parse time in milliseconds.</summary>
        public double Milliseconds { get; set; }
    }

    /// <summary>The parser-layer measurement report (issue #103 extension).</summary>
    public sealed class ParserRedosReport
    {
        /// <summary>When the report was generated (UTC).</summary>
        public string GeneratedAtUtc { get; set; } = string.Empty;

        /// <summary>Engine under test (name + assembly version).</summary>
        public string EngineUnderTest { get; set; } = string.Empty;

        /// <summary>.NET runtime of the measurement run.</summary>
        public string DotNetRuntime { get; set; } = string.Empty;

        /// <summary>Operating system of the measurement run.</summary>
        public string OperatingSystem { get; set; } = string.Empty;

        /// <summary>Logical processors available.</summary>
        public int ProcessorCount { get; set; }

        /// <summary>Hard wall timeout applied to every parse, in milliseconds.</summary>
        public int ParseHardTimeoutMs { get; set; }

        /// <summary>The hard active-path budget asserted throughout.</summary>
        public int MaxActivePathBudget { get; set; }

        /// <summary>Per-grammar measurements.</summary>
        public List<ParserMeasurement> Measurements { get; set; } = new();

        /// <summary>Serialize the report to indented JSON.</summary>
        public string ToJson() =>
            JsonSerializer.Serialize(this, new JsonSerializerOptions
            {
                WriteIndented = true,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            });
    }

    /// <summary>
    /// The parser-layer measurement report test (issue #103 extension):
    /// runs the full grammar catalog at representative sizes under the
    /// hard timeout, asserts the path-budget bound on every outcome, and
    /// writes a JSON artifact next to the test assembly. Unlike the
    /// lexer-layer suite this project restores the private
    /// CognitiveGraph feed, so it only builds in CI: the artifact is a
    /// CI-side record (visible in the test logs) and the committed
    /// documentation summarizes the asserted invariants rather than
    /// raw locally-generated numbers.
    /// </summary>
    public class ParserRedosReportTests
    {
        private readonly ITestOutputHelper _output;

        public ParserRedosReportTests(ITestOutputHelper output)
        {
            _output = output;
        }

        [Fact]
        public void ParserMeasurementReport_IsGenerated_WithBoundedPathsForEveryGrammar()
        {
            var report = new ParserRedosReport
            {
                GeneratedAtUtc = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                EngineUnderTest = ParserRedosHarness.StepParserEngineVersion(),
                DotNetRuntime = RuntimeInformation.FrameworkDescription,
                OperatingSystem = RuntimeInformation.OSDescription,
                ProcessorCount = Environment.ProcessorCount,
                ParseHardTimeoutMs = ParserRedosHarness.ParseHardTimeoutMs,
                MaxActivePathBudget = ParserRedosHarness.MaxActivePathBudget,
            };

            foreach (var grammar in GrammarRedosCatalog.AmbiguityStressGrammars.Concat(GrammarRedosCatalog.ScalingGrammars))
            {
                // The largest cataloged size is the representative
                // adversarial measurement for the report.
                var size = grammar.ImmunitySizes.Max();
                var input = grammar.BuildInput(size);
                var result = ParserRedosHarness.RunParse(grammar.GrammarText, input);

                Assert.True(
                    result.Completed,
                    $"{grammar.Name} did not complete at size {size} within the hard timeout.");

                Assert.True(
                    result.PathCount <= ParserRedosHarness.MaxActivePathBudget,
                    $"{grammar.Name} ended with {result.PathCount} active paths at size {size}, above the hard budget.");

                if (grammar.ExpectedSuccess is { } expected)
                {
                    Assert.True(
                        result.Success == expected,
                        $"{grammar.Name}: expected Success={expected} at size {size} but got {result.Success}.");
                }

                report.Measurements.Add(new ParserMeasurement
                {
                    Name = grammar.Name,
                    Size = size,
                    Completed = result.Completed,
                    Success = result.Success,
                    PathCount = result.PathCount,
                    TokenCount = result.TokenCount,
                    ErrorCount = result.ErrorCount,
                    Milliseconds = result.ElapsedMilliseconds,
                });
            }

            var artifactPath = Path.Combine(AppContext.BaseDirectory, "redos-parser-immunity-report.json");
            File.WriteAllText(artifactPath, report.ToJson());

            _output.WriteLine($"Report written to {artifactPath}");
            _output.WriteLine($"Engine under test: {report.EngineUnderTest}");
            foreach (var measurement in report.Measurements)
            {
                _output.WriteLine(
                    $"{measurement.Name} @ {measurement.Size}: {measurement.Milliseconds:F3} ms, success={measurement.Success}, " +
                    $"paths={measurement.PathCount}, tokens={measurement.TokenCount}, errors={measurement.ErrorCount}");
            }
        }
    }
}
