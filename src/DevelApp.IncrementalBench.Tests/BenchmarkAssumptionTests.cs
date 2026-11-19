using DevelApp.StepParser;
using Xunit;

namespace DevelApp.IncrementalBench.Tests;

/// <summary>
/// Benchmark-assumption smoke tests (issue #101): the committed StepIdl
/// grammar must load and parse benchmark documents to completion at
/// file scale — the core parseability assumption the per-edit tables
/// rest on — and the correctness guards the runner relies on must be
/// satisfiable (SPI must agree with a full reparse on the same text).
/// </summary>
public class BenchmarkAssumptionTests
{
    private static string GrammarPath =>
        Path.Combine(AppContext.BaseDirectory, Docs.StepIdlGrammarPath);

    private static string Grammar => File.ReadAllText(GrammarPath);

    [Fact]
    public void StepIdl_Grammar_Loads_And_Parses_File_Scale()
    {
        var session = new RealTimeParserSession(
            File.ReadAllText(GrammarPath), Docs.StepIdlGrammarPath);
        session.EnableIncrementalParsing = true;

        var doc = Docs.StepIdl(500);
        session.Initialize(doc);

        Assert.True(session.LastLexConsumedAllInput);
        Assert.True(session.LastParse?.Success);
        Assert.True(session.Tokens.Count > 2500);
    }

    [Fact]
    public void Spi_Edit_Agrees_With_Full_Reparse()
    {
        var doc = Docs.StepIdl(200);
        var edit = Traces.SingleEdit(doc, EditType.BlockPaste, 0.5);
        var edited = doc.Remove(edit.Offset, edit.DeleteLength)
            .Insert(edit.Offset, edit.InsertText);

        var warm = new RealTimeParserSession(Grammar, Docs.StepIdlGrammarPath);
        warm.EnableIncrementalParsing = true;
        warm.Initialize(doc);
        warm.ApplyEdit(edit.Offset, edit.DeleteLength, edit.InsertText);

        var cold = new RealTimeParserSession(Grammar, Docs.StepIdlGrammarPath);
        cold.EnableIncrementalParsing = true;
        cold.Initialize(edited);

        Assert.Equal(cold.LastParse?.Success, warm.LastParse?.Success);
        Assert.Equal(cold.Tokens.Count, warm.Tokens.Count);
        Assert.Equal(
            cold.LastParse?.ResolvedTokens.Count,
            warm.LastParse?.ResolvedTokens.Count);
    }

    [Fact]
    public void RealCSharp_Document_Fully_Lexes_With_CSharp10()
    {
        var grammarPath = Path.Combine(
            AppContext.BaseDirectory, RealCSharp.CSharp10GrammarPath);
        var session = new RealTimeParserSession(
            File.ReadAllText(grammarPath), RealCSharp.CSharp10GrammarPath);
        session.LexingMode = DevelApp.StepLexer.LexingMode.GreedyLongestMatch;
        session.EnableIncrementalParsing = false;

        session.Initialize(RealCSharp.Doc(200));

        Assert.True(session.LastLexConsumedAllInput);
        Assert.True(session.Tokens.Count > 500);
    }
}
