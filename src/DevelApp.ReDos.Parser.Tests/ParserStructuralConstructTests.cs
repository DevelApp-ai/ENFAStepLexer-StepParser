using Xunit;
using Xunit.Abstractions;

namespace DevelApp.ReDos.Parser.Tests
{
    /// <summary>
    /// Parser-layer structural-construct semantics (issue #103
    /// extension): alternation, nested groups, repetition, optionals,
    /// ambiguous sibling lexing, regex terminals and the
    /// end-of-input anchor - the constructs the lexer's forward-only
    /// matcher rejects - evaluated through the real grammar pipeline
    /// on tiny inputs with deterministic outcomes. These tests pin
    /// that the parser actually evaluates the constructs (the
    /// lexer/parser split Lars called out: the tests must test the
    /// evolved integration), and that every outcome lands within the
    /// hard path budget.
    /// </summary>
    public class ParserStructuralConstructTests
    {
        private readonly ITestOutputHelper _output;

        public ParserStructuralConstructTests(ITestOutputHelper output)
        {
            _output = output;
        }

        /// <summary>
        /// Run one grammar against one input and assert the expected
        /// parse outcome plus the path-budget bound, returning the
        /// result for logging.
        /// </summary>
        private ParserRunResult AssertOutcome(string grammarText, string input, bool expectedSuccess)
        {
            var result = ParserRedosHarness.RunParse(grammarText, input);

            Assert.True(result.Completed, "The parse must complete within the hard timeout.");
            Assert.True(
                result.Success == expectedSuccess,
                $"Expected Success={expectedSuccess} but got {result.Success} " +
                $"(errors={result.ErrorCount}, tokens={result.TokenCount}).");
            Assert.True(
                result.PathCount <= ParserRedosHarness.MaxActivePathBudget,
                $"PathCount {result.PathCount} exceeds the hard budget of {ParserRedosHarness.MaxActivePathBudget}.");

            _output.WriteLine(
                $"success={result.Success}, paths={result.PathCount}, tokens={result.TokenCount}, {result.ElapsedMilliseconds:F3} ms");
            return result;
        }

        [Fact]
        public void Alternation_MatchesEitherBranch()
        {
            const string grammar = "Grammar: Alt\n<doc> ::= <v>\n<v> ::= 'x' | 'y'";

            AssertOutcome(grammar, "x", true);
            AssertOutcome(grammar, "y", true);
            AssertOutcome(grammar, "z", false);
        }

        [Fact]
        public void MultiSymbolAlternation_MatchesBranchSequences()
        {
            const string grammar = "Grammar: PairAlt\n<doc> ::= <p>\n<p> ::= 'a' 'b' | 'b' 'a'";

            AssertOutcome(grammar, "a b", true);
            AssertOutcome(grammar, "b a", true);
            AssertOutcome(grammar, "a a", false);
        }

        [Fact]
        public void NestedGroupWithQuantifier_ConsumesWholeRepetitions()
        {
            const string grammar = "Grammar: GroupRep\n<doc> ::= ( 'a' 'b' )+";

            AssertOutcome(grammar, "a b", true);
            AssertOutcome(grammar, "a b a b", true);
            AssertOutcome(grammar, "a b a", false);
        }

        [Fact]
        public void NestedRepetitionInsideGroup_EvaluatesWithoutBacktracking()
        {
            // ( ( 'a' )* 'a' )+ on a three-token run: every inner split is
            // a live path, the budget must keep the fan-out bounded and
            // the parse must still succeed.
            const string grammar = "Grammar: NestedRep\n<doc> ::= ( ( 'a' )* 'a' )+";

            AssertOutcome(grammar, "a a a", true);
        }

        [Fact]
        public void OptionalGroup_AcceptsPresentAndAbsent()
        {
            const string grammar = "Grammar: Opt\n<doc> ::= 'a' ( 'b' )?";

            AssertOutcome(grammar, "a", true);
            AssertOutcome(grammar, "a b", true);
            AssertOutcome(grammar, "a b b", false);
        }

        [Fact]
        public void DuplicateAlternatives_StayWithinPathBudget_AndParse()
        {
            const string grammar = "Grammar: Dup\n<doc> ::= ( 'a' | 'a' )+";

            AssertOutcome(grammar, "a a a a a a a a", true);
        }

        [Fact]
        public void AmbiguousSiblingLex_TwoTokenRulesSameText_ParseWithinBudget()
        {
            // Two token rules matching the same text produce sibling
            // tokens (the ambiguous-lex shape); the grammar accepts
            // either, so the parse succeeds and the budget bounds the
            // sibling fan-out.
            const string grammar = "Grammar: Sibling\n<a> ::= 'a'\n<b> ::= 'a'\n<doc> ::= <a> | <b>";

            AssertOutcome(grammar, "a", true);
        }

        [Fact]
        public void RegexTerminalInProduction_MaterializesAndParses()
        {
            // A /regex/ written directly in a production right-hand side
            // is materialized as a lexer rule (issue #83) and parses.
            const string grammar = "Grammar: RegexTerm\n<doc> ::= /[0-9]+/";

            var result = AssertOutcome(grammar, "123", true);
            Assert.Equal(1, result.TokenCount);
        }

        [Fact]
        public void EndOfInputAnchor_TrailingTerminalDemandsFullConsumption()
        {
            // <doc> ::= 'a' 'b': the missing trailing 'b' must fail the
            // parse - the anchored-fail shape, the parser-layer
            // equivalent of the $-anchored corpus patterns.
            const string grammar = "Grammar: DemandTail\n<doc> ::= 'a' 'b'";

            AssertOutcome(grammar, "a b", true);
            AssertOutcome(grammar, "a", false);
        }

        [Fact]
        public void WhitespaceBetweenTokens_IsSkipped()
        {
            // The implicit __default_ws rule keeps inter-token
            // whitespace insignificant.
            const string grammar = "Grammar: Ws\n<doc> ::= 'a' 'b'";

            AssertOutcome(grammar, "a  b", true);
        }

        [Fact]
        public void EmptyInput_FailsFastWithoutHang()
        {
            const string grammar = "Grammar: Empty\n<doc> ::= 'a'";

            AssertOutcome(grammar, string.Empty, false);
        }

        [Fact]
        public void CatalanGrammar_TinyInput_ParsesWithinBudget()
        {
            const string grammar = "Grammar: CatalanTiny\n<s> ::= 'a' <s> <s>\n<s> ::= 'a'";

            // Five a's: Catalan(4) = 14 binary trees over the inner run -
            // ambiguity the budget must hold while still deriving.
            AssertOutcome(grammar, "a a a a a", true);
        }
    }
}
