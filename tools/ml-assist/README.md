# ML-Assist Trainer

Offline tooling for the ML-assist artifacts (issue #93): pure Python 3
standard library, no network access, fully deterministic
(Newton/IRLS with an L2 ridge, fixed holdout split).

- `train-pipeline.sh` - one-shot local pipeline: corpus label capture,
  `train.py rule`/`path`, `MlAssistEvaluation` evaluation and the #93
  measurement report, gated by the acceptance exit codes. Run it on
  your own machine against a local Minotaur-Grammars checkout (Git
  Bash on Windows or any POSIX shell). It replaced the former
  `.github/workflows/ml-assist-training.yml`, removed because a full
  run takes hours and must never be triggered automatically.
- `train.py` - the trainer itself. Subcommands:
  - `rule` / `path` - train the two artifacts from the label traces
  - `summarize-eval` - JSON summary of the `MlAssistEvaluationTests` CSV
  - `report-eval` - write the #93 measurement report (markdown); exit
    codes: `0` = zero semantic changes and end-to-end improvement,
    `1` = semantic divergence, `2` = no improvement

Usage and the full training/shipping workflow:
[docs/ML-Assist.md](../../docs/ML-Assist.md).
