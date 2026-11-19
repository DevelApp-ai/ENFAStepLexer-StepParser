#nullable enable
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using DevelApp.StepLexer;
using DevelApp.StepParser;

namespace DevelApp.IncrementalBench;

/// <summary>
/// The issue #101 benchmark runner (docs/measurements/
/// incremental-parsing-ides). Measures per-edit latency and cache
/// behavior of the engine's three IDE configurations on deterministic
/// documents and identical, replayed edit traces:
/// <list type="bullet">
/// <item><c>spi</c>: Selective Path Invalidation (incremental lex +
/// resumed parse, <see cref="RealTimeParserSession.EnableIncrementalParsing"/>).</item>
/// <item><c>full</c>: full-reparse configuration (each edit relexes and
/// reparses the whole document).</item>
/// <item><c>lex</c>: incremental lex only (the windowed suffix re-lex
/// of issue #144, no parse), the pure lexer-cache column.</item>
/// </list>
/// Real C# files are measured at the lexer level (the level at which
/// the engine supports real C#, see <see cref="RealCSharp"/>) in the
/// <c>realex</c> scenario. All traces are deterministic functions of
/// (document, scenario, seed): every arm replays byte-identical edits.
/// </summary>
internal static class Program
{
    private const int DefaultReps = 3;
    private const int TraceEdits = 15;
    private const int ColdStartEdits = 20;
    private const int ColdStartSize = 2000;
    private static readonly int[] DefaultSizes = { 100, 500, 2000, 5000 };
    private static readonly double[] Positions = { 0.1, 0.5, 0.9 };
    private static readonly EditType[] AllEditTypes =
    {
        EditType.CharInsert,
        EditType.WordCompletion,
        EditType.LineInsertion,
        EditType.BlockPaste,
    };
    private static readonly string[] Scenarios = { "peredit", "trace", "coldstart", "realex" };

