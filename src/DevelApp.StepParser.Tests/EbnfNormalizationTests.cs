using System.Linq;
using Xunit;
using DevelApp.StepParser;

namespace DevelApp.StepParser.Tests
{
    /// <summary>
    /// Regression tests for CEBNF-to-BNF production normalization
    /// (issue #83, Cluster C): optional <c>?</c>, repetition
    /// <c>*</c>/<c>+</c>, parenthesized groups with inline alternation,
    /// <c>@NAME[...]</c> annotations, regex terminals written directly in
    /// production right-hand sides, and whitespace-only tokens treated as
    /// legitimately absent.
    /// </summary>
    public class EbnfNormalizationTests
    {
        /// <summary>Grammar exercising every quantifier shape at once.</summary>
        private const string EbnfGrammar = @"
Grammar: EbnfShapes
<word> ::= /[a-z]+/
<num> ::= /[0-9]+/
<comma> ::= ','
<list> ::= <item> ( ',' <item> )*
<item> ::= <word> <num>?
";

        [Fact]
        public void Optional_ExpandsToPresentAndAbsentAlternatives()
        {
            var engine = new StepParserEngine();
            engine.LoadGrammarFromContent(EbnfGrammar);
            var grammar = engine.CurrentGrammar!;

            var item = grammar.ProductionRules.Where(r => r.Name == "item").ToList();
            // <word> <num>? must yield both <word> <num> and <word>.
            Assert.Contains(item, r => r.RightHandSide.SequenceEqual(new[] { "word", "num" }));
            Assert.Contains(item, r => r.RightHandSide.SequenceEqual(new[] { "word" }));
        }

        [Fact]
        public void RepetitionGroup_SynthesizesRightRecursiveHelper()
        {
            var engine = new StepParserEngine();
            engine.LoadGrammarFromContent(EbnfGrammar);
            var grammar = engine.CurrentGrammar!;

            // <list> ::= <item> ( ',' <item> )* must become
            // <list> ::= <item> <list__repN> and helper rules
            // <list__repN> ::= ',' <item> <list__repN> | ',' <item>.
            var list = grammar.ProductionRules.FirstOrDefault(r => r.Name == "list");
            Assert.NotNull(list);
            Assert.Equal(2, list!.RightHandSide.Count);
            Assert.StartsWith("list", list.RightHandSide[1]);
            Assert.Contains("__rep", list.RightHandSide[1]);

            var helperName = list.RightHandSide[1];
            var helper = grammar.ProductionRules.Where(r => r.Name == helperName).ToList();
            Assert.NotEmpty(helper);
            Assert.Contains(helper, r => r.RightHandSide.SequenceEqual(new[] { "comma", "item", helperName }));
            Assert.Contains(helper, r => r.RightHandSide.SequenceEqual(new[] { "comma", "item" }));
        }

        [Fact]
        public void QuantifiedSequence_SynthesizesHelperForPlus()
        {
            const string plusGrammar = @"
Grammar: PlusShape
<digit> ::= /[0-9]/
<number> ::= <digit>+
";
            var engine = new StepParserEngine();
            engine.LoadGrammarFromContent(plusGrammar);
            var grammar = engine.CurrentGrammar!;

            var number = grammar.ProductionRules.FirstOrDefault(r => r.Name == "number");
            Assert.NotNull(number);
            // <digit>+ must reference a helper (never a literal '+' symbol).
            Assert.Single(number!.RightHandSide);
            Assert.StartsWith("number", number.RightHandSide[0]);
            Assert.Contains("__rep", number.RightHandSide[0]);
        }

        [Fact]
        public void GroupedAlternation_InlinesIntoOwner()
        {
            const string groupGrammar = @"
Grammar: GroupAlternation
<word> ::= /[a-z]+/
<stmt> ::= <word> ( <word> <word> | <word> )
";
            var engine = new StepParserEngine();
            engine.LoadGrammarFromContent(groupGrammar);
            var grammar = engine.CurrentGrammar!;

            var stmt = grammar.ProductionRules.Where(r => r.Name == "stmt").ToList();
            Assert.Contains(stmt, r => r.RightHandSide.SequenceEqual(new[] { "word", "word", "word" }));
            Assert.Contains(stmt, r => r.RightHandSide.SequenceEqual(new[] { "word", "word" }));
        }

