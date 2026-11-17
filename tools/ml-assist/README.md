# ML-Assist Trainer

`train.py` trains the ML-assist artifacts offline (issue #93): pure
Python 3 standard library, no network access, fully deterministic
(Newton/IRLS with an L2 ridge, fixed holdout split).

Usage and the full training/shipping workflow:
[docs/ML-Assist.md](../../docs/ML-Assist.md).

The whole loop also runs as a manual-only CI workflow
(`.github/workflows/ml-assist-training.yml`, docs/ML-Assist.md section 5).
