using DevelApp.IncrementalBench;
using Xunit;

namespace DevelApp.IncrementalBench.Tests;

/// <summary>
/// Determinism tests for the issue #101 benchmark corpora and traces
/// (docs/measurements/incremental-parsing-ides): every arm, run and
/// machine must replay byte-identical documents and edits, so the
/// generators must be pure functions of their inputs.
/// </summary>
public class CorpusDeterminismTests
{
    [Fact]
    public void StepIdl_Documents_Are_Deterministic_And_Exact()
    {
        var first = Docs.StepIdl(500);
        var second = Docs.StepIdl(500);

        Assert.Equal(first, second);
        Assert.Equal(500, first.Count(c => c == '\n'));
        Assert.DoesNotContain(first, c => c > 127);
    }

    [Fact]
    public void StepIdl_Line_Count_Varies_With_Parameter()
    {
        Assert.Equal(100, Docs.StepIdl(100).Count(c => c == '\n'));
        Assert.Equal(5000, Docs.StepIdl(5000).Count(c => c == '\n'));
    }

    [Fact]
    public void Traces_Are_Deterministic_In_The_Seed()
    {
        var doc = Docs.StepIdl(100);

        var first = Traces.Sequential(doc, EditType.CharInsert, 20, seed: 101);
        var second = Traces.Sequential(doc, EditType.CharInsert, 20, seed: 101);

        Assert.Equal(first, second);
        Assert.Equal(20, first.Count);
    }

    [Fact]
    public void Traces_Differ_With_The_Seed()
    {
        var doc = Docs.StepIdl(100);

        var first = Traces.Sequential(doc, EditType.CharInsert, 20, seed: 101);
        var second = Traces.Sequential(doc, EditType.CharInsert, 20, seed: 202);

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void Traces_Apply_Matches_String_Edits()
    {
        var doc = Docs.StepIdl(50);
        var edit = Traces.SingleEdit(doc, EditType.LineInsertion, 0.5);

        var applied = Traces.Apply(doc, edit);
        var expected = doc.Remove(edit.Offset, edit.DeleteLength)
            .Insert(edit.Offset, edit.InsertText);

        Assert.Equal(expected, applied);
    }

    [Fact]
    public void Traces_All_Edit_Types_Are_Insertions()
    {
        var doc = Docs.StepIdl(20);

        foreach (var type in Enum.GetValues<EditType>())
        {
            var edit = Traces.SingleEdit(doc, type, 0.1);
            Assert.Equal(0, edit.DeleteLength);
            Assert.True(edit.InsertText.Length > 0);
            Assert.InRange(edit.Offset, 0, doc.Length - 1);
        }
    }

    [Fact]
    public void RealCSharp_Documents_Are_Exact_Line_Counts()
    {
        Assert.Equal(100, RealCSharp.Doc(100).Count(c => c == '\n'));
        Assert.Equal(500, RealCSharp.Doc(500).Count(c => c == '\n'));
    }

    [Fact]
    public void RealCSharp_Documents_Are_Prefixes_As_Size_Grows()
    {
        var small = RealCSharp.Doc(100);
        var large = RealCSharp.Doc(500);

        // Line j of Doc(N) must be the same source line in every size:
        // the benchmark's position-based traces stay comparable across
        // size rows only under this prefix property.
        var smallLines = small.Split('\n');
        var largeLines = large.Split('\n');
        // Split leaves a trailing empty entry after the final newline;
        // compare the real lines only.
        for (var i = 0; i < smallLines.Length - 1; i++)
        {
            Assert.Equal(smallLines[i], largeLines[i]);
        }
    }
}
