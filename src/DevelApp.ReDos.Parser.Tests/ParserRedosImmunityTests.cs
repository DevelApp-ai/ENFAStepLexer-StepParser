using System.Linq;
using Xunit;
using Xunit.Abstractions;

namespace DevelApp.ReDos.Parser.Tests
{
    /// <summary>
    /// Parser-layer ReDoS-immunity enforcement (issue #103 extension):
    /// every ambiguity-stress grammar, at multiple token counts, must
    /// (a) complete inside the hard wall timeout and (b) end with at
    /// most <see cref="ParserRedosHarness.MaxActivePathBudget"/> active
    /// paths. The parser is where groups, alternation and anchors are
    /// actually evaluated - the lexer's forward-only matcher rejects
    /// them - and its immunity is by construction: the hard path budget
    /// prunes every step, so however exponential a grammar's parse
    /// forest is, the engine never holds more than the budget's live
    /// derivations. These tests pin that property in CI against the
    /// classic exponential- and Catalan-ambiguity shapes.
    /// </summary>
    public class ParserRedosImmunityTests
    {
        private readonly ITestOutputHelper _output;

        public ParserRedosImmunityTests(ITestOutputHelper output)
        {
            _output = output;
        }

        /// <summary>Every (grammar, size) pair in the stress catalog.</summary>
        public static TheoryData<string, int> GrammarSizeCases
        {
            get
            {
                var data = new TheoryData<string, int>();
                foreach (var grammar in GrammarRedosCatalog.AmbiguityStressGrammars)
                {
                    foreach (var size in grammar.ImmunitySizes)
                    {
                        data.Add(grammar.Name, size);
                    }
                }

                return data;
            }
        }

        [Theory]
        [MemberData(nameof(GrammarSizeCases))]
        public void StepParser_CompletesAmbiguousGrammar_WithinHardTimeoutAndPathBudget(string name, int size)
        {
            var grammar = GrammarRedosCatalog.AmbiguityStressGrammars.Single(g => g.Name == name);
            var input = grammar.BuildInput(size);
            var result = ParserRedosHarness.RunParse(grammar.GrammarText, input);

            Assert.True(
                result.Completed,
                $"{name} on {size} tokens did not complete within {ParserRedosHarness.ParseHardTimeoutMs} ms. "
                    + "This is a parser ReDoS regression: the GLR path budget must bound every ambiguity.");

            Assert.True(
                result.PathCount <= ParserRedosHarness.MaxActivePathBudget,
                $"{name} on {size} tokens ended with {result.PathCount} active paths, above the hard budget "
                    + $"of {ParserRedosHarness.MaxActivePathBudget}. The path-budget pruning must bound the ambiguity fan-out.");

            if (grammar.ExpectedSuccess is { } expected)
            {
                Assert.True(
                    result.Success == expected,
                    $"{name} on {size} tokens: expected Success={expected} but got {result.Success}.");
            }

            _output.WriteLine(
                $"{name} @ {size}: {result.ElapsedMilliseconds:F3} ms, success={result.Success}, paths={result.PathCount}, " +
                $"tokens={result.TokenCount}, errors={result.ErrorCount}");
        }

        [Fact]
        public void StepParser_BudgetBoundZeroTokens_CompletesWithoutHang()
        {
            // Empty input against a grammar that demands a token: the
            // lexer emits nothing, the parse fails fast with no
            // derivation - bounded, not hanging.
            var result = ParserRedosHarness.RunParse("Grammar: RedosEmptyFail\n<doc> ::= 'a'", string.Empty);

            Assert.True(result.Completed);
            Assert.False(result.Success);
            Assert.True(result.PathCount <= ParserRedosHarness.MaxActivePathBudget);
        }
    }
}
