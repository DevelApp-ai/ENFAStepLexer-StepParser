using Xunit;
using DevelApp.StepParser;
using DevelApp.StepLexer;
using System;

namespace DevelApp.StepParser.Tests
{
    /// <summary>
    /// Tests for advanced grammar inheritance: registered base grammars,
    /// production rule merging, transitive inheritance, override behavior,
    /// the Inheritable flag and cycle detection.
    /// </summary>
    public class GrammarInheritanceAdvancedTests
    {
        private static readonly string BaseGrammar = TestGrammars.Get("test-grammars/step-parser-tests/GrammarInheritanceAdvancedTests/MyBase.grammar");

        [Fact]
        public void RegisterBaseGrammar_MakesGrammarAvailableForInheritance()
        {
            var loader = new GrammarLoader();
            loader.RegisterBaseGrammar("mybase", BaseGrammar);

            var derived = TestGrammars.Get("test-grammars/step-parser-tests/GrammarInheritanceAdvancedTests/MyDerived.grammar");
            var grammar = loader.ParseGrammarContent(derived);

            // Token rules from the registered base are inherited
            Assert.Contains(grammar.TokenRules, r => r.Name == "NUMBER");
            Assert.Contains(grammar.TokenRules, r => r.Name == "IDENTIFIER");
            Assert.Contains(grammar.TokenRules, r => r.Name == "WS");
        }

        [Fact]
        public void RegisteredBaseGrammar_ProductionRulesAreInherited()
        {
            var loader = new GrammarLoader();
            loader.RegisterBaseGrammar("mybase", BaseGrammar);

            var derived = TestGrammars.Get("test-grammars/step-parser-tests/GrammarInheritanceAdvancedTests/MyDerived.grammar");
            var grammar = loader.ParseGrammarContent(derived);

            // The base-only production rule 'value' is inherited
            Assert.Contains(grammar.ProductionRules, r => r.Name == "value");
            // The derived production rule is present
            Assert.Contains(grammar.ProductionRules, r => r.Name == "expression");
        }

        [Fact]
        public void DerivedTokenRule_OverridesBaseRuleWithSameName()
        {
            var loader = new GrammarLoader();
            loader.RegisterBaseGrammar("mybase", BaseGrammar);

            var derived = TestGrammars.Get("test-grammars/step-parser-tests/GrammarInheritanceAdvancedTests/MyDerived.2.grammar");
            var grammar = loader.ParseGrammarContent(derived);

            var numberRule = Assert.Single(grammar.TokenRules, r => r.Name == "NUMBER");
            Assert.Equal(@"/[0-9]{2,}/", numberRule.Pattern);
        }

        [Fact]
        public void DerivedProductionRule_OverridesBaseRuleWithSameName()
        {
            var loader = new GrammarLoader();
            loader.RegisterBaseGrammar("mybase", BaseGrammar);

            var derived = TestGrammars.Get("test-grammars/step-parser-tests/GrammarInheritanceAdvancedTests/MyDerived.3.grammar");
            var grammar = loader.ParseGrammarContent(derived);

            var valueRule = Assert.Single(grammar.ProductionRules, r => r.Name == "value");
            Assert.Contains("IDENTIFIER", valueRule.RightHandSide);
        }

        [Fact]
        public void Inheritance_IsTransitive()
        {
            var loader = new GrammarLoader();
            loader.RegisterBaseGrammar("root", TestGrammars.Get("test-grammars/step-parser-tests/GrammarInheritanceAdvancedTests/Root.grammar"));
            loader.RegisterBaseGrammar("middle", TestGrammars.Get("test-grammars/step-parser-tests/GrammarInheritanceAdvancedTests/Middle.grammar"));

            var derived = TestGrammars.Get("test-grammars/step-parser-tests/GrammarInheritanceAdvancedTests/MyDerived.4.grammar");
            var grammar = loader.ParseGrammarContent(derived);

            // 'middle' contributes IDENTIFIER and, via transitivity, 'root'
            // contributes NUMBER.
            Assert.Contains(grammar.TokenRules, r => r.Name == "IDENTIFIER");
            Assert.Contains(grammar.TokenRules, r => r.Name == "NUMBER");
        }

