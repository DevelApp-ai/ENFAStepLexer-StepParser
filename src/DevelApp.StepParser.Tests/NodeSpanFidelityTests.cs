using System;
using System.Collections.Generic;
using System.Linq;
using CognitiveGraph.Accessors;
using DevelApp.StepLexer;
using Xunit;

namespace DevelApp.StepParser.Tests;

/// <summary>
/// Regression tests for parse-graph source-span fidelity (issue #98):
/// node SourceStart/SourceLength must map to the exact 0-based byte offsets
/// of the node's text in the parsed source, not the lexer's 1-based columns.
/// SymbolNode is a zero-copy ref struct, so the walk extracts span records
/// (start, length, node type) instead of holding node references.
/// </summary>
public sealed class NodeSpanFidelityTests : IDisposable
{
    private readonly StepParserEngine _engine = new();

    public void Dispose() => _engine.Dispose();

    private const string NumberListGrammar = """
        Grammar: SpanTest
        TokenSplitter: Space
        FormatType: EBNF

        <number-list> ::= <number-list> <NUMBER>
        <number-list> ::= <NUMBER>
        <NUMBER> ::= /[0-9]+/
        """;

    /// <summary>A node's span record: the data needed to check fidelity.</summary>
    private readonly record struct NodeSpan(uint Start, uint Length, ushort NodeType);

    private CognitiveGraph.CognitiveGraph ParseOrFail(string input)
    {
        _engine.LoadGrammarFromContent(NumberListGrammar);
        var result = _engine.Parse(input);
        Assert.True(result.Success,
            $"Parse should succeed. Errors: {string.Join("; ", result.Errors)}");
        Assert.NotNull(result.CognitiveGraph);
        return result.CognitiveGraph!;
    }

    private static string SpanText(NodeSpan span, string source)
    {
        var start = (int)Math.Min(span.Start, (uint)source.Length);
        var length = (int)Math.Min(span.Length, (uint)(source.Length - start));
        return source.Substring(start, length);
    }

    private static void Collect(in SymbolNode node, List<NodeSpan> spans)
    {
        spans.Add(new NodeSpan(node.SourceStart, node.SourceLength, node.NodeType));
        foreach (var packed in node.GetPackedNodes())
        {
            foreach (var child in packed.GetChildNodes())
            {
                Collect(child, spans);
            }
        }
    }

    private List<NodeSpan> AllSpans(CognitiveGraph.CognitiveGraph graph)
    {
        var spans = new List<NodeSpan>();
        Collect(graph.GetRootNode(), spans);
        return spans;
    }

    private string Describe(List<NodeSpan> spans, string source) =>
        string.Join("; ", spans.Select(s => $"type={s.NodeType} start={s.Start} len={s.Length} text='{SpanText(s, source)}'"));

    private const ushort TerminalNodeType = 100;

    [Fact]
    public void FirstTerminal_StartsAtSourceOffsetZero()
    {
        // Before the fix, the first token's SourceStart was its 1-based
        // column (1), so reading its span from the source text yielded text
        // starting one character late (issue #98: "class" read as "lass").
        const string source = "42 7";
        var graph = ParseOrFail(source);

        var spans = AllSpans(graph);
        var described = Describe(spans, source);

        var fourtyTwo = spans.FirstOrDefault(s =>
            s.NodeType == TerminalNodeType && SpanText(s, source) == "42");
        Assert.True(fourtyTwo != default, $"No terminal with exact text '42'. Spans: {described}");
        Assert.True(fourtyTwo.Start == 0u,
            $"'42' must start at offset 0 (0-based), got {fourtyTwo.Start}. Spans: {described}");
        Assert.True(fourtyTwo.Length == 2u,
            $"'42' must have length 2, got {fourtyTwo.Length}. Spans: {described}");
    }

    [Fact]
    public void LaterTerminals_MapToExactOffsets()
    {
        // The second token ("7") sits at 0-based offset 3; with the old
        // 1-based-column spans, its SourceStart was 4 (one late).
        const string source = "42 7";
        var graph = ParseOrFail(source);

        var spans = AllSpans(graph);
        var described = Describe(spans, source);

        var seven = spans.FirstOrDefault(s =>
            s.NodeType == TerminalNodeType && SpanText(s, source) == "7");
        Assert.True(seven != default, $"No terminal with exact text '7'. Spans: {described}");
        Assert.True(seven.Start == 3u,
            $"'7' must start at offset 3 (0-based), got {seven.Start}. Spans: {described}");
    }

    [Fact]
    public void LeadingWhitespace_DoesNotShiftSpans()
    {
        // Tokens after leading spaces must map to their absolute offsets:
        // in "   42" (3 leading spaces), "42" starts at offset 3.
        const string source = "   42";
        var graph = ParseOrFail(source);

        var spans = AllSpans(graph);
        var described = Describe(spans, source);

        var fourtyTwo = spans.FirstOrDefault(s =>
            s.NodeType == TerminalNodeType && SpanText(s, source) == "42");
        Assert.True(fourtyTwo != default, $"No terminal with exact text '42'. Spans: {described}");
        Assert.True(fourtyTwo.Start == 3u,
            $"'42' must start at offset 3 (0-based), got {fourtyTwo.Start}. Spans: {described}");
    }

    [Fact]
    public void NonTerminalSpans_CoverTheirTerminalsExactly()
    {
        // A number-list node covering the whole input must start at the
        // first child's 0-based offset and extend to the last child's end,
        // so its span text is the whole (whitespace-trimmed) token range.
        const string source = "42 7";
        var graph = ParseOrFail(source);

        var spans = AllSpans(graph);
        var described = Describe(spans, source);

        // The root non-terminal covers offset 0 through the end of "7"
        // (offset 4): start 0, length 4.
        var root = spans.First();
        Assert.NotEqual(TerminalNodeType, root.NodeType);
        Assert.True(root.Start == 0u && root.Length == 4u,
            $"Root must span start=0 len=4, got start={root.Start} len={root.Length}. Spans: {described}");
    }
}
