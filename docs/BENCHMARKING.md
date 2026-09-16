# Benchmarking Guide

This repository ships a dedicated performance benchmarking suite based on
[BenchmarkDotNet](https://benchmarkdotnet.org/), covering throughput and memory
usage of the lexer, parser, symbol table and Unicode support.

## The benchmark project

The suite lives in `src/DevelApp.Benchmarks` (a console project, part of the
solution but not a test project, so it never runs during `dotnet test`).

| Benchmark class | What it measures |
|---|---|
| `LexerBenchmarks` | Full `StepLexer` tokenization pipeline (initialize + step loop), phase 1 lexical scan only, and a compiled .NET `Regex` baseline over the same input |
| `ParserBenchmarks` | `StepParserEngine.Parse` full pipeline (lexing + GLR parsing + CognitiveGraph building) and grammar loading cost |
| `SymbolTableBenchmarks` | `ScopeAwareSymbolTable` declaration, scoped lookup (hit / hierarchy walk / miss) and reference resolution |
| `UnicodeBenchmarks` | `UnicodePropertyMatcher.MatchesProperty` across general categories, blocks, `UTF8Utils.GetNextCodepoint` decoding, and .NET `char`/UTF-8 baselines |

All benchmark classes enable `MemoryDiagnoser`, so every result includes Gen0/1/2
collection counts and total allocated bytes.

Test inputs are generated deterministically (fixed seeds), so results are
reproducible across runs and machines.

## Running locally

```bash
# List all available benchmarks
dotnet run -c Release --project src/DevelApp.Benchmarks -- --list flat

# Run everything with the default job
dotnet run -c Release --project src/DevelApp.Benchmarks -- --filter *

# Run a single benchmark class with the short job (fewer iterations)
dotnet run -c Release --project src/DevelApp.Benchmarks -- --filter *LexerBenchmarks* -j short

# Quick smoke run: every benchmark executes exactly once, no statistics
dotnet run -c Release --project src/DevelApp.Benchmarks -- --filter * -j dry

# Restrict to a specific parameter value (note the "Param: value)" syntax)
dotnet run -c Release --project src/DevelApp.Benchmarks -- --filter "*LexerBenchmarks*TokenCount: 1000)"
```

Always use the `Release` configuration; BenchmarkDotNet refuses to produce
meaningful numbers from Debug builds.

Results, including a markdown summary table, are written to
`BenchmarkDotNet.Artifacts/results/` relative to the working directory.

> Note: one `--filter` glob per argument is supported. To run several classes,
> pass the `--filter` option multiple times.

## Running in CI

The [Benchmarks workflow](../.github/workflows/benchmarks.yml) runs the suite on
demand via `workflow_dispatch`. It accepts a filter (default `*`) and a job
characteristic (default `short`), runs the suite on `ubuntu-latest` and uploads
the full `BenchmarkDotNet.Artifacts` directory as an artifact.

Benchmarks are intentionally **not** run on every push or pull request — a full
run takes significant time and the results are only meaningful when compared
against a fixed baseline.

## Findings that motivated this suite

The first runs of the suite immediately surfaced several performance
characteristics worth knowing when working on optimization (see the
"CognitiveGraph optimization" roadmap item):

- **Lexer line/column recomputation**: `StepLexer.CalculateLineColumn` walks the
  input from position 0 on every step, making full tokenization cost grow
  quadratically with input size, and each step allocates heavily.
- **Parser allocation volume**: the GLR pipeline allocates over 1 GB for a
  1000-token parse (graph node writes and path cloning dominate), making large
  parses memory-bound.
- **Phase 1 scan is fast**: the raw `Phase1_LexicalScan` path processes 10,000
  tokens in single-digit milliseconds, confirming that the cost sits in the
  step-loop machinery, not in the pattern matching itself.
- **Fixed during this work**: `StepParserEngine.Parse` used to abort lexing with
  "Lexer appears stuck" whenever a whitespace-only step landed on a multiple of
  10, silently under-parsing any input longer than a handful of tokens.

## Adding a new benchmark

1. Add a new class to `src/DevelApp.Benchmarks` decorated with
   `[MemoryDiagnoser]`.
2. Use `[Params]` for input-size knobs and `[GlobalSetup]` for deterministic
   input generation (prefer `InputGenerator` helpers).
3. Keep one `[Benchmark(Baseline = true)]` per comparison group where a
   framework baseline makes sense.
4. Verify with `-j dry` before committing that every case executes.
