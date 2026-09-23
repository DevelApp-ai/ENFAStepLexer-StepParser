using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using DevelApp.StepParser;
using DevelApp.StepLexer;

namespace DevelApp.StepParser.Tests
{
    /// <summary>
    /// Tests for runtime grammar overlay/composition (issue #66): compose a
    /// base grammar with an overlay grammar without authoring a derived
    /// grammar file, with explicit conflict resolution.
    /// </summary>
    public class GrammarOverlayTests
    {
        private const string BaseGrammar = @"
Grammar: BaseLang
TokenSplitter: Space
FormatType: EBNF

<WS> ::= /[ \t\r\n]+/ => { skip(); }
<IDENTIFIER> ::= /[a-zA-Z_][a-zA-Z0-9]*/
<NUMBER> ::= /[0-9]+/

<expr> ::= <term> ;
<term> ::= IDENTIFIER | NUMBER ;
";

        private const string PatternOverlay = @"
Grammar: BaseLangPatternOverlay
FormatType: EBNF

<METAVARIABLE> ::= /\$[A-Z_][A-Z0-9_]*/
<ELLIPSIS> ::= /\.\.\./

<term> ::= METAVARIABLE ;
<call-args> ::= ELLIPSIS ;
";

        private const string IdentifierOverrideOverlay = @"
Grammar: IdentifierOverride
FormatType: EBNF

<IDENTIFIER> ::= /custom-[a-z]+/
";

        [Fact]
        public void ComposeGrammars_AddsOverlayTokensAndProductions_WithoutMutatingInputs()
        {
            var loader = new GrammarLoader();
            var baseGrammar = loader.ParseGrammarContent(BaseGrammar);
            var overlay = loader.ParseGrammarContent(PatternOverlay);

            var result = loader.ComposeGrammars(baseGrammar, overlay);

            Assert.NotNull(result.Grammar);
            Assert.Contains(result.Grammar.TokenRules, r => r.Name == "METAVARIABLE");
            Assert.Contains(result.Grammar.TokenRules, r => r.Name == "ELLIPSIS");
            Assert.Contains(result.Grammar.ProductionRules, r => r.Name == "call-args");

            // Inputs must not be mutated
            Assert.DoesNotContain(baseGrammar.TokenRules, r => r.Name == "METAVARIABLE");
            Assert.DoesNotContain(baseGrammar.ProductionRules, r => r.Name == "call-args");
        }

        [Fact]
        public void ComposeGrammars_OverlayWins_ReplacesConflictingTokenRule()
        {
            var loader = new GrammarLoader();
            var baseGrammar = loader.ParseGrammarContent(BaseGrammar);
            var overlay = loader.ParseGrammarContent(IdentifierOverrideOverlay);

            var result = loader.ComposeGrammars(baseGrammar, overlay, OverlayConflictResolution.OverlayWins);

            var identifier = result.Grammar.TokenRules.Single(r => r.Name == "IDENTIFIER");
            Assert.Equal("/custom-[a-z]+/", identifier.Pattern);
            Assert.Contains(result.Conflicts, c => c.Contains("IDENTIFIER"));
        }

        [Fact]
        public void ComposeGrammars_BaseWins_KeepsConflictingTokenRule()
        {
            var loader = new GrammarLoader();
            var baseGrammar = loader.ParseGrammarContent(BaseGrammar);
            var overlay = loader.ParseGrammarContent(IdentifierOverrideOverlay);

            var result = loader.ComposeGrammars(baseGrammar, overlay, OverlayConflictResolution.BaseWins);

            var identifier = result.Grammar.TokenRules.Single(r => r.Name == "IDENTIFIER");
            Assert.Equal("/[a-zA-Z_][a-zA-Z0-9]*/", identifier.Pattern);
            Assert.Contains(result.Conflicts, c => c.Contains("IDENTIFIER"));
        }

        [Fact]
        public void ComposeGrammars_PriorityBased_HigherPriorityTokenWins()
        {
            var loader = new GrammarLoader();
            var baseGrammar = new GrammarDefinition
            {
                TokenRules = new List<TokenRule> { new TokenRule("IDENTIFIER", "/base-pattern/", priority: 10) }
            };
            var lowOverlay = new GrammarDefinition
            {
                TokenRules = new List<TokenRule> { new TokenRule("IDENTIFIER", "/low-overlay/", priority: 5) }
            };
            var highOverlay = new GrammarDefinition
            {
                TokenRules = new List<TokenRule> { new TokenRule("IDENTIFIER", "/high-overlay/", priority: 20) }
            };

            var lowWins = loader.ComposeGrammars(baseGrammar, lowOverlay, OverlayConflictResolution.PriorityBased);
            var highWins = loader.ComposeGrammars(baseGrammar, highOverlay, OverlayConflictResolution.PriorityBased);

            Assert.Equal("/base-pattern/", lowWins.Grammar.TokenRules.Single(r => r.Name == "IDENTIFIER").Pattern);
            Assert.Equal("/high-overlay/", highWins.Grammar.TokenRules.Single(r => r.Name == "IDENTIFIER").Pattern);
        }

