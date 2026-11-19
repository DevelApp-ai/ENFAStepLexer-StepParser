# tools/incremental-bench

Measurement tooling for issue #101
([docs/measurements/incremental-parsing-ides](../../docs/measurements/incremental-parsing-ides));
all commands assume the repository root as working directory.

## Runner: `DevelApp.IncrementalBench`

A console project (part of the solution, so CI compiles it) with four
measurement scenarios. Traces and corpora are deterministic pure
functions of the checkout and the seed; see the project sources for the
exact generation rules (`Docs`, `Traces`, `RealCSharp`).

```text
--scenario all|peredit|trace|coldstart|realex   what to measure
--sizes 100,500,2000,5000                       document sizes (lines)
--reps 3                                        repetitions per cell
--seed 101                                      deterministic trace seed
--pos 0.1,0.5,0.9                               (fixed protocol positions)
--type <EditType>                               filter to one edit type
--coldstart-size 1000                            cold-start curve document
--out DIR                                        results (CSV + environment.json)
```

Arms (per-edit table):

- `spi` — warm session, `EnableIncrementalParsing = true`: one timed
  edit = windowed re-lex + resumed parse.
- `full` — full-reparse configuration: a timed full relex+reparse of the
  edited document per edit.
- `lex` — parse disabled: the timed pure windowed suffix re-lex (the
  lexer-cache column).

The per-edit scenario cross-checks every cell: SPI's parse of the edited
text must agree with the full arm's reparse on success flag, token count
and resolved-token count (`guard` column); the trace scenario
additionally cold-parse-guards every fifth edit.

Correctness guard budget: sequential scenarios also replay identical
traces across arms, so arm latencies compare like-for-like.

## Summarizer: `summarize.py`

```bash
python3 tools/incremental-bench/summarize.py --results bench-results --out report.md
```

Standard library only. Produces the issue #101 tables: per-edit p50/p95
per arm, hit rates per edit type, log-log size-scaling exponents, the
cold-start curve, per-edit allocation overhead, edit-size/latency
Pearson correlation and the guard census.

## tree-sitter arm: `treesitter_arm.py`

Runs on fixed hardware only (needs `pip install tree-sitter
tree-sitter-c-sharp`); the dev container has no pip. Reproduces the
same real-C# corpus and the same deterministic traces and emits
`ts.csv` (full vs incremental parse latency per edit).

## Known engine effects affecting the results

Measured as-shipped, not fixed here (see the measurement README):

1. SPI v1 cache policy (`DecimateOldestHalf`) collapses the resume
   boundary to 1 at 2000+ lines — the spi arm's size-scaling exponent
   converges with full-reparse.
2. Parse-context symbol-table state persists across parses on one
   engine: sequential-parse sessions grow the heap per parse and warm
   edit latency grows across a session; the coldstart full arm
   measures on a fresh engine per edit for this reason.
