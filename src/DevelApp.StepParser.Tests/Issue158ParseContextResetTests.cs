using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using DevelApp.StepLexer;
using Xunit;

namespace DevelApp.StepParser.Tests
{
    /// <summary>
    /// Regression tests for parse-context and builder retention across
    /// sequenced parses on one long-lived parser instance
    /// (ENFAStepLexer-StepParser issue #158).
    /// </summary>
    public class Issue158ParseContextResetTests
    {
        private static CodeLocation MakeLocation(string file, int sl, int sc, int el, int ec)
        {
            return new CodeLocation(file, sl, sc, el, ec);
        }

        private sealed class RecordingSymbolTable : IScopeAwareSymbolTable
        {
            public int Declarations;

            public void Declare(string name, string type, string scope, ICodeLocation location)
            {
                Declarations++;
            }

            public SymbolEntry? Lookup(string name, string scope) => null;

            public bool Exists(string name, string scope) => false;

            public IEnumerable<SymbolEntry> GetSymbols(string scope) => Enumerable.Empty<SymbolEntry>();

            public IEnumerable<SymbolEntry> FindAllReferences(string name) => Enumerable.Empty<SymbolEntry>();
        }

        private const string NumberGrammar =
            "Grammar: Numbers158\n" +
            "<NUMBER> ::= /[0-9]+/\n" +
            "<expr> ::= <NUMBER>";

        private static string StepIdlGrammar()
        {
            return File.ReadAllText(Path.Combine(
                AppContext.BaseDirectory, "Corpus", "StepIdl.grammar"));
        }

        private static string StepIdlDocument(int lines)
        {
            var sb = new System.Text.StringBuilder(lines * 24);
            for (var i = 0; i < lines; i++)
            {
                sb.Append('v').Append(i).Append(" = a").Append(i % 7)
                  .Append(" + 12 + b").Append(i % 13).Append('\n');
            }
            return sb.ToString();
        }

        private static StepParserEngine NumberEngine()
        {
            var engine = new StepParserEngine();
            engine.LoadGrammarFromContent(NumberGrammar);
            return engine;
        }

        [Fact]
        public void InitializedParse_DropsSymbolTableStateOfEarlierParses()
        {
            var engine = NumberEngine();
            Assert.True(engine.Parse("123").Success);

            // Simulate an external declarer (benchmark harness, analyzer
            // session) building state against the parser's context between
            // parses.
            var table = engine.InternalParser.Context.SymbolTable as ScopeAwareSymbolTable;
            Assert.NotNull(table);
            table.Project = "sample";
            for (var i = 0; i < 100; i++)
            {
                table.Declare(
                    "Sym" + i,
                    "class",
                    "N" + i,
                    MakeLocation("One.cs", 3, 18, 3, 19),
                    MakeLocation("One.cs", 3, 18, 3, 19));
            }

            Assert.NotNull(table.Lookup("Sym42", "N42"));

            // A new top-level parse re-initializes the parser: the state of
            // earlier parses must not leak into this parse.
            var next = engine.Parse("456", "seq.txt");

            Assert.True(next.Success);
            Assert.Null(table.Lookup("Sym42", "N42"));
            Assert.Empty(table.GetSymbols("N42"));
        }

        [Fact]
        public void SequencedParses_EachParseStartsWithEmptySymbolTable()
        {
            var engine = NumberEngine();
            Assert.True(engine.Parse("123").Success);

            for (var parse = 1; parse <= 10; parse++)
            {
                var table = engine.InternalParser.Context.SymbolTable as ScopeAwareSymbolTable;
                Assert.NotNull(table);
                for (var i = 0; i < 50; i++)
                {
                    table.Declare(
                        "Leak" + parse + "_" + i,
                        "field",
                        "N" + i,
                        MakeLocation("One.cs", 3, 18, 3, 19));
                }

                var result = engine.Parse((parse % 2 == 0 ? "123" : "456"), "loop.txt");

                Assert.True(result.Success);
                Assert.Empty(engine.InternalParser.Context.SymbolTable.FindAllReferences("Leak" + parse + "_1"));
            }
        }

