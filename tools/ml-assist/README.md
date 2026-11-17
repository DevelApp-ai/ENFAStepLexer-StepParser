# ML-Assist Trainer

`train.py` trains the ML-assist artifacts offline (issue #93): purd
Python 3 standard library, no network access, fully deterministic
(Newton/IRLS with an L2 ridge, fixed holdout split).

Usage and the full training/shipping workflow:
[docs/ML-Assist.md](../../docs/ML-Assist.md).

Subcommands:

- `rule` / `path` â train the two artifacts from the label traces
- `summarize-eval` â JSON summary of the `MlAssistEvaluationTests` CSV
- `report-eval` â write the #93 measurement report (markdown); exit codes:
  `0` = zero semantic changes and end-to-end improvement, `1` = semantic
  divergence, `2` = no improvement
