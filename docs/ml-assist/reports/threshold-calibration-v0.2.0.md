---
layout: default
title: Path Pruner Threshold Calibration v0.2.0
---

# Path pruner confidence-threshold calibration - v0.2.0 (issues #93 / #58)

Recorded: 2026-11-18 (UTC), issue-specific probe over the same corpus slice
as the measurement report plus the deterministic ambiguous-chain regime.
Hardware: dev container, single-threaded, .NET 8 Release; single runs per
arm (timing is indicative, the divergence counts are deterministic).

Model: `path-pruner.json` v0.2.0 (holdout accuracy 0.892, AUC 0.692,
trained on 5,699 `ml-label-path/2` records of which 83% are the synthetic
`AmbiguousChain` augmentation — the parse-healthy corpus slice itself
yields doomed explorations only, so the model's success-class knowledge
comes almost entirely from the synthetic chains).

## Corpus slice (41 grammars, parse-healthy, Minotaur-Grammars main)

| ConfidenceThreshold | Semantic divergences | Paths pruned (corpus) |
|---|---|---|
| 0.90  | 1 (`Norwegian_Nynorsk.grammar`) | 239 |
| 0.95  | 0 | 14 |
| 0.98  | 0 | 0 |
| 0.99  | 0 | 0 |
| 0.999 | 0 | 0 |

The single divergence at 0.90 is a rescue, not a break: `Norwegian_Nynorsk`
**fails** in the baseline arm (the deterministic path budget caps out) and
**succeeds** in the ML arm (14 pruned paths make room for the survivor).
The zero-semantic-change contract of issue #58 is strict equality in both
directions, so this still counts as a divergence and disqualifies 0.90:
shipped behavior must not change, and a parse that flips could equally
flip the other way on an unseen grammar at a slightly different threshold.

## Ambiguous-chain regime (AmbiguousExpr, deterministic sums)

| ConfidenceThreshold | terms=50 | terms=100 | terms=200 |
|---|---|---|---|
| off (no pruner) | 3.7 ms | 6.0 ms | 11.8 ms |
| 0.90  | 1.2 ms (330 pruned) | 1.4 ms (703) | 2.7 ms (1442) |
| 0.95  | 1.5 ms (43 pruned)  | 5.6 ms (93)  | 10.5 ms (193) |
| 0.98  | 2.7 ms (1 pruned)   | 5.7 ms (6)   | 12.4 ms (17) |
| 0.99  | 3.1 ms (0)          | 6.5 ms (0)   | 12.8 ms (0) |
| 0.999 | 3.5 ms (0)          | 6.3 ms (0)   | 12.8 ms (0) |

All chain parses keep `Success == True` at every threshold measured.
Relative raw-time wins at 0.95: terms=50 2.2x (3.3 -> 1.5 ms),
terms=100 1.3x (7.4 -> 5.6 ms), terms=200 1.5x (15.4 -> 10.5 ms);
0.90 delivers more (1.4x-3.2x-4.4x) but fails the corpus contract;
at 0.99+ the pruner is inert everywhere and pure consultation overhead.

## Verdict

Stamps `confidenceThreshold: 0.95` in the v0.2.0 path artifact:

- zero measured semantic divergence on the corpus slice,
- near-zero corpus-side effect (14 prunes, no flips),
- 1.3x-2.2x raw parse-time win on 50–200-term ambiguous chains,
- 0.90 fails the contract, 0.99+ forfeits the win entirely.

Re-calibrate (via `train.py path --confidence-threshold`) whenever the
training mix changes: with real-grammar success labels still missing from
the corpus slice, the boundary is measured, not derived.