        [Fact]
        public void Annotation_IsSkippedDuringNormalization()
        {
            const string annotationGrammar = @"
Grammar: Annotated
<word> ::= /[a-z]+/
@DIALECT[test]
<phrase> ::= <word> <word>
";
            var engine = new StepParserEngine();
            engine.LoadGrammarFromContent(annotationGrammar);
            var grammar = engine.CurrentGrammar!;

            var phrase = grammar.ProductionRules.Where(r => r.Name == "phrase").ToList();
            Assert.Single(phrase);
            Assert.Equal(new[] { "word", "word" }, phrase[0].RightHandSide);
        }

        [Fact]
        public void EbnfShapes_OnlyMentionedInRhs_ParseSuccessfully()
        {
            var engine = new StepParserEngine();
            engine.LoadGrammarFromContent(EbnfGrammar);

            // Optional trailing <num> present.
            var withNum = engine.Parse("alpha 42 , beta 7 , gamma");
            Assert.True(withNum.Success,
                $"Expected success with optional present. Errors: {string.Join(" | ", withNum.Errors)}");

            // ... and absent, including a trailing optional at the end.
            var withoutNum = engine.Parse("alpha , beta , gamma 5");
            Assert.True(withoutNum.Success,
                $"Expected success with optional absent. Errors: {string.Join(" | ", withoutNum.Errors)}");
        }

        [Fact]
        public void WhitespaceOnlyToken_IsTreatedAsOptional()
        {
            const string wsGrammar = @"
Grammar: WhitespaceOptional
<word> ::= /[a-z]+/
<opt-whitespace> ::= /[ \t\n\r]*/
<phrase> ::= <word> <opt-whitespace> <word>
";
            var engine = new StepParserEngine();
            engine.LoadGrammarFromContent(wsGrammar);
            var grammar = engine.CurrentGrammar!;

            // The lexer rejects zero-width matches, so <opt-whitespace>
            // must expand to present/absent alternatives.
            var phrase = grammar.ProductionRules.Where(r => r.Name == "phrase").ToList();
            Assert.Contains(phrase, r => r.RightHandSide.SequenceEqual(new[] { "word", "opt-whitespace", "word" }));
            Assert.Contains(phrase, r => r.RightHandSide.SequenceEqual(new[] { "word", "word" }));

            var result = engine.Parse("alpha beta");
            Assert.True(result.Success, string.Join(" | ", result.Errors));
        }

        [Fact]
        public void RegexTerminalInProduction_MaterializesAsLexerRule()
        {
            const string regexGrammar = @"
Grammar: InlineRegex
<word> ::= /[a-z]+/
<ident> ::= /[a-zA-Z][a-zA-Z0-9_]*/
<decl> ::= <ident> <word>
";
            var engine = new StepParserEngine();
            engine.LoadGrammarFromContent(regexGrammar);

            var result = engine.Parse("hello world");
            Assert.True(result.Success, string.Join(" | ", result.Errors));
        }

        [Fact]
        public void UnsupportedSyntax_FallsBackToLegacyPath()
        {
            // Bracket optionals [ ... ] are outside the supported subset:
            // the normalizer must return null and the legacy parser must
            // still load the grammar without throwing.
            const string legacyGrammar = @"
Grammar: LegacyFallback
<word> ::= /[a-z]+/
<phrase> ::= <word> { <word> }
";
            var engine = new StepParserEngine();
            engine.LoadGrammarFromContent(legacyGrammar);
            Assert.NotNull(engine.CurrentGrammar);
        }

        [Fact]
        public void SemanticAction_PreservedOnNormalizedRules()
        {
            const string actionGrammar = @"
Grammar: ActionPreserved
<num> ::= /[0-9]+/
<value> ::= <num> => { value = $1; }
";
            var engine = new StepParserEngine();
            engine.LoadGrammarFromContent(actionGrammar);
            var grammar = engine.CurrentGrammar!;

            var value = grammar.ProductionRules.FirstOrDefault(r => r.Name == "value");
            Assert.NotNull(value);
            Assert.NotNull(value!.SemanticAction);
            Assert.Equal(new[] { "num" }, value.RightHandSide);
        }
    }
}