        [Fact]
        public void ComposeGrammars_Additive_AppendsProductionAlternatives()
        {
            var loader = new GrammarLoader();
            var baseGrammar = loader.ParseGrammarContent(BaseGrammar);
            var overlay = loader.ParseGrammarContent(PatternOverlay);

            var result = loader.ComposeGrammars(baseGrammar, overlay, OverlayConflictResolution.Additive);

            // <term> had two alternatives in the base (IDENTIFIER | NUMBER) and
            // gains the overlay alternative (METAVARIABLE) instead of replacing them
            var termRules = result.Grammar.ProductionRules.Where(r => r.Name == "term").ToList();
            Assert.Equal(3, termRules.Count);
            Assert.Contains(termRules, r => r.RightHandSide.Contains("METAVARIABLE"));
            Assert.Contains(termRules, r => r.RightHandSide.Contains("IDENTIFIER"));
        }

        [Fact]
        public void ComposeGrammars_OverlayWins_ReplacesConflictingProductionAlternatives()
        {
            var loader = new GrammarLoader();
            var baseGrammar = loader.ParseGrammarContent(BaseGrammar);
            var overlay = loader.ParseGrammarContent(PatternOverlay);

            var result = loader.ComposeGrammars(baseGrammar, overlay, OverlayConflictResolution.OverlayWins);

            var termRules = result.Grammar.ProductionRules.Where(r => r.Name == "term").ToList();
            var term = Assert.Single(termRules);
            Assert.Equal(new List<string> { "METAVARIABLE" }, term.RightHandSide);
        }

        [Fact]
        public void ComposeGrammars_NoConflicts_WhenGrammarsAreDisjoint()
        {
            var loader = new GrammarLoader();
            var baseGrammar = loader.ParseGrammarContent(BaseGrammar);

            var disjointOverlay = new GrammarDefinition
            {
                TokenRules = new List<TokenRule> { new TokenRule("METAVARIABLE", @"/\$[A-Z_][A-Z0-9_]*/") },
                ProductionRules = new List<ProductionRule> { new ProductionRule("extra", new List<string> { "METAVARIABLE" }) }
            };

            var result = loader.ComposeGrammars(baseGrammar, disjointOverlay);

            Assert.Empty(result.Conflicts);
            Assert.Contains(result.Grammar.ProductionRules, r => r.Name == "extra");
        }

        [Fact]
        public void ComposeGrammars_MergesPrecedenceAndAssociativity()
        {
            var loader = new GrammarLoader();
            var baseGrammar = loader.ParseGrammarContent(BaseGrammar);
            baseGrammar.Precedence["*"] = 2;
            baseGrammar.Precedence["+"] = 1;

            var overlay = new GrammarDefinition();
            overlay.Precedence["*"] = 5;
            overlay.Precedence["/"] = 3;
            overlay.Associativity["+"] = "left";

            var overlayWins = loader.ComposeGrammars(baseGrammar, overlay, OverlayConflictResolution.OverlayWins);
            var baseWins = loader.ComposeGrammars(baseGrammar, overlay, OverlayConflictResolution.BaseWins);

            // Overlay wins: overlay entries overwrite, base-only entries survive
            Assert.Equal(5, overlayWins.Grammar.Precedence["*"]);
            Assert.Equal(3, overlayWins.Grammar.Precedence["/"]);
            Assert.Equal(1, overlayWins.Grammar.Precedence["+"]);
            Assert.Equal("left", overlayWins.Grammar.Associativity["+"]);

            // Base wins: overlay only fills gaps
            Assert.Equal(2, baseWins.Grammar.Precedence["*"]);
            Assert.Equal(3, baseWins.Grammar.Precedence["/"]);
            Assert.Equal(1, baseWins.Grammar.Precedence["+"]);
        }

        [Fact]
        public void ComposeGrammars_NullArguments_Throw()
        {
            var loader = new GrammarLoader();
            var grammar = new GrammarDefinition();

            Assert.Throws<ArgumentNullException>(() => loader.ComposeGrammars(null!, grammar));
            Assert.Throws<ArgumentNullException>(() => loader.ComposeGrammars(grammar, null!));
        }

