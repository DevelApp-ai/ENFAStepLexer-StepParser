# ML-Assist Evaluation: Approaches 3 and 4 (issue #76)

Sub-issue of #58 (plan item 5, candidate approaches 3 and 4). This document
is the written evaluation of the two remaining ML-assisted optimization
candidates for IDE-facing wins, with a go / no-go recommendation and rough
projected gains for each. The evaluation harness that produced the
supporting measurements lives in
`src/DevelApp.StepParser.Tests/MlApproaches34EvaluationTests.cs`
(env-gated corpus run + always-on unit tests).

Constraints (from #58) apply to both: ML may only influence ordering,
pruning and strategy decisions — never token boundaries, parse trees or
emitted diagnostics; models are trained offline and shipped as small,
deterministic artifacts; the zero-copy allocation profile must not regress.

---

## Approach 3: Hotspot-guided strategy selection

**Mechanism.** Use `CognitiveGraphAnalytics` metrics
(`ComplexityScore`, hotspots, `AverageFanout`/`MaxFanout`,
`AmbiguityRate`, `NodesPerKb`) as model features to detect pathological
grammars/inputs and switch parsing strategy (more aggressive pruning or
batching) *before* slowdowns occur.

**Findings.**

1. *The features are post-hoc by construction.* `GraphAnalyticsReport` is
   computed by `CognitiveGraphAnalytics.Analyze(graph)` **after** a parse
   produced its CognitiveGraph. At the moment the strategy decision is
   needed (before/at parse start), none of these features exist yet. The
   model could only transfer hotspot knowledge from *previous* parses of
   the same grammar to *later* parses — a per-grammar, warm-cache strategy
   hint, not an up-front detector.
2. *The signal is largely redundant with static grammar features.* The
   `ml-trace/1` schema (PR #64) already records static grammar features
   (token rule count, production rule count, average RHS length, import
   count) plus parse observables (`PathCount`, `AmbiguousParses`,
   `ParseTime`). Complexity correlates with grammar shape, which is known
   at grammar-load time — before the first parse, no ML needed.
3. *The strategy switch itself does not exist yet.* The engine currently
   has one deterministic strategy (fixed path budget of 10, fixed pruning
   comparator). Approach 2 (#75) adds a learned pruner behind a flag; that
   is the only knob a "strategy selection" could turn today, and its
   confidence threshold already provides the safety valve the strategy
   switch would want.

**Rough projected gain.** Small: quadratic blowup is concentrated in the
handful of ambiguous grammars of the corpus (see the
`MlApproaches34EvaluationTests` corpus CSV: `pathCount`/`ambiguousParses`
vs. `parseTimeMicros`). Skipping doomed work earlier via #75 addresses the
same cost center with an observable, per-decision model. A separate
grammar-level model would at best duplicate that signal.

**Recommendation: NO-GO** for the ML prototype in its current form. Revisit
as a *deterministic* (non-ML) heuristic once #74/#75 measurements exist:
`pathCount`/`ambiguousParses` from the previous parse of a grammar can
escalate the #75 pruning threshold on the next parse — a two-line strategy
switch with no model artifact, no training pipeline, and full observability.
Split out a sub-issue only if corpus data shows warm-grammar escalation
would have caught blowups that per-path pruning missed.

---

## Approach 4: Learned token-reuse prediction for `RealTimeParserSession`

**Mechanism.** Predict the re-lexing blast radius of an edit to maximize
incremental token reuse in IDE scenarios.

**Findings.**

1. *The deterministic baseline is already strong.* `RealTimeParserSession`
   re-lexes only from the first affected token and aligns the unchanged
   tail from the end (`MergeWithTail`), keeping token object identity for
   unchanged text. New instrumentation (`LastReusedTokenCount`,
   `LastReuseRatio`) measures the actual reuse per edit: for edits in the
   middle of long files, the aligned suffix reuse is near-total because
   alignment is anchored at the (unshifted) end of text (measured: ratio
   1.00 for a mid-file single-character edit in a 400-token document; see
   `MlApproaches34EvaluationTests`).
2. *The expensive part is not the tail.* What a model would have to predict
   is how far *forward* the blast radius reaches (e.g. an edit inside a
   string/comment/block construct can change tokenization arbitrarily far
   ahead). But the current implementation already re-lexes forward to the
   end of text and then discards the prefix down to the alignment anchor —
   a linear scan whose cost is bounded by the affected region length, not
   by total file size. The remaining waste is one re-lex over the region
   between the edit and the alignment anchor, which only exists when
   tokenization actually changed ahead of the edit.
3. *No IDE workload traces exist.* There is no recorded corpus of
   (grammar, edit sequence, reuse outcome) data to train on, and the win
   depends entirely on the edit distribution of real editors (typing vs.
   restructuring), which we do not have.

**Rough projected gain.** Speculative: bounded by the cost of the redundant
forward re-lex between edit and anchor, which is zero for plain typing
(the anchor is the token right after the edit) and only nonzero for
construct-crossing edits. Without workload traces the upside cannot be
bounded below the cost of carrying, validating and shipping a model.

**Recommendation: NO-GO for now; gather data first.** The added
instrumentation (`LastReuseRatio` per edit, plus the corpus CSV from the
evaluation harness) makes it cheap to record real reuse ratios once an IDE
integration exists. If those records show a meaningful fraction of edits
with low reuse ratio and long re-lex ranges, split out a prototype sub-issue
(for example: a tiny classifier over (edit offset, preceding token type,
grammar construct) predicting the forward re-lex stop position).

---

## Summary

| Approach | Recommendation | Reason |
|---|---|---|
| 3 — hotspot-guided strategy selection | NO-GO as ML; revisit as deterministic heuristic | features are post-parse and redundant with static grammar shape; no strategy switch exists beyond #75 |
| 4 — learned token-reuse prediction | NO-GO for now; instrument first | deterministic tail reuse already near-total for common edits; no workload traces to justify a model |

Both recommendations keep the issue #58 definition of progress on track:
the measurable end-to-end win is expected from #74 (learned ordering) and
#75 (learned pruning), which are behind flags and observable in
diagnostics. The instrumentation added for this evaluation
(`LastReuseRatio`, corpus evaluation CSV harness) is the concrete artifact
that makes a later data-driven re-evaluation of approaches 3–4 possible.
