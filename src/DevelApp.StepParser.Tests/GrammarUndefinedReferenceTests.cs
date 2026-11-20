using Xunit;
using DevelApp.StepParser;
using System;
using System.Linq;

namespace DevelApp.StepParser.Tests
{
    /// <summary>
    /// Tests for load-time detection of undefined nonterminal references
    /// (issue #157, part 2): grammars referencing nonterminals that are never
    /// defined used to load silently and then wander into parse blowups.
    /// The loader records them on
    /// <see cref="GrammarDefinition.UndefinedNonterminalReferences"/> and throws
    /// in strict mode (<c>GRAMMAR_VALIDATION_MODE=strict</c> or the
    /// <c>throwOnUndefinedReferences</c> argument).
    /// </summary>
    public class GrammarUndefinedReferenceTests
    {
        [Fact]
        public void ParseGrammarContent_UndefinedReference_IsRecordedNotThrown()
        {
            var loader = new GrammarLoader();
            var content = "Grammar: T\nFormatType: EBNF\n\n<start> ::= <missing> \"x\"\n";

            var grammar = loader.ParseGrammarContent(content, "t.grammar");

            Assert.Single(grammar.UndefinedNonterminalReferences);
            Assert.Equal("missing", grammar.UndefinedNonterminalReferences[0]);
        }

        [Fact]
        public void ParseGrammarContent_StrictArgument_ThrowsOnUndefinedReference()
        {
            var loader = new GrammarLoader();
            var content = "Grammar: T\nFormatType: EBNF\n\n<start> ::= <missing>\n";

            var ex = Assert.Throws<ENFA_GrammarBuild_Exception>(
                () => loader.ParseGrammarContent(content, "t.grammar", throwOnUndefinedReferences: true));

            Assert.Contains(DiagnosticCodes.GrammarUndefinedNonterminal, ex.Message);
            Assert.Contains("missing", ex.Message);
        }

        [Fact]
        public void ParseGrammarContent_StrictEnvMode_ThrowsOnUndefinedReference()
        {
            var loader = new GrammarLoader();
            var content = "Grammar: T\nFormatType: EBNF\n\n<start> ::= <missing>\n";
            var prev = Environment.GetEnvironmentVariable("GRAMMAR_VALIDATION_MODE");
            try
            {
                Environment.SetEnvironmentVariable("GRAMMAR_VALIDATION_MODE", "strict");
                Assert.Throws<ENFA_GrammarBuild_Exception>(
                    () => loader.ParseGrammarContent(content, "t.grammar"));

                Environment.SetEnvironmentVariable("GRAMMAR_VALIDATION_MODE", "verify");
                var grammar = loader.ParseGrammarContent(content, "t.grammar");
                Assert.Single(grammar.UndefinedNonterminalReferences);
            }
            finally
            {
                Environment.SetEnvironmentVariable("GRAMMAR_VALIDATION_MODE", prev);
            }
        }

        [Fact]
        public void ParseGrammarContent_DefinedReferences_AreNotFlagged()
        {
            var loader = new GrammarLoader();
            var content = "Grammar: T\nFormatType: EBNF\n\n<start> ::= <expr> <tok>\n<expr> ::= <tok> \"+\" <tok>\n<tok> ::= /[a-z]+/\n";

            var grammar = loader.ParseGrammarContent(content, "t.grammar");

            Assert.Empty(grammar.UndefinedNonterminalReferences);
        }

        [Fact]
        public void ParseGrammarContent_QuotedLiteralsAndRegexes_AreNotReferences()
        {
            var loader = new GrammarLoader();
            // "<b>" is a quoted literal and /<c>/ a regex terminal: neither is a
            // nonterminal reference. <real> is defined.
            var content = "Grammar: T\nFormatType: EBNF\n\n<start> ::= \"<b>\" /<c>/ <real>\n<real> ::= \"ok\"\n";

            var grammar = loader.ParseGrammarContent(content, "t.grammar");

            Assert.Empty(grammar.UndefinedNonterminalReferences);
        }

        [Fact]
        public void ParseGrammarContent_MultiLineRule_UndefinedReferenceIsFound()
        {
            var loader = new GrammarLoader();
            var content = "Grammar: T\nFormatType: EBNF\n\n<start> ::= <real>\n    | <also-missing>\n<real> ::= \"ok\"\n";

            var grammar = loader.ParseGrammarContent(content, "t.grammar");

            Assert.Single(grammar.UndefinedNonterminalReferences);
            Assert.Equal("also-missing", grammar.UndefinedNonterminalReferences[0]);
        }

        [Fact]
        public void ParseGrammarContent_ContextQualifiedNames_AreResolved()
        {
            var loader = new GrammarLoader();
            // LHS with context suffix defines the bare name; RHS reference with a
            // context suffix resolves to the same bare name.
            var content = "Grammar: T\nFormatType: EBNF\n\n<a(ctx)> ::= \"x\"\n<start> ::= <a(other)>\n";

            var grammar = loader.ParseGrammarContent(content, "t.grammar");

            Assert.Empty(grammar.UndefinedNonterminalReferences);
        }
    }
}
