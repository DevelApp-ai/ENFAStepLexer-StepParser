# Incremental Parsing: Selective Path Invalidation (SPI)

Task: [issue #101](https://github.com/DevelApp-ai/ENFAStepLexer-StepParser/issues/101) —
parse-level incremental analysis on `RealTimeParserSession`, complementing
the token-level incremental re-lex (issue #144, windowed tail realignment).
SPI is the algorithm the
[incremental-parsing-ides article](https://github.com/DevelApp-ai/Research-article-builder)
presents as the design contribution of the step-based architecture.

## What shipped: SPI v1 (issue #101)

`RealTimeParserSession` grows an opt-in parse-level mode:

```csharp
var session = new RealTimeParserSession(grammarContent);
session.EnableIncrementalParsing = true;   // default: false (token-level only)
session.Initialize(source);                 // cold full parse; snapshot per boundary
session.ApplyEdit(offset, deleteLength, insertText);
var parse = session.LastParse;              // StepParsingResult of the latest parse
```

The four phases of the article's algorithm map to shipped code as follows.

| Phase | Component | File |
|---|---|---|
| 1. Edit impact analysis | `EditImpactAnalyzer` | `EditImpactAnalyzer.cs` |
| 2. Selective invalidation | `ParsePathCache.InvalidateFrom` | `ParsePathCache.cs` |
| 3. Incremental re-parse | `RealTimeParserSession.RunParse` + `StepParser.RestorePaths` | `RealTimeParserSession.cs`, `StepParser.cs` |
| 4. Cache integration | `ParsePathCache.Store` (snapshot per re-stepped boundary) | `ParsePathCache.cs` |

### Why it works

A `ParserPath` is persistent: its parse stack lives on immutable cons
cells shared between clones, and its O(1) stack signature
(`ParserPath.StackSignature`) identifies the GLR state. After every
`StepParser.Step()` the session can therefore snapshot the active path
set cheaply (clone per path, no deep copy) and bind it to the token
boundary just consumed. Because `ApplyEdit`'s token splice guarantees that
tokens below the first affected token are *the same objects*, any
snapshot below the edit remains a valid resume point: restore it, replace
the parser's token stream with the spliced list, and step forward.

An edit at token boundary `f`:

1. **Impact analysis**: the first affected token index `f` becomes the
   impact boundary (tokens `[0, f)` are unchanged).
2. **Selective invalidation**: `ParsePathCache.InvalidateFrom(f)` drops
   exactly the snapshots at or after `f`; earlier ones survive.
3. **Incremental re-parse**: `RestorePaths` installs the nearest surviving
   snapshot (clone-on-restore), and the parser steps from `f` to the end
   of input.
4. **Cache integration**: each re-stepped boundary contributes a fresh
   snapshot, so the cache stays warm for the next edit.

Cold start: an edit that invalidates the entire cache (e.g. at token 0)
falls back to a full parse — that parse itself repopulates the cache.

### Instrumentation

- `LastParse` — the `StepParsingResult` of the latest parse pass.
- `LastParseStepCount` — parser steps of the latest pass (compare with
  the token count).
- `LastParseResumeBoundary` — 0 for cold parses, otherwise the restored
  boundary (a cache hit).
- `LastParseUsedCache` — the parse-level cache-hit signal.
- `ParseCacheBoundaryCount` — snapshots currently retained.

The cache keeps **every** boundary up to 256 snapshots, then decimates
its oldest half adaptively (recent history dense, distant history
sparse: a bounded, position-proportional resume granularity). Snapshot
cost is O(active paths): clones share stack spines.

## Measured effect (this machine, .NET 8, Release)

Ambiguous expression chains over `SimpleExpr.grammar`
(`a + 12 + b + …`, 2n+1 tokens), median single measurement per cell;
reproducible via `IncrementalParsingBenchmarks`:

| Terms (tokens) | Edit pos. | Step ratio (cold/warm) | Warm-vs-cold wall mean ratio* | Edit types |
|---|---|---|---|---|
| 25 (51)   | 50% | 1.68–2.08 | warm 0.9–1.5x faster | char, word, line, block |
| 100 (201) | 50% | 1.91–2.00 | warm 1.3–2.4x faster  | char, word, line, block |
| 400 (801) | 50% | 1.91–2.00 | warm 1.2–1.6x faster  | char, word, line, block |
| 400 (801) | 90% | **8.9–9.9** | warm 2.7–5.3x faster   | char, word, line, block |

\* ratio of the reported values; captured from a single best-effort run
on the development container, not from fixed, dedicated hardware, and
all four edit types were measured per row (char insert, word completion,
line insertion, block paste). Numbers change per machine; the *step*
ratio is position-driven and machine-independent:
resumed stepping covers the suffix, so the step work is roughly
`1 - p` of the cold parse for an edit at relative position `p`, and the
wall-clock advantage grows with the preserved prefix's GLR cost. Colder
edits at the very start of a document cold-start (ratio 1.0).

The step ratio being approximately `1/(1-p)` is exactly what the
checkpoint design predicts: resume below the edit, re-step to EOF.

## Honest limitations (scoped for v1 → v2)

1. **Suffix re-stepping (the O(suffix) residue).** Byte positions are
   baked immutably into CognitiveGraph node records at reduction time
   (`CognitiveGraphBuilder` is append-only), so parse state adopted from
   a cached suffix would carry pre-edit positions. SPI v1 therefore steps
   the unchanged suffix to produce position-correct nodes. Sub-linear
   (O(edit)) suffix adoption requires either position-rebasing support in
   the graph builder, or a store with node rewriting. Until then the
   honest complexity of the *warm* path is O(suffix) parser steps — the
   win is the skipped prefix (typically the majority of a document for
   typing-position edits), the preserved parse state, and the resulting
   whole-pipeline latency measured above. No O(log n) claim is made.
2. **Semantic action & graph-store side effects replay** for the region
   from the resume boundary to EOF: reductions re-run, so registered
   action handlers observe suffix work again and the append-only node
   store grows per edit (as it already does across consecutive
   `StepParserEngine.Parse` calls on one engine). Fine for diagnostic /
   graph-output session use (build reads only the reachable subgraph);
   long-lived editor sessions will want a store compaction story (v2).
3. **Deep single-expression documents** (1600+ terms on this grammar)
   exhaust the cold full parse path today — pre-existing behavior,
   reproduced with `StepParserEngine.Parse` alone. Largest benchmark
   parameter is 400 terms.

## What the article can claim once measured on fixed hardware

With v1 shipped, per-edit latency of the warm path and the resume
boundary behavior are measurable as functions of edit position and edit
type (the committed `IncrementalParsingBenchmarks` produces the tables).
The previously projected "85–95% reduction" and O(log n) complexity are
*positional* claims for the parse phase that this design makes concrete
and bounded (`1/(1-p)` steps), and must be reported from fixed-hardware
repetitions of the committed benchmark, not from projections.

## Measured: dev-container results (this section supersedes the projections above)

The fixed-hardware projections above are now grounded by an actual
measurement campaign. The committed
`src/DevelApp.IncrementalBench` harness (per-edit warm/full/lex arms,
trace guards, cold-start arm, real-C# corpus arm — see
`tools/incremental-bench/README.md`)
was run on real repo files at 100/500/2000/5000 lines, three repetitions,
four edit types, three edit positions. Raw CSVs, environment JSON, and
the generated report live in
`docs/measurements/incremental-parsing-ides/results/dev-container/`;
the campaign README is `docs/measurements/incremental-parsing-ides/README.md`.

Headline dev-container findings (1 vCPU, 1 GB cgroup, .NET 8, Release):

1. **SPI beats full reparse at every measured size and edit type** with
   zero mismatched edits (per-edit success/token/resolved equality guard,
   108 per-edit runs + trace arm, 0 MISMATCH). Examples (p50):
   2000-line CharDelete 94 ms vs 111 ms, 2000-line Word 149 ms vs
   178 ms, 5000-line CharInsert 174 ms vs 298 ms.
2. **Lexing reuse is near-perfect**: the lex-only arm costs 0.2–4.7 ms
   across 100–5000 lines (fit exponent 0.76); the lexer reuses
   98.7–99.97% of tokens on warm edits.
3. **Cache collapse is real and is now quantified**: at every size, the
   SPI v1 decimation policy (`DecimateOldestHalf` retains the boundary-1
   path) yields resumeBoundary=1 even for edits at 10% of the document,
   so warm-path parser work grows with document size. Fitted per-edit
   complexity exponent is 0.89 for both SPI and full-parse arms at
   scale, versus 0.76 for pure re-lexing. A cache-policy v2 (resume
   ladder keeping cohort checkpoints at multiple depths instead of only
   the last-resort boundary) is the direct fix.
4. **Per-edit allocation is heavy** (~309 MB per SPI edit at 5000 lines)
   — a GC pressure reduction target comparable to the latency target.
5. **Engine-side findings (fixed for
   [#158](https://github.com/DevelApp-ai/ENFAStepLexer-StepParser/issues/158),
   see the issue for the fix PR):**
   a shared `StepParserEngine` used to retain per-parse data across
   calls (an append-only graph builder held for the parser's lifetime,
   plus parse-context symbol-table entries that were appended and never
   reset), causing ~30–70 MB growth per 1000-line parse, doubling
   per-parse wall time over a sequence of parses, and eventual wrong
   `ParseOutcome` under memory pressure; warm SPI session latency grows
   across a session (~90→290 ms over 20 edits at 1000 lines) until the
   engine is recreated. `StepParser.Initialize` now resets the parse
   context and recreates the graph builder, so a long-lived engine stays
   flat (measured: 27 MB retained across 20 sequenced 1000-line parses,
   ~950 ms per parse, both flat; before: 27→334 MB and 968→1896 ms).
   The bench cold-start arm keeps its fresh-engine-per-full-parse-edit
   setup so the two arms stay comparable, but it is now a comparability
   choice rather than a correctness workaround.

The two-phase-regex article can therefore claim, with committed data:
measured per-edit latency curves per size/type, measured reuse rates,
measured fit exponents, and the cache-collapse defect itself as a
bounded, fixable v1 policy issue (the resume-boundary math in this
document remains the design ceiling once v2 restores multi-depth
checkpoints).

**The remaining fixed-hardware step** for the article: run
`DevelApp.IncrementalBench --scenario all --sizes 100,500,2000,5000
--reps 10 --seed 101 --coldstart-size 1000` and the python
`tools/incremental-bench/treesitter_arm.py` on the target
fixed-hardware box (not in a container), then regenerate the report with
`tools/incremental-bench/summarize.py`. The dev-container numbers are
committed as sanity baseline, not as publication figures.
