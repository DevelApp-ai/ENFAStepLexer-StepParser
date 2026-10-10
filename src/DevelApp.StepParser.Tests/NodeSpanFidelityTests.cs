using System;
using System.Linq;
using CognitiveGraph.Accessors;
using DevelApp.StepLexer;
using Xunit;

namespace DevelApp.StepParser.Tests;

/// <summary>
/// Regression tests for parse-graph source-span fidelity (issue #98):
/// node SourceStart/SourceLength must map to the exact 0-based byte offsets
/// of the node's text in the parsed source, not the lexer's 1-based columns.
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

    private static CognitiveGraph.CognitiveGraph ParseOrFail(StepParserEngine engine, string input)
    {
        engine.LoadGrammarFromContent(ExpressionGrammar);
        var result = engine.Parse(input);
        Assert.True(result.Success,
            $"Parse should succeed. Errors: {string.Join("; ", result.Errors)}");
        Assert.NotNull(result.CognitiveGraph);
        return result.CognitiveGraph!;
    }

    private static string NodeText(SymbolNode node, string source)
    {
        var start = (int)Math.Min(node.SourceStart, (uint)source.Length);
        var length = (int)Math.Min(node.SourceLength, (uint)(source.Length - start));
        return source.Substring(start, length);
    }

    private static System.Collections.Generic.List<SymbolNode> AllNodes(
        CognitiveGraph.CognitiveGraph graph, SymbolNode node)
    {
        var all = new System.Collections.Generic.List<SymbolNode> { node };
        foreach (var packed in node.GetPackedNodes())
        {
            foreach (var child in packed.GetChildNodes())
            {
                all.AddRange(AllNodes(graph, child));
            }
        }

        return all;
    }

    private static bool IsTerminal(SymbolNode node)
    {
        // Terminal nodes are written with nodeType 100; non-terminals 200.
        return node.NodeType == 100;
    }

    [Fact]
    public void TerminalNodes_CarryExactZeroBasedOffsets()
    {
        const string source = "12 + 34";
        var graph = ParseOrFail(_engine, source);

        var all = AllNodes(graph, graph.GetRootNode());

        // The NUMBER terminals must carry their exact source text: "12" at
        // offsets 0..2 and "34" at offsets 5..7. Before the fix, SourceStart
        // was the 1-based column, so the first token read as "2 " (issue #98).
        var first = all.First(n => IsTerminal(n) && NodeText(n, source).StartsWith("12"));
        Assert.Equal(0u, first.SourceStart);
        Assert.Equal(2u, first.SourceLength);

        var last = all.First(n => IsTerminal(n) && NodeText(n, source).StartsWith("34"));
        Assert.Equal(5u, last.SourceStart);
        Assert.Equal(2u, last.SourceLength);
    }

    [Fact]
    public void NonTerminalNodes_SpanTheirExactText()
    {
        const string source = "12 + 34";
        var graph = ParseOrFail(_engine, source);

        var all = AllNodes(graph, graph.GetRootNode());

        // Some non-terminal node (expr covering "12 + 34") must span the
        // whole expression exactly: start 0, length 7.
        var wholeSpan = all.FirstOrDefault(n =>
            !IsTerminal(n) && n.SourceStart == 0 && n.SourceLength == (uint)source.Length);
        Assert.NotNull(wholeSpan);
        Assert.Equal(source, NodeText(wholeSpan!, source));
    }

    [Fact]
    public void LeadingWhitespace_DoesNotShiftSpans()
    {
        // Tokens after leading spaces must still map exactly: in
        // "   12 + 34" (3 leading spaces), "34" sits at offset 9..11.
        const string source = "   12 + 34";
        var graph = ParseOrFail(_engine, source);

        var all = AllNodes(graph, graph.GetRootNode());
        var thirtyFour = all.FirstOrDefault(n => IsTerminal(n) && NodeText(n, source) == "34");

        Assert.NotNull(thirtyFour);
        Assert.Equal(9u, thirtyFour!.SourceStart);
        Assert.Equal(2u, thirtyFour.SourceLength);
    }
}
