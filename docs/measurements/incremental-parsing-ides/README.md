# Incremental parsing for IDEs: measurement protocol and results (issue #101)

This directory holds the issue #101 measurement infrastructure and results
for the three IDE parsing configurations discussed in the
incremental-parsing-ides article:

- **SPI** (Selective Path Invalidation) —
  `RealTimeParserSession.EnableIncrementalParsing = true`: the windowed
  incremental re-lex of issue #144 plus the resumed parse of issue #101.
- **full-reparse** — the traditional configuration: every edit relexes and
  reparses the whole document.
- **tree-sitter** — the reference incremental system, measured on the same
  documents by the committed fixed-hardware script
  ([`tools/incremental-bench/treesitter_arm.py`](../../../tools/incremental-bench/treesitter_arm.py)).

All spatial claims (per-edit latency, cache hit rates, size scaling,
cold start, memory overhead) are reported from committed, deterministic
benchmark artifacts. No table projection is used.

## What is measured where

| Question | Scenario | Corpus | Notes |
|---|---|---|---|
| Per-edit latency per size / edit type / position (p50, p95) | `peredit` | StepIdl 100/500/2000/5000 lines | spi, full, lex arms; all replay identical deterministic edits |
| Cache hit rates per edit type | `trace` | StepIdl | 15-edit warm-session traces, resume boundary + steps + lexer reuse per edit, cold-parse guards every 5 edits |
| Cold-start curve (first 20 edits) | `coldstart` | StepIdl (500 dev / 1000 fixed hardware) | spi vs full per edit index |
| Memory overhead | `peredit`/`coldstart` alloc columns | StepIdl | allocated bytes per timed edit; retention caveat below |
| Real files at the supported level | `realex` | real C# (repo sources) | lexer arm: cold full lex vs windowed suffix re-lex, CSharp10 grammar, `GreedyLongestMatch` |
| Real C# parse-level reference | tree-sitter arm | same real C# corpus rule | not runnable in the dev container (no pip); run on fixed hardware |

### Corpus feasibility (why StepIdl + a purpose-built protocol)

No real-world grammar from the Minotaur-Grammars corpus parses real files
at 2000+ lines today (evidence: [feasibility-sweep.md](feasibility-sweep.md)):
JSON aborts the host, INI/Makefile/YAML token rules collapse file-scale
input to a handful of greedy multi-line tokens, and the natural-language
grammars emit zero tokens on repeated example text. C# parses at the
**lexer** level (that arm uses real repository sources), but the CSharp10
parse layer is combinatorially infeasible at file scale (issue #157).
StepIdl is a purpose-built, line-oriented benchmark grammar that parses
to completion at 5000 lines, making the parse-level incremental behavior
measurable at real file sizes. Both corpora and all traces are
deterministic functions of the checkout and the seed.

## Running

Dev-container (indicative, memory-bounded, `--reps 3`):

```bash
dotnet run --project src/DevelApp.IncrementalBench -c Release -- \
  --scenario peredit --sizes 100,500,2000,5000 --reps 3 --out bench-results
# sequential-edit scenarios leak engine parse-context state on long
# sessions (see "Known engine effects"); bounding per-process:
dotnet run --project src/DevelApp.IncrementalBench -c Release -- \
  --scenario trace --sizes 100,500 --type CharInsert --out bench-results
# ... --type WordCompletion / LineInsertion / BlockPaste ...
dotnet run --project src/DevelApp.IncrementalBench -c Release -- \
  --scenario coldstart --coldstart-size 500 --reps 3 --out bench-results
dotnet run --project src/DevelApp.IncrementalBench -c Release -- \
  --scenario realex --sizes 100,500,2000,5000 --reps 3 --out bench-results
python3 tools/incremental-bench/summarize.py --results bench-results \
  --out bench-results/report.md
```

Fixed hardware (the article protocol, 10 repetitions as required by
issue #101; machine details are recorded in `environment.json`):

```bash
dotnet run --project src/DevelApp.IncrementalBench -c Release -- \
  --scenario all --sizes 100,500,2000,5000 --reps 10 --coldstart-size 1000 \
  --out bench-fixed-hw
python3 tools/incremental-bench/treesitter_arm.py --reps 10 \
  --out bench-fixed-hw/ts.csv
```

On a machine with more than the dev container's 1 GB, the sequential-
edit scenarios can run all sizes in one process; otherwise bound each
process with `--type` / `--sizes` as above.

## Results

- [Dev-container results](results/dev-container/report.md) (raw CSVs
  next to the report; environment in `environment.json`).
- Fixed-hardware results: to be recorded in
  [results/fixed-hardware/](results/fixed-hardware/) with the same
  summarize output once measured (issue #101 acceptance run).

## Known engine effects measured here (not merged fixes)

The harness measures the engine as shipped and surfaces two effects
that the article and follow-up engine work must reflect:

1. **SPI resume-boundary collapse at scale (v1 cache policy).** At
   2000+ lines the `ParsePathCache` `DecimateOldestHalf` policy keeps
   boundary 1 and exponentially sparse distant history, so a mid-
   document edit resumes at boundary 1: the warm parse becomes a full
   restep and the spi arm's size-scaling exponent (0.89 dev-container)
   matches full-reparse. The dev-container per-edit tables quantify
   this directly.
2. **Parse-context retention across parses (memory/latency growth).**
   `ScopeAwareSymbolTable`/`ParseContext` state is never reset between
   parses on one engine instance: sequential-edit sessions grow the
   heap linearly per parse (~30-70 MB retained per 1000-line parse on
   the dev container) and warm per-edit latency grows across a session
   (90 ms -> 290 ms over 20 edits at 1000 lines). Track the filed
   engine issue (linked from issue #157's family) for the fix; the
   `coldstart` full arm therefore measures on a fresh engine per edit.
