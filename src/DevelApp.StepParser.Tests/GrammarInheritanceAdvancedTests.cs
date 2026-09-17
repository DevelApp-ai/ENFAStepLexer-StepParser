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
        private const string BaseGrammar = @"
Grammar: MyBase
Inheritable: true

<NUMBER> ::= /[0-9]+/
<IDENTIFIER> ::= /[a-zA-Z][a-zA-Z0-9]*/
<WS> ::= /[ \t\r\n]+/

<value> ::= <NUMBER>
";

        [Fact]
        public void RegisterBaseGrammar_MakesGrammarAvailableForInheritance()
        {
            var loader = new GrammarLoader();
            loader.RegisterBaseGrammar("mybase", BaseGrammar);

            var derived = @"
Grammar: MyDerived
Inherits: mybase

<expression> ::= <value>
";
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

            var derived = @"
Grammar: MyDerived
Inherits: mybase

<expression> ::= <value>
";
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

            var derived = @"
Grammar: MyDerived
Inherits: mybase

<NUMBER> ::= /[0-9]{2,}/
";
            var grammar = loader.ParseGrammarContent(derived);

            var numberRule = Assert.Single(grammar.TokenRules, r => r.Name == "NUMBER");
            Assert.Equal(@"/[0-9]{2,}/", numberRule.Pattern);
        }

        [Fact]
        public void DerivedProductionRule_OverridesBaseRuleWithSameName()
        {
            var loader = new GrammarLoader();
            loader.RegisterBaseGrammar("mybase", BaseGrammar);

            var derived = @"
Grammar: MyDerived
Inherits: mybase

<value> ::= <IDENTIFIER>
";
            var grammar = loader.ParseGrammarContent(derived);

            var valueRule = Assert.Single(grammar.ProductionRules, r => r.Name == "value");
            Assert.Contains("IDENTIFIER", valueRule.RightHandSide);
        }

        [Fact]
        public void Inheritance_IsTransitive()
        {
            var loader = new GrammarLoader();
            loader.RegisterBaseGrammar("root", @"
Grammar: Root
Inheritable: true

<NUMBER> ::= /[0-9]+/
");
            loader.RegisterBaseGrammar("middle", @"
Grammar: Middle
Inherits: root
Inheritable: true

<IDENTIFIER> ::= /[a-zA-Z][a-zA-Z0-9]*/
");

            var derived = @"
Grammar: MyDerived
Inherits: middle
";
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
            loader.RegisterBaseGrammar("sealed", @"
Grammar: Sealed
Inheritable: false

<NUMBER> ::= /[0-9]+/
");

            var derived = @"
Grammar: MyDerived
Inherits: sealed
";

            var ex = Assert.Throws<ENFA_GrammarBuild_Exception>(() => loader.ParseGrammarContent(derived));
            Assert.Contains("GR3001", ex.Message);
            Assert.Contains("Inheritable", ex.Message);
        }

        [Fact]
        public void InheritanceCycle_Throws()
        {
            var loader = new GrammarLoader();
            loader.RegisterBaseGrammar("a", @"
Grammar: A
Inherits: b
Inheritable: true

<NUMBER> ::= /[0-9]+/
");
            loader.RegisterBaseGrammar("b", @"
Grammar: B
Inherits: a
Inheritable: true

<IDENTIFIER> ::= /[a-zA-Z][a-zA-Z0-9]*/
");

            var derived = @"
Grammar: MyDerived
Inherits: a
";

            var ex = Assert.Throws<ENFA_GrammarBuild_Exception>(() => loader.ParseGrammarContent(derived));
            Assert.Contains("GR3002", ex.Message);
            Assert.Contains("cycle", ex.Message);
        }

        [Fact]
        public void UnknownBaseGrammar_DoesNotThrow()
        {
            var loader = new GrammarLoader();

            var derived = @"
Grammar: MyDerived
Inherits: does_not_exist

<NUMBER> ::= /[0-9]+/
";
            var grammar = loader.ParseGrammarContent(derived);

            // Unknown imports resolve to an empty grammar; the derived
            // grammar keeps its own rules.
            Assert.Contains(grammar.TokenRules, r => r.Name == "NUMBER");
        }

        [Fact]
        public void BuiltInBaseGrammars_AreInheritable()
        {
            var loader = new GrammarLoader();

            var derived = @"
Grammar: MyDerived
Inherits: antlr4_base

<expression> ::= <NUMBER>
";
            var grammar = loader.ParseGrammarContent(derived);

            Assert.Contains(grammar.TokenRules, r => r.Name == "WS");
            Assert.Contains(grammar.TokenRules, r => r.Name == "IDENTIFIER");
        }

        [Fact]
        public void RegisteredBaseGrammar_CanBeUsedByMultipleGrammars()
        {
            var loader = new GrammarLoader();
            loader.RegisterBaseGrammar("mybase", BaseGrammar);

            var first = loader.ParseGrammarContent(@"
Grammar: First
Inherits: mybase
");
            var second = loader.ParseGrammarContent(@"
Grammar: Second
Inherits: mybase

<NUMBER> ::= /[0-9]{2,}/
");

            // Both parses inherit the base rules; the second one overrides
            // NUMBER, which must not leak into the first parse.
            Assert.Contains(first.TokenRules, r => r.Name == "NUMBER" && r.Pattern == "/[0-9]+/");
            Assert.Contains(second.TokenRules, r => r.Name == "NUMBER" && r.Pattern == "/[0-9]{2,}/");
        }
    }
}
