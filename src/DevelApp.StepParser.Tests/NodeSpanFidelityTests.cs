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

    private const string ExpressionGrammar = """
        Grammar: SpanTest
        TokenSplitter: Space
        FormatType: EBNF

        <expr> ::= <expr> "+" <term>
        <expr> ::= <term>
        <term> ::= <NUMBER>
        <NUMBER> ::= /[0-9]+/
        """;

    /// <summary>A node's span record: the data needed to check fidelity.</summary>
    private readonly record struct NodeSpan(uint Start, uint Length, ushort NodeType);

    private CognitiveGraph.CognitiveGraph ParseOrFail(string input)
    {
        _engine.LoadGrammarFromContent(ExpressionGrammar);
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
    private const ushort NonTerminalNodeType = 200;

    [Fact]
    public void TerminalNodes_CarryExactZeroBasedOffsets()
    {
        const string source = "12 + 34";
        var graph = ParseOrFail(source);

        var spans = AllSpans(graph);
        var described = Describe(spans, source);

        // The NUMBER terminals must carry their exact source text: "12" at
        // offsets 0..2 and "34" at offsets 5..7. Before the fix, SourceStart
        // was the 1-based column, so the first token read as "2 " (issue #98).
        var first = spans.FirstOrDefault(s => s.NodeType == TerminalNodeType && SpanText(s, source).StartsWith("12"));
        Assert.True(first != default, $"No terminal with text starting '12'. Spans: {described}");
        Assert.True(first.Start == 0u && first.Length == 2u,
            $"First terminal should be start=0 len=2, got start={first.Start} len={first.Length}. Spans: {described}");

        var last = spans.FirstOrDefault(s => s.NodeType == TerminalNodeType && SpanText(s, source).StartsWith("34"));
        Assert.True(last != default, $"No terminal with text starting '34'. Spans: {described}");
        Assert.True(last.Start == 5u && last.Length == 2u,
            $"Last terminal should be start=5 len=2, got start={last.Start} len={last.Length}. Spans: {described}");
    }

    [Fact]
    public void NonTerminalNodes_SpanTheirExactText()
    {
        const string source = "12 + 34";
        var graph = ParseOrFail(source);

        var spans = AllSpans(graph);
        var described = Describe(spans, source);

        // Some non-terminal node (expr covering "12 + 34") must span the
        // whole expression exactly: start 0, length 7.
        var wholeSpan = spans.FirstOrDefault(s =>
            s.NodeType == NonTerminalNodeType && s.Start == 0 && s.Length == (uint)source.Length);
        Assert.True(wholeSpan != default,
            $"No non-terminal spanning the whole source (start=0 len={source.Length}). Spans: {described}");
        Assert.Equal(source, SpanText(wholeSpan, source));
    }

    [Fact]
    public void LeadingWhitespace_DoesNotShiftSpans()
    {
        // Tokens after leading spaces must still map exactly: in
        // "   12 + 34" (3 leading spaces), "34" sits at offset 9..11.
        const string source = "   12 + 34";
        var graph = ParseOrFail(source);

        var spans = AllSpans(graph);
        var described = Describe(spans, source);

        var thirtyFour = spans.FirstOrDefault(s =>
            s.NodeType == TerminalNodeType && SpanText(s, source) == "34");
        Assert.True(thirtyFour != default, $"No terminal with exact text '34'. Spans: {described}");
        Assert.True(thirtyFour.Start == 9u && thirtyFour.Length == 2u,
            $"'34' should be start=9 len=2, got start={thirtyFour.Start} len={thirtyFour.Length}. Spans: {described}");
    }
}
