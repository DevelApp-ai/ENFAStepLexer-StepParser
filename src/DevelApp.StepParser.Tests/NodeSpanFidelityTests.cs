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

        <expr> ::= <expr> "+" <term> | <term>
        <term> ::= <NUMBER>
        <NUMBER> ::= /[0-9]+/
        """;

    /// <summary>A node's span record: the data needed to check fidelity.</summary>
    private readonly record struct NodeSpan(uint Start, uint Length, ushort NodeType);

    private static CognitiveGraph.CognitiveGraph ParseOrFail(StepParserEngine engine, string input)
    {
        engine.LoadGrammarFromContent(ExpressionGrammar);
        var result = engine.Parse(input);
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

    private static void Collect(in SymbolNode node, string source, List<NodeSpan> spans)
    {
        spans.Add(new NodeSpan(node.SourceStart, node.SourceLength, node.NodeType));
        foreach (var packed in node.GetPackedNodes())
        {
            foreach (var child in packed.GetChildNodes())
            {
                Collect(child, source, spans);
            }
        }
    }

    private static List<NodeSpan> AllSpans(CognitiveGraph.CognitiveGraph graph, string source)
    {
        var spans = new List<NodeSpan>();
        Collect(graph.GetRootNode(), source, spans);
        return spans;
    }

    private const ushort TerminalNodeType = 100;
    private const ushort NonTerminalNodeType = 200;

    [Fact]
    public void TerminalNodes_CarryExactZeroBasedOffsets()
    {
        const string source = "12 + 34";
        var graph = ParseOrFail(_engine, source);

        var spans = AllSpans(graph, source);

        // The NUMBER terminals must carry their exact source text: "12" at
        // offsets 0..2 and "34" at offsets 5..7. Before the fix, SourceStart
        // was the 1-based column, so the first token read as "2 " (issue #98).
        var first = spans.First(s => s.NodeType == TerminalNodeType && SpanText(s, source).StartsWith("12"));
        Assert.Equal(0u, first.Start);
        Assert.Equal(2u, first.Length);

        var last = spans.First(s => s.NodeType == TerminalNodeType && SpanText(s, source).StartsWith("34"));
        Assert.Equal(5u, last.Start);
        Assert.Equal(2u, last.Length);
    }

    [Fact]
    public void NonTerminalNodes_SpanTheirExactText()
    {
        const string source = "12 + 34";
        var graph = ParseOrFail(_engine, source);

        var spans = AllSpans(graph, source);

        // Some non-terminal node (expr covering "12 + 34") must span the
        // whole expression exactly: start 0, length 7.
        var wholeSpan = spans.FirstOrDefault(s =>
            s.NodeType == NonTerminalNodeType && s.Start == 0 && s.Length == (uint)source.Length);
        Assert.NotEqual(default(NodeSpan), wholeSpan);
        Assert.Equal(source, SpanText(wholeSpan, source));
    }

    [Fact]
    public void LeadingWhitespace_DoesNotShiftSpans()
    {
        // Tokens after leading spaces must still map exactly: in
        // "   12 + 34" (3 leading spaces), "34" sits at offset 9..11.
        const string source = "   12 + 34";
        var graph = ParseOrFail(_engine, source);

        var spans = AllSpans(graph, source);
        var thirtyFour = spans.FirstOrDefault(s =>
            s.NodeType == TerminalNodeType && SpanText(s, source) == "34");

        Assert.NotEqual(default(NodeSpan), thirtyFour);
        Assert.Equal(9u, thirtyFour.Start);
        Assert.Equal(2u, thirtyFour.Length);
    }
}
