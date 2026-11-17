# ml-assist/run — deliberate trigger branch

Any commit pushed to this branch starts the manual ML-assist training
workflow (`.github/workflows/ml-assist-training.yml`, issue #93). It is
the "run now" button: never pushed by automation, never triggered by
regular CI. Results appear on the `ml-assist/trained-artifacts` branch.

This push starts training run for model version 0.2.0.
