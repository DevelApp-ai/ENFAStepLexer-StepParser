# ML Performance Baseline (issue #58, sub-issue #73)

Recorded: 2026-09-24 · schema `ml-baseline/1`

Pre-ML baseline of the deterministic engine over the
[Minotaur-Grammars](https://github.com/DevelApp-ai/Minotaur-Grammars) example
corpora — one record per (grammar, `Examples.txt`) pair, with latency
percentiles (p50/p95/p99/min) and allocation profile (allocated bytes
p50/p95/p99/min) per parse, measured over 30 runs after 2 warm-up runs,
fresh `StepParserEngine` per run.

This is the "today" measurement the ML-assisted prototypes
(sub-issues #74 learned rule prioritization, #75 learned GLR path pruning)
must be evaluated against: **no semantic changes and no allocation
regressions** vs. these numbers (issue #58 definition of done).

## File

- `baseline-2026-09-24.csv` — 111 records, one per (grammar, examples) pair.
  Comment header records the environment; columns:
  `grammar, inputLengthBytes, success, errorCount, tokenCount, pathCount,
  ambiguousParseCount, parseTimeP50Micros, parseTimeP95Micros,
  parseTimeP99Micros, parseTimeMinMicros, allocatedBytesP50,
  allocatedBytesP95, allocatedBytesP99, allocatedBytesMin`.

## Environment (2026-09-24 recording)

| Field | Value |
|---|---|
| Runtime | .NET 8.0.31 (`Release` build) |
| OS | Linux x64 |
| Processor count | 1 |
| Corpus | Minotaur-Grammars `main` @ 2026-09-24 |

Timings are machine-dependent and intended for **relative, same-machine**
comparison only; re-measure on your machine with the command below before
comparing prototypes against this file.

## How to reproduce

From a checkout of this repo (with a Minotaur-Grammars checkout):

```bash
MINOTAUR_GRAMMARS_DIR=/path/to/Minotaur-Grammars \
ML_TRACE_OUT=/tmp/ml-baseline \
dotnet test -c Release --filter "FullyQualifiedName~MlBaseline_RecordsCorpus"
```

This writes the raw `ml-baseline/1` JSONL (same numbers, plus per-record
environment fields). The always-on smoke test
`MlBaseline_HarnessMeasuresKnownGrammarParse` covers the
percentile/allocation math in CI without the corpus.

## Notable observations (2026-09-24 snapshot)

- Only ~7% of the 111 (grammar, examples) pairs parse successfully; the
  failure rate is identical in the `ml-trace/1` corpus harness
  (`MlCorpusTraceTests`), i.e. it reflects the current engine/grammar
  state, not the harness. Improving this is out of scope for the baseline
  itself but is a useful signal for the ML work.
- Median parse p50 latency across successful pairs is on the order of
  10 µs on the recording machine; allocations per parse are dominated by
  engine/graph setup (see `allocatedBytesP50` per record).
