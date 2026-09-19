using Xunit;
using DevelApp.StepParser;
using DevelApp.StepLexer;
using System;
using System.Collections.Generic;
using System.Linq;

namespace DevelApp.StepParser.Tests
{
    /// <summary>
    /// Tests for the structured parse diagnostics: unexpected lexer input,
    /// parser failures and diagnostic rendering.
    /// </summary>
    public class ParseDiagnosticTests
    {
        private static readonly string SimpleGrammar = TestGrammars.Get("test-grammars/step-parser-tests/ParseDiagnosticTests/DiagnosticGrammar.grammar");

        [Fact]
        public void UnexpectedInput_ProducesLexerDiagnosticWithLocation()
        {
            var engine = new StepParserEngine();
            engine.LoadGrammarFromContent(SimpleGrammar);

            var result = engine.Parse("123 @ 456", "input.txt");

            var diagnostic = Assert.Single(result.Diagnostics);
            Assert.Equal(DiagnosticCodes.LexerUnexpectedInput, diagnostic.Code);
            Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
            Assert.Equal("input.txt", diagnostic.FileName);
            Assert.Equal(1, diagnostic.Line);
            Assert.Equal(5, diagnostic.Column);
            Assert.Equal(4, diagnostic.Position);
            Assert.Equal("123 @ 456", diagnostic.SourceLine);
            Assert.Contains("no token rule matches", diagnostic.Message);
            Assert.NotEmpty(diagnostic.Hint);
        }

        [Fact]
        public void UnexpectedInput_OnSecondLine_ReportsSecondLineLocation()
        {
            var engine = new StepParserEngine();
            engine.LoadGrammarFromContent(SimpleGrammar);

            var result = engine.Parse("123\n@@@", "input.txt");

            var diagnostic = Assert.Single(result.Diagnostics);
            Assert.Equal(DiagnosticCodes.LexerUnexpectedInput, diagnostic.Code);
            Assert.Equal(2, diagnostic.Line);
            Assert.Equal(1, diagnostic.Column);
            Assert.Equal(4, diagnostic.Position);
            Assert.Equal("@@@", diagnostic.SourceLine);
        }

        [Fact]
        public void UnexpectedInput_IsMirroredInErrorsList()
        {
            var engine = new StepParserEngine();
            engine.LoadGrammarFromContent(SimpleGrammar);

            var result = engine.Parse("123 @", "input.txt");

            var diagnostic = Assert.Single(result.Diagnostics);
            Assert.Contains(diagnostic.ToDisplayString(), result.Errors.Single());
        }

        [Fact]
        public void FullyConsumedInput_ProducesNoDiagnostics()
        {
            var engine = new StepParserEngine();
            engine.LoadGrammarFromContent(SimpleGrammar);

            var result = engine.Parse("123", "input.txt");

            Assert.True(result.Success);
            Assert.Empty(result.Diagnostics);
            Assert.Empty(result.Errors);
        }

        [Fact]
        public void ParseError_WhenTokensDoNotMatchProductions_ProducesParserDiagnostic()
        {
            var engine = new StepParserEngine();
            // The expression production requires an identifier; the number
            // lexes cleanly but cannot be parsed.
            var grammar = TestGrammars.Get("test-grammars/step-parser-tests/ParseDiagnosticTests/MismatchGrammar.grammar");
            engine.LoadGrammarFromContent(grammar);

            var result = engine.Parse("123", "input.txt");

            Assert.False(result.Success);
            var diagnostic = result.Diagnostics.Single(d => d.Code == DiagnosticCodes.ParserParseError);
            Assert.Equal("input.txt", diagnostic.FileName);
            Assert.Equal(1, diagnostic.Line);
            Assert.Equal("123", diagnostic.SourceLine);
        }

        [Fact]
        public void ToDisplayString_HasCompilerStyleFormat()
        {
            var diagnostic = new ParseDiagnostic(DiagnosticCodes.LexerUnexpectedInput, DiagnosticSeverity.Error, "Unexpected input")
            {
                FileName = "test.txt",
                Line = 2,
                Column = 7
            };

            Assert.Equal("test.txt(2,7): error LX1001: Unexpected input", diagnostic.ToDisplayString());
        }

        [Fact]
        public void ToDisplayString_RendersSeverities()
        {
            Assert.Equal("info PS2003: m", new ParseDiagnostic("PS2003", DiagnosticSeverity.Information, "m").ToDisplayString());
            Assert.Equal("warning PS2003: m", new ParseDiagnostic("PS2003", DiagnosticSeverity.Warning, "m").ToDisplayString());
            Assert.Equal("error PS2003: m", new ParseDiagnostic("PS2003", DiagnosticSeverity.Error, "m").ToDisplayString());
        }

        [Fact]
        public void ToDisplayString_OmitsLocationWhenFileIsMissing()
        {
            var diagnostic = new ParseDiagnostic("LX1002", DiagnosticSeverity.Warning, "stuck");

            Assert.Equal("warning LX1002: stuck", diagnostic.ToDisplayString());
        }

        [Fact]
        public void ToSourceExcerpt_RendersCaretUnderOffendingColumn()
        {
            var diagnostic = new ParseDiagnostic(DiagnosticCodes.LexerUnexpectedInput, DiagnosticSeverity.Error, "Unexpected input")
            {
                FileName = "test.txt",
                Line = 1,
                Column = 5,
                SourceLine = "123 @ 456"
            };

            var excerpt = diagnostic.ToSourceExcerpt();
            var lines = excerpt.Split(new[] { Environment.NewLine }, StringSplitOptions.None);
            Assert.Equal(3, lines.Length);
            Assert.Equal("test.txt(1,5): error LX1001: Unexpected input", lines[0]);
            Assert.Equal("    123 @ 456", lines[1]);
            Assert.Equal("        ^", lines[2]);
        }

        [Fact]
        public void ParseMultipleFiles_AggregatesDiagnostics()
        {
            var engine = new StepParserEngine();
            engine.LoadGrammarFromContent(SimpleGrammar);

            var files = new Dictionary<string, string>
            {
                { "good.txt", "123" },
                { "bad.txt", "123 @ 456" }
            };

            var result = engine.ParseMultipleFiles(files);

            var diagnostic = Assert.Single(result.Diagnostics);
            Assert.Equal("bad.txt", diagnostic.FileName);
        }

        [Fact]
        public void DiagnosticCodes_HaveExpectedStableValues()
        {
            Assert.Equal("LX1001", DiagnosticCodes.LexerUnexpectedInput);
            Assert.Equal("LX1002", DiagnosticCodes.LexerStalled);
            Assert.Equal("PS2001", DiagnosticCodes.ParserParseError);
            Assert.Equal("PS2002", DiagnosticCodes.ParserStalled);
            Assert.Equal("PS2003", DiagnosticCodes.ParserInternalError);
            Assert.Equal("GR3001", DiagnosticCodes.GrammarNotInheritable);
            Assert.Equal("GR3002", DiagnosticCodes.GrammarInheritanceCycle);
        }
    }
}
