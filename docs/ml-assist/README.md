# ML-Assist Artifacts

Versioned training artifacts for the ML-assist prototypes, consumed at
runtime through `LearnedRulePrioritizer` and `LearnedPathPruner`.
Full workflow: [ML-Assist.md](../ML-Assist.md).

- `rule-prioritizer.json` — trained rule prioritizer (issue #74)
- `path-pruner.json` — trained path pruner (issue #75)

Produced by `tools/ml-assist/train.py` from the label traces captured by
`MlLabelTraceTests`; consumed by `MlAssistEvaluationTests` (evaluation)
and by applications. Training is deterministic: the same traces and the
same trainer version produce identical weights.

No artifacts are committed yet: they land with the first corpus training
run (issue #93).
