using BenchmarkDotNet.Attributes;
using DevelApp.StepLexer;

// The full symbol table with reference tracking ships with DevelApp.StepLexer;
// the DevelApp.StepParser namespace contains a simpler table without it.
using SymbolTable = DevelApp.StepLexer.ScopeAwareSymbolTable;

namespace DevelApp.Benchmarks
{
    /// <summary>
    /// Throughput and memory benchmarks for the scope-aware symbol table,
    /// covering declaration, scoped lookup and reference resolution.
    /// </summary>
    [MemoryDiagnoser]
    public class SymbolTableBenchmarks
    {
        private SymbolTable _table = null!;
        private string[] _lookupKeys = null!;
        private string[] _scopePaths = null!;

        /// <summary>
        /// Gets or sets the number of symbols to declare per run.
        /// </summary>
        [Params(1_000, 10_000)]
        public int SymbolCount { get; set; }

        /// <summary>
        /// Gets or sets the number of nested scopes to distribute symbols over.
        /// </summary>
        [Params(10)]
        public int ScopeCount { get; set; }

        /// <summary>
        /// Populate a symbol table with symbols spread over nested scopes and
        /// prepare a deterministic key sequence for lookups.
        /// </summary>
        [GlobalSetup]
        public void Setup()
        {
            _table = new SymbolTable();

            _scopePaths = new string[ScopeCount];
            for (int s = 0; s < ScopeCount; s++)
            {
                _scopePaths[s] = s == 0 ? "global" : _scopePaths[s - 1] + ".scope" + s;
            }

            for (int i = 0; i < SymbolCount; i++)
            {
                var scope = _scopePaths[i % ScopeCount];
                _table.Declare("symbol" + i, "variable", scope, new CodeLocation());
                _table.AddReference("symbol" + i, scope, new CodeLocation(), "read");
            }

            var random = new Random(42);
            _lookupKeys = new string[SymbolCount];
            for (int i = 0; i < SymbolCount; i++)
            {
                _lookupKeys[i] = "symbol" + random.Next(SymbolCount);
            }
        }

        /// <summary>
        /// Declare all symbols into a fresh table (includes table allocation cost).
        /// </summary>
        /// <returns>The declared symbol count.</returns>
        [Benchmark(Baseline = true)]
        public int Declare()
        {
            var table = new SymbolTable();
            for (int i = 0; i < SymbolCount; i++)
            {
                var scope = _scopePaths[i % ScopeCount];
                table.Declare("symbol" + i, "variable", scope, new CodeLocation());
            }
            return SymbolCount;
        }

        /// <summary>
        /// Look up symbols in the scope they were declared in (all hits).
        /// </summary>
        /// <returns>The number of successful lookups.</returns>
        [Benchmark]
        public int Lookup_Hit()
        {
            var hits = 0;
            for (int i = 0; i < _lookupKeys.Length; i++)
            {
                if (_table.Lookup(_lookupKeys[i], _scopePaths[i % ScopeCount]) != null)
                {
                    hits++;
                }
            }
            return hits;
        }

        /// <summary>
        /// Look up symbols from the deepest scope. Every declared scope is an
        /// ancestor of the deepest scope, so each lookup resolves by walking up
        /// the scope hierarchy.
        /// </summary>
        /// <returns>The number of successful lookups.</returns>
        [Benchmark]
        public int Lookup_ThroughHierarchy()
        {
            var deepest = _scopePaths[ScopeCount - 1];
            var hits = 0;
            for (int i = 0; i < _lookupKeys.Length; i++)
            {
                if (_table.Lookup(_lookupKeys[i], deepest) != null)
                {
                    hits++;
                }
            }
            return hits;
        }

        /// <summary>
        /// Look up symbols that do not exist anywhere in the table.
        /// </summary>
        /// <returns>The number of successful lookups (expected zero).</returns>
        [Benchmark]
        public int Lookup_Miss()
        {
            var hits = 0;
            for (int i = 0; i < _lookupKeys.Length; i++)
            {
                if (_table.Lookup("missing_" + _lookupKeys[i], _scopePaths[i % ScopeCount]) != null)
                {
                    hits++;
                }
            }
            return hits;
        }

        /// <summary>
        /// Resolve all references for each declared symbol.
        /// </summary>
        /// <returns>The total number of references found.</returns>
        [Benchmark]
        public int FindAllReferences()
        {
            var total = 0;
            for (int i = 0; i < SymbolCount; i++)
            {
                total += _table.FindAllReferences("symbol" + i).Length;
            }
            return total;
        }
    }
}