        [Fact]
        public void RegisterBaseGrammar_EmptyName_Throws()
        {
            var loader = new GrammarLoader();

            Assert.Throws<ENFA_GrammarBuild_Exception>(() => loader.RegisterBaseGrammar("", BaseGrammar));
            Assert.Throws<ENFA_GrammarBuild_Exception>(() => loader.RegisterBaseGrammar("   ", BaseGrammar));
        }

        [Fact]
        public void NonInheritableBaseGrammar_Throws()
        {
            var loader = new GrammarLoader();
            loader.RegisterBaseGrammar("sealed", TestGrammars.Get("test-grammars/step-parser-tests/GrammarInheritanceAdvancedTests/Sealed.grammar"));

            var derived = TestGrammars.Get("test-grammars/step-parser-tests/GrammarInheritanceAdvancedTests/MyDerived.5.grammar");

            var ex = Assert.Throws<ENFA_GrammarBuild_Exception>(() => loader.ParseGrammarContent(derived));
            Assert.Contains("GR3001", ex.Message);
            Assert.Contains("Inheritable", ex.Message);
        }

        [Fact]
        public void InheritanceCycle_Throws()
        {
            var loader = new GrammarLoader();
            loader.RegisterBaseGrammar("a", TestGrammars.Get("test-grammars/step-parser-tests/GrammarInheritanceAdvancedTests/A.grammar"));
            loader.RegisterBaseGrammar("b", TestGrammars.Get("test-grammars/step-parser-tests/GrammarInheritanceAdvancedTests/B.grammar"));

            var derived = TestGrammars.Get("test-grammars/step-parser-tests/GrammarInheritanceAdvancedTests/MyDerived.6.grammar");

            var ex = Assert.Throws<ENFA_GrammarBuild_Exception>(() => loader.ParseGrammarContent(derived));
            Assert.Contains("GR3002", ex.Message);
            Assert.Contains("cycle", ex.Message);
        }

        [Fact]
        public void UnknownBaseGrammar_DoesNotThrow()
        {
            var loader = new GrammarLoader();

            var derived = TestGrammars.Get("test-grammars/step-parser-tests/GrammarInheritanceAdvancedTests/MyDerived.7.grammar");
            var grammar = loader.ParseGrammarContent(derived);

            // Unknown imports resolve to an empty grammar; the derived
            // grammar keeps its own rules.
            Assert.Contains(grammar.TokenRules, r => r.Name == "NUMBER");
        }

        [Fact]
        public void BuiltInBaseGrammars_AreInheritable()
        {
            var loader = new GrammarLoader();

            var derived = TestGrammars.Get("test-grammars/step-parser-tests/GrammarInheritanceAdvancedTests/MyDerived.8.grammar");
            var grammar = loader.ParseGrammarContent(derived);

            Assert.Contains(grammar.TokenRules, r => r.Name == "WS");
            Assert.Contains(grammar.TokenRules, r => r.Name == "IDENTIFIER");
        }

        [Fact]
        public void RegisteredBaseGrammar_CanBeUsedByMultipleGrammars()
        {
            var loader = new GrammarLoader();
            loader.RegisterBaseGrammar("mybase", BaseGrammar);

            var first = loader.ParseGrammarContent(TestGrammars.Get("test-grammars/step-parser-tests/GrammarInheritanceAdvancedTests/First.grammar"));
            var second = loader.ParseGrammarContent(TestGrammars.Get("test-grammars/step-parser-tests/GrammarInheritanceAdvancedTests/Second.grammar"));

            // Both parses inherit the base rules; the second one overrides
            // NUMBER, which must not leak into the first parse.
            Assert.Contains(first.TokenRules, r => r.Name == "NUMBER" && r.Pattern == "/[0-9]+/");
            Assert.Contains(second.TokenRules, r => r.Name == "NUMBER" && r.Pattern == "/[0-9]{2,}/");
        }
    }
}