        [Fact]
        public void ComposeWithOverlayContent_ComposesFromGrammarText()
        {
            var loader = new GrammarLoader();

            var result = loader.ComposeWithOverlayContent(BaseGrammar, PatternOverlay);

            Assert.Contains(result.Grammar.TokenRules, r => r.Name == "METAVARIABLE");
            Assert.Contains(result.Grammar.ProductionRules, r => r.Name == "call-args");
            Assert.Contains(result.Grammar.ProductionRules, r => r.Name == "expr");
        }

        [Fact]
        public void ComposeWithOverlayContent_NullContent_Throws()
        {
            var loader = new GrammarLoader();

            Assert.Throws<ArgumentNullException>(() => loader.ComposeWithOverlayContent(BaseGrammar, null!));
            Assert.Throws<ArgumentNullException>(() => loader.ComposeWithOverlayContent(null!, PatternOverlay));
        }

        [Fact]
        public void Engine_LoadGrammarWithOverlay_ConfiguresCompositeGrammar()
        {
            using var engine = new StepParserEngine();

            engine.LoadGrammarWithOverlay(BaseGrammar, PatternOverlay);

            Assert.NotNull(engine.CurrentGrammar);
            Assert.Contains(engine.CurrentGrammar!.TokenRules, r => r.Name == "METAVARIABLE");
            Assert.Contains(engine.CurrentGrammar.TokenRules, r => r.Name == "ELLIPSIS");
            Assert.Contains(engine.CurrentGrammar.ProductionRules, r => r.Name == "call-args");
            Assert.NotNull(engine.LastMergeResult);
            Assert.Contains(engine.LastMergeResult!.Conflicts, c => c.Contains("term"));
        }

        [Fact]
        public void Engine_ApplyOverlay_AppliesToLoadedGrammar_WithoutDuplicates()
        {
            using var engine = new StepParserEngine();
            engine.LoadGrammarFromContent(BaseGrammar);

            engine.ApplyOverlay(PatternOverlay);
            engine.ApplyOverlay(PatternOverlay);

            Assert.Contains(engine.CurrentGrammar!.TokenRules, r => r.Name == "METAVARIABLE");
            // Composition replaces by name, so the token must not be duplicated
            // in the composite grammar even after applying the overlay twice.
            Assert.Single(engine.CurrentGrammar.TokenRules, r => r.Name == "METAVARIABLE");
        }

        [Fact]
        public void Engine_ApplyOverlay_WithoutLoadedGrammar_Throws()
        {
            using var engine = new StepParserEngine();

            Assert.Throws<InvalidOperationException>(() => engine.ApplyOverlay(PatternOverlay));
        }

        [Fact]
        public void Engine_LoadGrammarDefinition_LoadsComposedGrammar()
        {
            var loader = new GrammarLoader();
            var result = loader.ComposeWithOverlayContent(BaseGrammar, PatternOverlay);

            using var engine = new StepParserEngine();
            engine.LoadGrammarDefinition(result.Grammar);

            Assert.Same(result.Grammar, engine.CurrentGrammar);
            Assert.Contains(engine.CurrentGrammar!.TokenRules, r => r.Name == "ELLIPSIS");
            Assert.Null(engine.LastMergeResult);
        }

        [Fact]
        public void Engine_LoadGrammarDefinition_NullGrammar_Throws()
        {
            using var engine = new StepParserEngine();

            Assert.Throws<ArgumentNullException>(() => engine.LoadGrammarDefinition(null!));
        }

        [Fact]
        public void ComposeGrammars_OverlayGrammarMayUseInheritance()
        {
            var loader = new GrammarLoader();
            loader.RegisterBaseGrammar("antlr4_base", "Grammar: antlr4_base\nInheritable: true\nFormatType: EBNF\n\n<BASEID> ::= /[a-z]+/\n");

            var overlayContent = @"
Grammar: InheritingOverlay
Inherits: antlr4_base
FormatType: EBNF

<METAVARIABLE> ::= /\$[A-Z_][A-Z0-9_]*/
";

            var result = loader.ComposeWithOverlayContent(BaseGrammar, overlayContent);

            // The overlay's own Inherits: declaration was processed before composition
            Assert.Contains(result.Grammar.TokenRules, r => r.Name == "BASEID");
            Assert.Contains(result.Grammar.TokenRules, r => r.Name == "METAVARIABLE");
        }
    }
}
