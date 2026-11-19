# Issue #101 measurement: incremental edit latency (lex level) + parse-time table

Measured (UTC): 2026-11-19T09:55:48.2306915+00:00; .NET 8.0.34 on Ubuntu 26.04.1 LTS (X64), 4 cores.
Corpus: committed `Corpus/issue-101/cs-*.txt`, CSharp10 grammar from Minotaur-Grammars@main; traces: committed `traces/*.json` (40 edits each). CI sweep: sizes 100/500/2000, reps full-relex 2 (1 above 500 lines) / incremental 3, Parse() skipped in CI (ISSUE101_MAX_PARSE_LINES=0 - GLR parse of even a 100-line file with the 647-rule CSharp10 grammar does not fit the CI budget; parse table is a local-run item).

## Per-edit lex latency (median across reps, then p50/p95 across the 40-edit trace)

| Lines | Trace | Config | p50 (ms) | p95 (ms) | mean reuse | cold-start x | alloc/edit (B) |
|---|---|---|---|---|---|---|---|
| 100 | char-insert | full-relex | 32.916 | 86.268 | 0.00 | 2.05 | 29921491 |
| 100 | char-insert | incremental | 0.035 | 0.064 | 0.05 | 1.89 | 60428 |
| 100 | word-completion | full-relex | 29.771 | 37.293 | 0.00 | 0.93 | 29921906 |
| 100 | word-completion | incremental | 0.034 | 0.048 | 0.05 | 1.61 | 61050 |
| 100 | line-insert | full-relex | 32.104 | 35.622 | 0.00 | 0.89 | 35427817 |
| 100 | line-insert | incremental | 0.248 | 0.256 | 1.00 | 1.06 | 332522 |
| 100 | block-paste | full-relex | 60.800 | 89.108 | 0.00 | 0.57 | 70410472 |
| 100 | block-paste | incremental | 1.635 | 1.968 | 1.00 | 1.02 | 2100945 |
| 500 | char-insert | full-relex | 117.409 | 124.050 | 0.00 | 1.01 | 137392548 |
| 500 | char-insert | incremental | 0.039 | 0.456 | 0.12 | 1.07 | 191134 |
| 500 | word-completion | full-relex | 117.415 | 120.662 | 0.00 | 1.00 | 137366132 |
| 500 | word-completion | incremental | 0.039 | 0.440 | 0.12 | 1.08 | 190230 |
| 500 | line-insert | full-relex | 105.111 | 122.075 | 0.00 | 1.29 | 123860379 |
| 500 | line-insert | incremental | 0.257 | 0.662 | 0.97 | 3.38 | 334643 |
| 500 | block-paste | full-relex | 154.754 | 184.845 | 0.00 | 0.79 | 177822550 |
| 500 | block-paste | incremental | 1.699 | 2.185 | 1.00 | 1.01 | 2226321 |
| 2000 | char-insert | full-relex | 457.079 | 479.679 | 0.00 | 1.02 | 539056629 |
| 2000 | char-insert | incremental | 0.098 | 0.821 | 0.32 | 1.11 | 791599 |
| 2000 | word-completion | full-relex | 465.091 | 498.271 | 0.00 | 0.98 | 539072439 |
| 2000 | word-completion | incremental | 0.101 | 0.848 | 0.32 | 1.36 | 790657 |
| 2000 | line-insert | full-relex | 276.491 | 476.112 | 0.00 | 4.74 | 324790587 |
| 2000 | line-insert | incremental | 0.321 | 1.066 | 0.97 | 5.53 | 771113 |
| 2000 | block-paste | full-relex | 473.255 | 490.709 | 0.00 | 0.98 | 529059816 |
| 2000 | block-paste | incremental | 0.132 | 2.536 | 0.97 | 1.25 | 1478607 |

## Parse-time table (skipped in this CI sweep; see docs/measurements/issue-101/README.md - local runs only)

| Lines | Tokens | Lex (ms) | Parse (ms) | Success | Errors |
|---|---|---|---|---|---|

## Size scaling (incremental p50 vs lines, log-log slope; 1.0 = linear, O(log n) would be well below 1 and shrinking). Measured log-log slope: **-0.06** (flat/sub-linear at lex level); full-relex p50 scales linearly (33 -> 117 -> 457 ms for 100/500/2000 lines).

- 100 lines: incremental p50 0.141 ms
- 500 lines: incremental p50 0.148 ms
- 2000 lines: incremental p50 0.116 ms

Tree-sitter comparison is not measurable on the .NET CI runner (no tree-sitter binding); that article column remains an external-baseline item.
