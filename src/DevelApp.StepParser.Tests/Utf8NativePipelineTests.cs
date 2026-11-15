using System;
using System.Linq;
using System.Text;
using CognitiveGraph.Accessors;
using DevelApp.StepLexer;
using Xunit;

namespace DevelApp.StepParser.Tests
{
    /// <summary>
    /// Regression tests for the UTF-8-native parse pipeline (issue #87):
    /// Parse(ReadOnlyMemory&lt;byte&gt;) must behave identically to
    /// Parse(string) (representation-only change), and an ambiguous
    /// non-ASCII source must produce an ambiguous root whose
    /// SourceLength counts UTF-8 bytes - the CognitiveGraph buffer's
    /// unit of account - rather than UTF-16 characters.
    /// </summary>
    public class Utf8NativePipelineTests : IDisposable
    {
        private readonly StepParserEngine _engine = new();

        public void Dispose() => _engine.Dispose();

        private const string DanishWordListGrammar = """
            Grammar: Utf8NativeEquivalenceTest
            FormatType: EBNF
            <word-list> ::= <word-list> <DA>
            <word-list> ::= <DA>
            <DA> ::= 'rødgrød'
            """;

        private const string DanishDanglingElseGrammar = """
            Grammar: Utf8AmbiguityTest
            FormatType: EBNF
            <cond> ::= 'ø'
            <stmt> ::= 'hvis' <cond> <stmt> ( 'ellers' <stmt> )?
            <stmt> ::= 's'
            """;

        private static ReadOnlyMemory<byte> Utf8(string text)
            => new ReadOnlyMemory<byte>(Encoding.UTF8.GetBytes(text));

        [Fact]
        public void ParseBytes_DanishSource_EquivalentToStringParse()
        {
            _engine.LoadGrammarFromContent(DanishWordListGrammar);
            const string input = "rødgrød rødgrød rødgrød";

            var stringResult = _engine.Parse(input);
            var byteResult = _engine.Parse(Utf8(input));

            Assert.True(stringResult.Success,
                $"String parse should succeed. Errors: {string.Join("; ", stringResult.Errors)}");
            Assert.True(byteResult.Success,
                $"Byte parse should succeed. Errors: {string.Join("; ", byteResult.Errors)}");
            Assert.Equal(stringResult.Tokens.Count, byteResult.Tokens.Count);
            Assert.Equal(
                stringResult.Tokens.Select(t => (t.Type, t.Value, t.StartPosition, t.Length)).ToList(),
                byteResult.Tokens.Select(t => (t.Type, t.Value, t.StartPosition, t.Length)).ToList());
            Assert.Equal(
                stringResult.ResolvedTokens.Select(t => (t.Type, t.Value)).ToList(),
                byteResult.ResolvedTokens.Select(t => (t.Type, t.Value)).ToList());
            Assert.Equal(stringResult.PathCount, byteResult.PathCount);
        }

        [Fact]
        public void ParseBytes_DanishTokenSpans_AreByteSpans()
        {
            _engine.LoadGrammarFromContent(DanishWordListGrammar);
            const string input = "rødgrød";
            const int rødgrødBytes = 9; // 7 ASCII letters + 2-byte 'ø'

            var result = _engine.Parse(Utf8(input));

            Assert.True(result.Success,
                $"Parse should succeed. Errors: {string.Join("; ", result.Errors)}");
            var token = Assert.Single(result.Tokens);
            Assert.Equal(rødgrødBytes, token.Length);
            Assert.Equal(Encoding.UTF8.GetByteCount("rødgrød"), token.Length);
        }

        [Fact]
        public void ParseBytes_AmbiguousNonAsciiRoot_SourceLengthCountsBytes()
        {
            _engine.LoadGrammarFromContent(DanishDanglingElseGrammar);
            const string input = "hvis ø s ellers s";

            var result = _engine.Parse(Utf8(input));

            Assert.True(result.Success,
                $"Ambiguous parse should succeed. Errors: {string.Join("; ", result.Errors)}");
            Assert.True(result.PathCount > 1,
                "The dangling-else shape must produce more than one complete parse for the ambiguous-root path.");
            Assert.NotNull(result.CognitiveGraph);

            var root = result.CognitiveGraph!.GetRootNode();
            Assert.True(root.IsAmbiguous, "Multiple complete parses must produce an ambiguous root node.");

            // Issue #87: the ambiguous root's SourceLength must count UTF-8
            // bytes (the CognitiveGraph buffer's unit of account), which for
            // this Danish source differs from the UTF-16 character count.
            Assert.Equal((uint)Encoding.UTF8.GetByteCount(input), root.SourceLength);
            Assert.True(root.SourceLength > (uint)input.Length,
                "SourceLength must exceed the UTF-16 length for multi-byte Danish input.");
        }
    }
}
