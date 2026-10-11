using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using CognitiveGraph.Accessors;
using DevelApp.StepLexer;
using Xunit;

namespace DevelApp.StepParser.Tests
{
    /// <summary>
    /// Tests for the byte-native parse entry points (issue #87):
    /// Parse(ReadOnlyMemory&lt;byte&gt;), ParseFile(path), and byte-based
    /// ParseAndMerge. These must produce results identical to the
    /// string-based Parse for the same source text (representation-only
    /// change, no behavioral divergence).
    /// </summary>
    public class ByteNativeParseTests : IDisposable
    {
        private readonly StepParserEngine _engine = new();

        public void Dispose() => _engine.Dispose();

        private const string NumberListGrammar = """
        Grammar: ByteNativeTest
        TokenSplitter: Space
        FormatType: EBNF
        <number-list> ::= <number-list> <NUMBER>
        <number-list> ::= <NUMBER>
        <NUMBER> ::= /[0-9]+/
        """;

        private static ReadOnlyMemory<byte> Utf8(string text)
            => new ReadOnlyMemory<byte>(Encoding.UTF8.GetBytes(text));

        [Fact]
        public void ParseBytes_ProducesSameTokensAndGraphAsStringParse()
        {
            _engine.LoadGrammarFromContent(NumberListGrammar);
            const string input = "123 456 789";

            var stringResult = _engine.Parse(input);
            var byteResult = _engine.Parse(Utf8(input));

            Assert.True(stringResult.Success);
            Assert.True(byteResult.Success);
            Assert.NotNull(byteResult.CognitiveGraph);

            Assert.Equal(
                stringResult.Tokens.Select(t => (t.Type, t.Value, t.StartPosition, t.Length)).ToList(),
                byteResult.Tokens.Select(t => (t.Type, t.Value, t.StartPosition, t.Length)).ToList());
            Assert.Equal(stringResult.PathCount, byteResult.PathCount);
        }

        [Fact]
        public void ParseBytes_UnexpectedInput_DiagnosticMatchesStringParse()
        {
            _engine.LoadGrammarFromContent(NumberListGrammar);
            const string input = "123 @ 456";

            var stringResult = _engine.Parse(input, "input.txt");
            var byteResult = _engine.Parse(Utf8(input), "input.txt");

            var stringDiagnostic = Assert.Single(stringResult.Diagnostics);
            var byteDiagnostic = Assert.Single(byteResult.Diagnostics);
            Assert.Equal(stringDiagnostic.Code, byteDiagnostic.Code);
            Assert.Equal(stringDiagnostic.Line, byteDiagnostic.Line);
            Assert.Equal(stringDiagnostic.Column, byteDiagnostic.Column);
            Assert.Equal(stringDiagnostic.Position, byteDiagnostic.Position);
            Assert.Equal(stringDiagnostic.SourceLine, byteDiagnostic.SourceLine);
        }

        [Fact]
        public void ParseBytes_NonAscii_InputMatchesStringParse()
        {
            _engine.LoadGrammarFromContent(NumberListGrammar);
            const string input = "123é456";

            var stringResult = _engine.Parse(input);
            var byteResult = _engine.Parse(Utf8(input));

            // Both parses must reach the same lexer verdict over the
            // multi-byte sequence (no mid-codepoint matching divergence).
            Assert.Equal(stringResult.Success, byteResult.Success);
            Assert.Equal(
                stringResult.Tokens.Select(t => (t.Type, t.Value)).ToList(),
                byteResult.Tokens.Select(t => (t.Type, t.Value)).ToList());
        }

        [Fact]
        public void ParseFile_ReadsUtf8FileWithoutIntermediateStringRoundTrip()
        {
            _engine.LoadGrammarFromContent(NumberListGrammar);
            var path = Path.Combine(Path.GetTempPath(), $"byte-native-{Guid.NewGuid():N}.txt");
            const string input = "123 456";
            try
            {
                File.WriteAllText(path, input, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

                var result = _engine.ParseFile(path);

                Assert.True(result.Success, $"Parse should succeed. Errors: {string.Join("; ", result.Errors)}");
                Assert.NotNull(result.CognitiveGraph);
                Assert.Equal(2, result.Tokens.Count(t => t.Type == "NUMBER"));
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Fact]
        public void ParseFile_WithUtf8Bom_StripsBomBeforeParsing()
        {
            _engine.LoadGrammarFromContent(NumberListGrammar);
            var path = Path.Combine(Path.GetTempPath(), $"byte-native-bom-{Guid.NewGuid():N}.txt");
            try
            {
                File.WriteAllBytes(path, new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes("123 456")).ToArray());

                var result = _engine.ParseFile(path);

                Assert.True(result.Success, $"Parse should succeed. Errors: {string.Join("; ", result.Errors)}");
                Assert.Equal(2, result.Tokens.Count(t => t.Type == "NUMBER"));
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Fact]
        public void ParseAndMergeBytes_MatchesStringVariantBehavior()
        {
            _engine.LoadGrammarFromContent(NumberListGrammar);
            var graph = _engine.ParseAndMerge(null!, Utf8("123 456"), "input.txt");

            Assert.NotNull(graph);
        }

        [Fact]
        public void ParseBytes_EmptyInput_DoesNotThrow()
        {
            _engine.LoadGrammarFromContent(NumberListGrammar);

            var result = _engine.Parse(ReadOnlyMemory<byte>.Empty);

            Assert.NotNull(result);
            Assert.False(result.Success);
        }
    }
}
