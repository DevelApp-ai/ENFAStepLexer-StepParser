# ML-Assist Artifacts

Versioned training artifacts for the ML-assist prototypes, consumed at
runtime through `LearnedRulePrioritizer` and `LearnedPathPruner`.
Full workflow: [ML-Assist.md](../ML-Assist.md).

- `rule-prioritizer.json` â trained rule prioritizer (issue #74)
- `path-pruner.json` â trained path pruner (issue #75)

Produced by `tools/ml-assist/train.py` from the label traces captured by
`MlLabelTraceTests`; consumed by `MlAssistEvaluationTests` (evaluation)
and by applications. Training is deterministic: the same traces and the
same trainer version produce identical weights.

Artifacts are committed by the ML-Assist training workflow
([ML-Assist.md Â§5](../ML-Assist.md), issue #93) together with the
measurement report in `docs/ml-assist/reports/`. To retrain after a
corpus refresh, run the workflow (`workflow_dispatch`) or push to a
`feature/ml-assist*` branch, then open a PR with the result.