        [Fact]
        public void ReDeclarationOfSameSymbolId_UpdatesEntryInPlace()
        {
            var table = new ScopeAwareSymbolTable { Project = "sample" };
            table.Declare("A", "class", "N1", MakeLocation("One.cs", 3, 1, 3, 20), MakeLocation("One.cs", 3, 18, 3, 19));
            var firstRef = table.Lookup("A", "N1")!.SymbolRef;

            // The same stable identity re-declared after an incremental
            // re-parse updates the existing entry instead of appending.
            var updatedSpan = MakeLocation("One.cs", 4, 18, 4, 19);
            table.Declare("A", "class", "N1", MakeLocation("One.cs", 4, 1, 4, 20), updatedSpan);

            var entries = table.FindAllReferences("A").ToList();
            Assert.Single(entries);
            Assert.Equal(firstRef, table.Lookup("A", "N1")!.SymbolRef);
            Assert.Equal("sample|N1.A", firstRef.Id);
            var viaRef = table.LookupRef(firstRef);
            Assert.NotNull(viaRef);
            Assert.Equal(4, viaRef!.Location.StartLine);
            Assert.Equal(updatedSpan, viaRef.IdentifierSpan);
        }

        [Fact]
        public void ReDeclarationInDifferentScope_StillDistinct()
        {
            var table = new ScopeAwareSymbolTable { Project = "sample" };
            table.Declare("A", "class", "N1", MakeLocation("One.cs", 3, 18, 3, 19));
            table.Declare("A", "class", "N2", MakeLocation("Two.cs", 3, 18, 3, 19));
            table.Declare("A", "class", "N1", MakeLocation("One.cs", 5, 18, 5, 19));

            // N1 was re-declared (in place) but N2 stays distinct.
            Assert.Equal(2, table.FindAllReferences("A").Count());
            Assert.Equal("sample|N2.A", table.Lookup("A", "N2")!.SymbolRef.Id);
        }

        [Fact]
        public void Clear_KeepsProjectAndDropsDeclarations()
        {
            var table = new ScopeAwareSymbolTable { Project = "sample" };
            table.Declare("A", "class", "N1", MakeLocation("One.cs", 3, 18, 3, 19));

            table.Clear();

            Assert.Equal("sample", table.Project);
            Assert.Null(table.Lookup("A", "N1"));
            Assert.False(table.Exists("A", "N1"));
            Assert.Empty(table.GetSymbols("N1"));
        }

        [Fact]
        public void ResetParseState_CustomTable_ReplacedWithDefault()
        {
            var custom = new RecordingSymbolTable();
            var context = new ParseContext { SymbolTable = custom };
            context.Variables["carry"] = 42;
            context.ActiveContexts.Add("block");

            context.ResetParseState();

            // A custom implementation cannot be cleared through the
            // interface, so Initialize replaces it with a fresh default
            // table, mirroring the state of a brand-new engine.
            Assert.NotSame(custom, context.SymbolTable);
            Assert.IsType<ScopeAwareSymbolTable>(context.SymbolTable);
            Assert.Empty(context.Variables);
            Assert.Empty(context.ActiveContexts);
            Assert.NotNull(context.ContextStack);
        }

        [Fact]
        public void ParseStepBudget_MatchesDocumentedContract()
        {
            Assert.Equal(64, ParseStepBudget.ForTokenCount(0));
            Assert.Equal(84, ParseStepBudget.ForTokenCount(1));
            Assert.Equal(2064, ParseStepBudget.ForTokenCount(100));
            for (var tokens = 0; tokens <= 500; tokens += 25)
            {
                // Engine and real-time session both derive their budget from
                // this one definition, so their step budgets stay identical
                // for every token count.
                Assert.Equal(tokens * ParseStepBudget.StepsPerToken + ParseStepBudget.MinSteps,
                    ParseStepBudget.ForTokenCount(tokens));
            }
        }

        [Fact]
        public void SequencedParses_OnOneEngine_KeepFlatPerParseWallClock()
        {
            // Retention of the append-only graph builder previously doubled
            // per-parse wall time over a sequence of parses of the same
            // document (measured 2026-11-19: 968 ms growing to 1896 ms over
            // 8 parses). With the builder recreated on Initialize, per-parse
            // time must stay flat. The bound is deliberately loose to stay
            // robust on shared CI runners.
            var engine = new StepParserEngine();
            engine.LoadGrammarFromContent(StepIdlGrammar());
            var document = StepIdlDocument(800);
            var timings = new List<double>();

            for (var parse = 0; parse < 12; parse++)
            {
                var clock = Stopwatch.StartNew();
                var result = engine.Parse(document, "flat.txt");
                clock.Stop();
                Assert.True(result.Success);
                timings.Add(clock.Elapsed.TotalMilliseconds);
            }

            var first = timings[0];
            var last = timings[timings.Count - 1];
            Assert.True(last <= first * 2.0,
                $"Per-parse wall time grew over sequenced parses: first={first:F1} ms, last={last:F1} ms, all=[{string.Join(", ", timings.Select(t => t.ToString("F0")))}]");
        }
    }
}
