using Xunit;
using DevelApp.StepParser;
using DevelApp.StepLexer;
using System;
using System.Linq;

namespace DevelApp.StepParser.Tests
{
    /// <summary>
    /// Tests for grammar inheritance (Inherits: header) and base grammar merging
    /// </summary>
    public class GrammarInheritanceTests
    {
        private const string DerivedGrammar = @"
Grammar: Derived
Inherits: antlr4_base

<expression> ::= <NUMBER>
<NUMBER> ::= /[0-9]+/
";

        [Fact]
        public void ParseGrammarContent_InheritsAntlrBase_MergesBaseTokenRules()
        {
            // Arrange
            var loader = new GrammarLoader();

            // Act
            var grammar = loader.ParseGrammarContent(DerivedGrammar);

            // Assert - the antlr4_base default grammar contributes WS, IDENTIFIER and NUMBER
            Assert.NotNull(grammar);
            Assert.Contains(grammar.TokenRules, r => r.Name == "WS");
            Assert.Contains(grammar.TokenRules, r => r.Name == "IDENTIFIER");
            Assert.Contains(grammar.TokenRules, r => r.Name == "NUMBER");
        }

        [Fact]
        public void ParseGrammarContent_InheritsAntlrBase_BaseWhitespaceRuleIsSkippable()
        {
            // Arrange
            var loader = new GrammarLoader();

            // Act
            var grammar = loader.ParseGrammarContent(DerivedGrammar);

            // Assert
            var wsRule = grammar.TokenRules.Single(r => r.Name == "WS");
            Assert.True(wsRule.IsSkippable);
        }

        [Fact]
        public void ParseGrammarContent_DerivedRuleOverridesBaseRuleWithSameName()
        {
            // Arrange
            var loader = new GrammarLoader();
            var grammarWithOverride = @"
Grammar: Derived
Inherits: antlr4_base

<IDENTIFIER> ::= /custom-[a-z]+/
<expression> ::= <IDENTIFIER>
";

            // Act
            var grammar = loader.ParseGrammarContent(grammarWithOverride);

            // Assert - the derived rule must win over the base rule
            var identifierRule = grammar.TokenRules.Single(r => r.Name == "IDENTIFIER");
            Assert.Equal("/custom-[a-z]+/", identifierRule.Pattern);
        }

        [Fact]
        public void ParseGrammarContent_InheritsBisonBase_MergesPrecedenceAndAssociativity()
        {
            // Arrange
            var loader = new GrammarLoader();
            var bisonDerived = @"
Grammar: Derived
Inherits: bison_base

<expression> ::= <NUMBER>
<NUMBER> ::= /[0-9]+/
";

            // Act
            var grammar = loader.ParseGrammarContent(bisonDerived);

            // Assert
            Assert.Equal(1, grammar.Precedence["+"]);
            Assert.Equal(1, grammar.Precedence["-"]);
            Assert.Equal(2, grammar.Precedence["*"]);
            Assert.Equal(2, grammar.Precedence["/"]);
            Assert.Equal("left", grammar.Associativity["+"]);
            Assert.Equal("left", grammar.Associativity["*"]);
        }

        [Fact]
        public void ParseGrammarContent_InheritsMultipleBases_MergesAll()
        {
            // Arrange
            var loader = new GrammarLoader();
            var multiDerived = @"
Grammar: Derived
Inherits: antlr4_base, bison_base

<expression> ::= <NUMBER>
<NUMBER> ::= /[0-9]+/
";

            // Act
            var grammar = loader.ParseGrammarContent(multiDerived);

            // Assert - token rules from antlr4_base and precedence from bison_base
            Assert.Contains(grammar.TokenRules, r => r.Name == "WS");
            Assert.Contains(grammar.TokenRules, r => r.Name == "IDENTIFIER");
            Assert.Equal(2, grammar.Precedence["*"]);
        }

        [Fact]
        public void ParseGrammarContent_DerivedPrecedenceIsNotOverriddenByBase()
        {
            // Arrange
            var loader = new GrammarLoader();
            var derivedWithPrecedence = @"
Grammar: Derived
Inherits: bison_base
Precedence:
Level5: { operators: [""*""], associativity: ""right"" }

<expression> ::= <NUMBER>
<NUMBER> ::= /[0-9]+/
";

            // Act
            var grammar = loader.ParseGrammarContent(derivedWithPrecedence);

            // Assert - base merge must not override an explicitly declared derived precedence
            Assert.Equal(5, grammar.Precedence["*"]);
            Assert.Equal("right", grammar.Associativity["*"]);
            // Operators only present in the base grammar are still merged in
            Assert.Equal(1, grammar.Precedence["+"]);
        }

        [Fact]
        public void ParseGrammarContent_UnknownBase_DoesNotThrow()
        {
            // Arrange
            var loader = new GrammarLoader();
            var unknownBase = @"
Grammar: Derived
Inherits: mystery_base

<expression> ::= <NUMBER>
<NUMBER> ::= /[0-9]+/
";

            // Act
            var grammar = loader.ParseGrammarContent(unknownBase);

            // Assert - unknown bases contribute no rules but must not break parsing
            Assert.NotNull(grammar);
            Assert.Equal("Derived", grammar.Name);
            Assert.Contains(grammar.TokenRules, r => r.Name == "NUMBER");
            Assert.DoesNotContain(grammar.TokenRules, r => r.Name == "WS");
        }

        [Fact]
        public void ParseGrammarContent_NoInheritance_LeavesRulesUntouched()
        {
            // Arrange
            var loader = new GrammarLoader();

            // Act
            var grammar = loader.ParseGrammarContent(DerivedGrammar.Replace("Inherits: antlr4_base\n", ""));

            // Assert
            Assert.Empty(grammar.Imports);
            Assert.DoesNotContain(grammar.TokenRules, r => r.Name == "WS");
            Assert.DoesNotContain(grammar.TokenRules, r => r.Name == "IDENTIFIER");
        }

        [Fact]
        public void ParseGrammarContent_ParsesInheritanceHeader()
        {
            // Arrange
            var loader = new GrammarLoader();

            // Act
            var grammar = loader.ParseGrammarContent(DerivedGrammar);

            // Assert
            Assert.Equal("Derived", grammar.Name);
            Assert.Contains("antlr4_base", grammar.Imports);
        }

        [Fact]
        public void ParseGrammarContent_InheritableHeader_IsParsed()
        {
            // Arrange
            var loader = new GrammarLoader();
            var inheritable = @"
Grammar: Base
Inheritable: true

<NUMBER> ::= /[0-9]+/
";

            // Act
            var grammar = loader.ParseGrammarContent(inheritable);

            // Assert
            Assert.True(grammar.IsInheritable);
        }
    }
}