    private static int Main(string[] args)
    {
        var scenario = Arg(args, "--scenario") ?? "all";
        var sizes = ParseSizes(Arg(args, "--sizes") ?? "100,500,2000,5000");
        var reps = Arg(args, "--reps") is { } repsArg
            ? int.Parse(repsArg, CultureInfo.InvariantCulture)
            : DefaultReps;

        var outDir = Arg(args, "--out") ?? "bench-results";
        var seed = int.Parse(Arg(args, "--seed") ?? "101", CultureInfo.InvariantCulture);
        var coldStartSize = int.Parse(
            Arg(args, "--coldstart-size") ?? ColdStartSize.ToString(CultureInfo.InvariantCulture),
            CultureInfo.InvariantCulture);
        var typeFilter = Arg(args, "--type");
        var confirmed = scenario == "all" || Scenarios.Contains(scenario)
            ? scenario
            : throw new ArgumentException(
                $"Unknown scenario '{scenario}'. Valid: all, {string.Join(", ", Scenarios)}");

        Directory.CreateDirectory(outDir);
        WriteEnvironment(args, confirmed, sizes, reps, seed, outDir);

        var active = confirmed == "all" ? Scenarios : new[] { confirmed };
        if (active.Contains("peredit") || active.Contains("trace") || active.Contains("coldstart"))
        {
            WarmUp(outDir);
        }

        var stepIdlGrammar = ReadGrammar(Docs.StepIdlGrammarPath);
        var editTypes = AllEditTypes
            .Where(t => typeFilter == null || t.ToString().Equals(typeFilter, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (active.Contains("peredit"))
        {
            RunPerEdit(outDir, stepIdlGrammar, sizes, reps, editTypes);
        }

        if (active.Contains("trace"))
        {
            RunTrace(outDir, stepIdlGrammar, sizes, seed, editTypes);
        }

        if (active.Contains("coldstart"))
        {
            RunColdStart(outDir, stepIdlGrammar, reps, seed, coldStartSize, editTypes);
        }

        if (active.Contains("realex"))
        {
            RunRealCSharp(outDir, sizes, reps, editTypes);
        }

        Console.WriteLine("done: " + string.Join(", ", active) + " -> " + Path.GetFullPath(outDir));
        return 0;
    }

    /// <summary>
    /// One measured row of the shared CSV schema. Fields use
    /// sentinel -1 for "not produced by this arm" so the CSV stays
    /// single-schema across all four scenarios.
    /// </summary>
    private sealed class Row
    {
        public string Scenario = "";
        public int Size;
        public string Arm = "";
        public string Type = "";
        public double Pos = -1;
        public int Rep;
        public int EditIndex = -1;
        public int Offset = -1;
        public int DeleteLength = -1;
        public int InsertLength = -1;
        public double LexReuseRatio = -1;
        public int LexReusedTokens = -1;
        public int LexRelexedTokens = -1;
        public bool? LexConsumedAll;
        public bool? ParseCacheHit;
        public int ParseResumeBoundary = -1;
        public int ParseSteps = -1;
        public int CacheBoundaryCount = -1;
        public int Tokens = -1;
        public int ParseResolved = -1;
        public bool? ParseSuccess;
        public double EditMs;
        public long AllocBytes;
        public string Guard = "";

        public string ToCsv()
        {
            var g = CultureInfo.InvariantCulture;
            return string.Join(',',
                Escape(Scenario), Size, Escape(Arm), Escape(Type),
                Pos.ToString("0.##", g), Rep, EditIndex, Offset, DeleteLength, InsertLength,
                LexReuseRatio.ToString("0.####", g), LexReusedTokens, LexRelexedTokens,
                LexConsumedAll == null ? "" : LexConsumedAll.ToString(),
                ParseCacheHit == null ? "" : ParseCacheHit.ToString(),
                ParseResumeBoundary, ParseSteps, CacheBoundaryCount, Tokens, ParseResolved,
                ParseSuccess == null ? "" : ParseSuccess.ToString(),
                EditMs.ToString("0.000", g), AllocBytes, Escape(Guard));
        }

        private static string Escape(string value)
        {
            if (!value.Contains(',') && !value.Contains('"'))
            {
                return value;
            }

            return '"' + value.Replace("\"", "\"\"") + '"';
        }
    }

    /// <summary>Append-only CSV writer that adds the header once per file.</summary>
    private sealed class CsvSink : IDisposable
    {
        private const string Header =
            "scenario,size,arm,type,pos,rep,editIndex,offset,deleteLength,insertLength,"
            + "lexReuseRatio,lexReusedTokens,lexRelexedTokens,lexConsumedAll,parseCacheHit,"
            + "parseResumeBoundary,parseSteps,cacheBoundaryCount,tokens,parseResolved,parseSuccess,"
            + "editMs,allocBytes,guard";

        private readonly StreamWriter _writer;

        public CsvSink(string path)
        {
            var append = File.Exists(path) && new FileInfo(path).Length > 0;
            _writer = new StreamWriter(
                new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read),
                new UTF8Encoding(false))
            {
                // Persist each row immediately: if a scenario crashes the
                // host, already-measured rows survive for triage.
                AutoFlush = true,
            };
            if (!append)
            {
                _writer.WriteLine(Header);
            }
        }

        public void Write(Row row) => _writer.WriteLine(row.ToCsv());

        public void Dispose() => _writer.Dispose();
    }

    // ------------------------------------------------------------------
    // Scenario: per-edit latency table (size x type x position x arm).
    // ------------------------------------------------------------------

    private static void RunPerEdit(string outDir, string grammar, int[] sizes, int reps, EditType[] editTypes)
    {
        using var sink = new CsvSink(Path.Combine(outDir, "peredit.csv"));
        foreach (var size in sizes)
        {
            var doc = Docs.StepIdl(size);
            foreach (var type in editTypes)
            {
                foreach (var pos in Positions)
                {
                    var edit = Traces.SingleEdit(doc, type, pos);
                    for (var rep = 1; rep <= reps; rep++)
                    {
                        var spi = MeasureSpi(grammar, doc, edit);
                        var full = MeasureFull(grammar, doc, edit);
                        var lex = MeasureLex(grammar, doc, edit);
                        foreach (var row in new[] { spi, full, lex })
                        {
                            row.Scenario = "peredit";
                            row.Size = size;
                            row.Type = type.ToString();
                            row.Pos = pos;
                            row.Rep = rep;
                        }

                        CheckAgainstReference(spi, full);
                        sink.Write(spi);
                        sink.Write(full);
                        sink.Write(lex);
                    }
                }
            }

            Console.WriteLine("peredit: size " + size + " done");
        }
    }

    /// <summary>
    /// SPI arm: warm session (Initialize ran the cold parse that
    /// populates the cache), then one timed edit = windowed re-lex +
    /// resumed parse.
    /// </summary>
    private static Row MeasureSpi(string grammar, string doc, Edit edit)
    {
        var session = new RealTimeParserSession(grammar, Docs.StepIdlGrammarPath);
        session.EnableIncrementalParsing = true;
        session.Initialize(doc);
        Row row;
        GC.Collect();
        var alloc0 = GC.GetAllocatedBytesForCurrentThread();
        var sw = Stopwatch.StartNew();
        session.ApplyEdit(edit.Offset, edit.DeleteLength, edit.InsertText);
        sw.Stop();
        row = FillLexRow(session, edit);
        row.Arm = "spi";
        row.DeleteLength = edit.DeleteLength;
        row.InsertLength = edit.InsertText.Length;
        row.EditMs = sw.Elapsed.TotalMilliseconds;
        row.AllocBytes = GC.GetAllocatedBytesForCurrentThread() - alloc0;
        row.ParseCacheHit = session.LastParseUsedCache;
        row.ParseResumeBoundary = session.LastParseResumeBoundary;
        row.ParseSteps = session.LastParseStepCount;
        row.CacheBoundaryCount = session.ParseCacheBoundaryCount;
        row.Tokens = session.Tokens.Count;
        row.ParseResolved = session.LastParse?.ResolvedTokens.Count ?? -1;
        row.ParseSuccess = session.LastParse?.Success;
        return row;
    }

    /// <summary>
    /// Full-reparse arm: the same edit, then a timed full parse
    /// (<see cref="RealTimeParserSession.Parse"/> relexes and reparses
    /// the whole document, the IDE full-reparse configuration).
    /// </summary>
    private static Row MeasureFull(string grammar, string doc, Edit edit)
    {
        var session = new RealTimeParserSession(grammar, Docs.StepIdlGrammarPath);
        session.EnableIncrementalParsing = false;
        session.Initialize(doc);
        session.ApplyEdit(edit.Offset, edit.DeleteLength, edit.InsertText);
        Row row;
        GC.Collect();
        var alloc0 = GC.GetAllocatedBytesForCurrentThread();
        var sw = Stopwatch.StartNew();
        var result = session.Parse();
        sw.Stop();
        row = FillLexRow(session, edit);
        row.Arm = "full";
        row.EditMs = sw.Elapsed.TotalMilliseconds;
        row.AllocBytes = GC.GetAllocatedBytesForCurrentThread() - alloc0;
        row.Tokens = result.Tokens.Count;
        row.ParseResolved = result.ResolvedTokens.Count;
        row.ParseSuccess = result.Success;
        row.CacheBoundaryCount = 0;
        return row;
    }

    /// <summary>
    /// Lex-only arm: warm session with parsing disabled, one timed
    /// edit = the windowed suffix re-lex of issue #144 (parse fields
    /// stay empty).
    /// </summary>
    private static Row MeasureLex(string grammar, string doc, Edit edit)
    {
        var session = new RealTimeParserSession(grammar, Docs.StepIdlGrammarPath);
        session.EnableIncrementalParsing = false;
        session.Initialize(doc);
        Row row;
        GC.Collect();
        var alloc0 = GC.GetAllocatedBytesForCurrentThread();
        var sw = Stopwatch.StartNew();
        session.ApplyEdit(edit.Offset, edit.DeleteLength, edit.InsertText);
        sw.Stop();
        row = FillLexRow(session, edit);
        row.Arm = "lex";
        row.EditMs = sw.Elapsed.TotalMilliseconds;
        row.AllocBytes = GC.GetAllocatedBytesForCurrentThread() - alloc0;
        row.Tokens = session.Tokens.Count;
        return row;
    }

    /// <summary>
    /// Cross-arm correctness guard: the SPI parse of the edited
    /// document must agree with the full reparse on success flag,
    /// token count and resolved-token count.
    /// </summary>
    private static void CheckAgainstReference(Row spi, Row full)
    {
        var mismatches = new List<string>();
        if (spi.ParseSuccess != full.ParseSuccess)
        {
            mismatches.Add("parseSuccess " + spi.ParseSuccess + "/" + full.ParseSuccess);
        }

        if (spi.Tokens != full.Tokens)
        {
            mismatches.Add("tokens " + spi.Tokens + "/" + full.Tokens);
        }

        if (spi.ParseResolved != full.ParseResolved)
        {
            mismatches.Add("resolved " + spi.ParseResolved + "/" + full.ParseResolved);
        }

        spi.Guard = full.Guard = mismatches.Count == 0
            ? "ok"
            : "MISMATCH:" + string.Join(";", mismatches);
    }

    private static Row FillLexRow(RealTimeParserSession session, Edit edit)
    {
        return new Row
        {
            Offset = edit.Offset,
            DeleteLength = edit.DeleteLength,
            InsertLength = edit.InsertText.Length,
            LexReuseRatio = session.LastReuseRatio,
            LexReusedTokens = session.LastReusedTokenCount,
            LexRelexedTokens = session.LastRelexedTokenCount,
            LexConsumedAll = session.LastLexConsumedAllInput,
        };
    }

    // ------------------------------------------------------------------
    // Scenario: sequential-edit hit-rate traces (per edit type, warm
    // session, raw instrumentation, periodic cold-parse guard).
    // ------------------------------------------------------------------

    private static void RunTrace(string outDir, string grammar, int[] sizes, int seed, EditType[] editTypes)
    {
        using var sink = new CsvSink(Path.Combine(outDir, "trace.csv"));
        foreach (var size in sizes)
        {
            var doc = Docs.StepIdl(size);
            for (var typeIndex = 0; typeIndex < AllEditTypes.Length; typeIndex++)
            {
                var type = AllEditTypes[typeIndex];
                if (!editTypes.Contains(type))
                {
                    continue;
                }
                var edits = Traces.Sequential(doc, type, TraceEdits, seed + size + typeIndex);
                var session = new RealTimeParserSession(grammar, Docs.StepIdlGrammarPath);
                session.EnableIncrementalParsing = true;
                session.Initialize(doc);

                var text = doc;
                for (var i = 0; i < edits.Count; i++)
                {
                    var edit = edits[i];
                    Row row;
                    GC.Collect();
                    var alloc0 = GC.GetAllocatedBytesForCurrentThread();
                    var sw = Stopwatch.StartNew();
                    session.ApplyEdit(edit.Offset, edit.DeleteLength, edit.InsertText);
                    sw.Stop();
                    row = FillLexRow(session, edit);
                    text = Traces.Apply(text, edit);
                    row.Scenario = "trace";
                    row.Size = size;
                    row.Arm = "spi";
                    row.Type = type.ToString();
                    row.Rep = 1;
                    row.EditIndex = i;
                    row.EditMs = sw.Elapsed.TotalMilliseconds;
                    row.AllocBytes = GC.GetAllocatedBytesForCurrentThread() - alloc0;
                    row.ParseCacheHit = session.LastParseUsedCache;
                    row.ParseResumeBoundary = session.LastParseResumeBoundary;
                    row.ParseSteps = session.LastParseStepCount;
                    row.CacheBoundaryCount = session.ParseCacheBoundaryCount;
                    row.Tokens = session.Tokens.Count;
                    row.ParseResolved = session.LastParse?.ResolvedTokens.Count ?? -1;
                    row.ParseSuccess = session.LastParse?.Success;

                    if ((i + 1) % 5 == 0)
                    {
                        row.Guard = ColdReferenceGuard(grammar, text, session);
                    }

                    sink.Write(row);
                }
            }

            Console.WriteLine("trace: size " + size + " done");
        }
    }

    /// <summary>
    /// Cold-parse guard for the trace: a fresh full parse of the same
    /// text must agree with the incrementally maintained session.
    /// </summary>
    private static string ColdReferenceGuard(string grammar, string text, RealTimeParserSession warm)
    {
        var reference = new RealTimeParserSession(grammar, Docs.StepIdlGrammarPath);
        reference.EnableIncrementalParsing = true;
        reference.Initialize(text);
        var same = warm.LastParse?.Success == reference.LastParse?.Success
            && warm.Tokens.Count == reference.Tokens.Count
            && warm.LastParse?.ResolvedTokens.Count == reference.LastParse?.ResolvedTokens.Count;
        return same
            ? "ok"
            : "MISMATCH: warm " + warm.LastParse?.Success + "/" + warm.Tokens.Count + "/"
                + warm.LastParse?.ResolvedTokens.Count
            + " vs cold " + reference.LastParse?.Success + "/" + reference.Tokens.Count + "/"
                + reference.LastParse?.ResolvedTokens.Count;
    }

    // ------------------------------------------------------------------
    // Scenario: cold-start curve (first N sequential edits after
    // opening the file, spi vs full per edit index).
    // ------------------------------------------------------------------

    private static void RunColdStart(string outDir, string grammar, int reps, int seed, int size, EditType[] editTypes)
    {
        using var sink = new CsvSink(Path.Combine(outDir, "coldstart.csv"));
        var editType = editTypes.FirstOrDefault(EditType.CharInsert);
        var doc = Docs.StepIdl(size);
        var edits = Traces.Sequential(doc, editType, ColdStartEdits, seed);
        for (var rep = 1; rep <= reps; rep++)
        {
            // SPI configuration.
            var spi = new RealTimeParserSession(grammar, Docs.StepIdlGrammarPath);
            spi.EnableIncrementalParsing = true;
            spi.Initialize(doc);
            for (var i = 0; i < edits.Count; i++)
            {
                var edit = edits[i];
                GC.Collect();
                var alloc0 = GC.GetAllocatedBytesForCurrentThread();
                var sw = Stopwatch.StartNew();
                spi.ApplyEdit(edit.Offset, edit.DeleteLength, edit.InsertText);
                sw.Stop();
                var row = FillLexRow(spi, edit);
                row.Scenario = "coldstart";
                row.Size = size;
                row.Arm = "spi";
                row.Type = editType.ToString();
                row.Rep = rep;
                row.EditIndex = i;
                row.EditMs = sw.Elapsed.TotalMilliseconds;
                row.AllocBytes = GC.GetAllocatedBytesForCurrentThread() - alloc0;
                row.ParseCacheHit = spi.LastParseUsedCache;
                row.ParseResumeBoundary = spi.LastParseResumeBoundary;
                row.ParseSteps = spi.LastParseStepCount;
                row.CacheBoundaryCount = spi.ParseCacheBoundaryCount;
                row.Tokens = spi.Tokens.Count;
                row.ParseSuccess = spi.LastParse?.Success;
                sink.Write(row);
            }

            // Full-reparse configuration: identical replayed edits. The
            // parse runs on a FRESH engine per edit (grammar load
            // untimed): the engine as shipped retains parse-context
            // symbol-table state across Parse calls on one engine, so a
            // shared-engine measurement would grow the heap linearly and
            // diverge observably (see the engine issue referenced from
            // the measurement report). A fresh engine per edit measures
            // the per-edit full-reparse cost itself.
            var fullText = doc;
            for (var i = 0; i < edits.Count; i++)
            {
                var edit = edits[i];
                fullText = Traces.Apply(fullText, edit);
                var engine = new StepParserEngine();
                engine.LoadGrammarFromContent(grammar, Docs.StepIdlGrammarPath);
                GC.Collect();
                var alloc0 = GC.GetAllocatedBytesForCurrentThread();
                var sw = Stopwatch.StartNew();
                var result = engine.Parse(fullText, "coldstart.txt");
                sw.Stop();
                var row = new Row
                {
                    Scenario = "coldstart",
                    Size = size,
                    Arm = "full",
                    Type = editType.ToString(),
                    Rep = rep,
                    EditIndex = i,
                    Offset = edit.Offset,
                    DeleteLength = edit.DeleteLength,
                    InsertLength = edit.InsertText.Length,
                    EditMs = sw.Elapsed.TotalMilliseconds,
                    AllocBytes = GC.GetAllocatedBytesForCurrentThread() - alloc0,
                    Tokens = result.Tokens.Count,
                    ParseResolved = result.ResolvedTokens.Count,
                    ParseSuccess = result.Success,
                };
                sink.Write(row);
            }

            Console.WriteLine("coldstart: rep " + rep + " done");
        }
    }

    // ------------------------------------------------------------------
    // Scenario: real-C# documents at the lexer level (cold full lex
    // vs windowed suffix re-lex per edit type).
    // ------------------------------------------------------------------

    private static void RunRealCSharp(string outDir, int[] sizes, int reps, EditType[] editTypes)
    {
        using var sink = new CsvSink(Path.Combine(outDir, "realex.csv"));
        var grammar = ReadGrammar(RealCSharp.CSharp10GrammarPath);
        foreach (var size in sizes)
        {
            var doc = RealCSharp.Doc(size);

            // Cold full lex (document open), one row per rep.
            for (var rep = 1; rep <= reps; rep++)
            {
                var cold = new RealTimeParserSession(grammar, RealCSharp.CSharp10GrammarPath);
                cold.LexingMode = LexingMode.GreedyLongestMatch;
                cold.EnableIncrementalParsing = false;
                GC.Collect();
                var alloc0 = GC.GetAllocatedBytesForCurrentThread();
                var sw = Stopwatch.StartNew();
                cold.Initialize(doc);
                sw.Stop();
                var coldRow = new Row
                {
                    Scenario = "realex",
                    Size = size,
                    Arm = "coldlex",
                    Type = "cold",
                    Rep = rep,
                    LexConsumedAll = cold.LastLexConsumedAllInput,
                    Tokens = cold.Tokens.Count,
                    EditMs = sw.Elapsed.TotalMilliseconds,
                    AllocBytes = GC.GetAllocatedBytesForCurrentThread() - alloc0,
                };
                sink.Write(coldRow);
            }

            // Warm suffix re-lex per edit type.
            foreach (var type in editTypes)
            {
                for (var rep = 1; rep <= reps; rep++)
                {
                    var session = new RealTimeParserSession(grammar, RealCSharp.CSharp10GrammarPath);
                    session.LexingMode = LexingMode.GreedyLongestMatch;
                    session.EnableIncrementalParsing = false;
                    session.Initialize(doc);
                    var edit = Traces.SingleEdit(doc, type, 0.5);
                    GC.Collect();
                    var alloc1 = GC.GetAllocatedBytesForCurrentThread();
                    var s = Stopwatch.StartNew();
                    session.ApplyEdit(edit.Offset, edit.DeleteLength, edit.InsertText);
                    s.Stop();
                    var row = FillLexRow(session, edit);
                    row.Scenario = "realex";
                    row.Size = size;
                    row.Arm = "lex";
                    row.Type = type.ToString();
                    row.Pos = 0.5;
                    row.Rep = rep;
                    row.EditMs = s.Elapsed.TotalMilliseconds;
                    row.AllocBytes = GC.GetAllocatedBytesForCurrentThread() - alloc1;
                    row.Tokens = session.Tokens.Count;
                    sink.Write(row);
                }
            }

            Console.WriteLine("realex: size " + size + " done");
        }
    }

    // ------------------------------------------------------------------
    // Support.
    // ------------------------------------------------------------------

    private static void WarmUp(string outDir)
    {
        // JIT warm-up: one unrecorded pass of every measured code path
        // at size 100 keeps the recorded rows free of first-hit JIT and
        // grammar-NFA compile costs.
        var grammar = ReadGrammar(Docs.StepIdlGrammarPath);
        var doc = Docs.StepIdl(100);
        var edit = Traces.SingleEdit(doc, EditType.CharInsert, 0.5);
        MeasureSpi(grammar, doc, edit);
        MeasureFull(grammar, doc, edit);
        MeasureLex(grammar, doc, edit);

        var csharp = ReadGrammar(RealCSharp.CSharp10GrammarPath);
        var realDoc = RealCSharp.Doc(100);
        var session = new RealTimeParserSession(csharp, RealCSharp.CSharp10GrammarPath);
        session.LexingMode = LexingMode.GreedyLongestMatch;
        session.EnableIncrementalParsing = false;
        session.Initialize(realDoc);
        var realEdit = Traces.SingleEdit(realDoc, EditType.CharInsert, 0.5);
        session.ApplyEdit(realEdit.Offset, realEdit.DeleteLength, realEdit.InsertText);
        _ = outDir;
    }

    private static string ReadGrammar(string relativePath)
    {
        var path = Path.Combine(AppContext.BaseDirectory, relativePath);
        if (!File.Exists(path))
        {
            // Fall back to the project directory for dotnet-run.
            var project = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", relativePath);
            if (File.Exists(project))
            {
                path = project;
            }
        }

        return File.ReadAllText(path);
    }

    private static void WriteEnvironment(string[] args, string scenario, int[] sizes, int reps, int seed, string outDir)
    {
        var payload = new
        {
            timestampUtc = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
            machine = Environment.MachineName,
            os = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
            osArchitecture = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString(),
            processArchitecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
            processorCount = Environment.ProcessorCount,
            dotnetVersion = Environment.Version.ToString(),
            scenario,
            sizes,
            reps,
            seed,
            args = string.Join(" ", args),
        };
        File.WriteAllText(
            Path.Combine(outDir, "environment.json"),
            JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static int[] ParseSizes(string csv)
    {
        return csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(int.Parse)
            .ToArray();
    }

    private static string? Arg(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == name)
            {
                return args[i + 1];
            }
        }

        return null;
    }
}
