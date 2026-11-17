---
layout: default
title: ML-Assist Training and Shipping Workflow
---

# ML-Assist Training and Shipping Workflow

How the ML-assist prototypes (learned rule prioritization, #74; learned GLR
path pruning, #75 — plan #58, tracked by #105) are trained offline,
versioned, shipped and measured end-to-end. This is the #92 deliverable:
the workflow is documented so it is reproducible without tribal knowledge.
Runtime behavior and the guard-rail contract live in
[StepParser.md](StepParser.md).

## Artifacts

| Artifact | Runtime class | Gate | Features | Label |
|---|---|---|---|---|
| `rule-prioritizer.json` | `LearnedRulePrioritizer` (DevelApp.StepLexer) | `MlAssistFeature.LearnedRulePrioritization` | 10 | matched at the position |
| `path-pruner.json` | `LearnedPathPruner` (DevelApp.StepParser) | `MlAssistFeature.LearnedPathPruning` | 6 | doomed (= !succeeded) |

Both gates are OFF by default (the deterministic, bit-identical behavior is
what ships), `DEVELAPP_STEPML_DISABLE_ALL=1` forces everything off at
runtime, and `StepParserEngine.MlAssistStatus` reports the active model
versions for every ML-assisted run.

## 1. Capture training labels (issue #93)

You need a checkout of the grammar repository (`MINOTAUR_GRAMMARS_DIR`).
All harnesses are env-gated and skip by default, CI included. For a
one-shot CI run of the whole pipeline, see §5 (manual only).

```bash
MINOTAUR_GRAMMARS_DIR=/path/to/Minotaur-Grammars \
ML_TRACE_OUT=/tmp/ml-labels dotnet test --filter MlLabel
```

Outputs (one JSON object per line):

- `ml-rule-labels.jsonl` — schema `ml-label-rule/1`: one record per
  lexer rule-evaluation attempt: `grammarName`, `features` (the ten
  prioritizer features) and `matched`.
- `ml-path-labels.jsonl` — schema `ml-label-path/1`: one record per
  (parser step, path) while the live path count exceeds the deterministic
  budget: `grammarName`, `step`, `pathId`, `features` (the six
  pruner features) and `succeeded` (whether that path ultimately
  completed the parse).

(The aggregate `ml-trace/1` corpus traces of the #73 baseline are captured
by `MlCorpusTraceTests`: `dotnet test --filter MlCorpusTrace`.)

## 2. Train the models

`tools/ml-assist/train.py` is pure Python 3 standard library — no
third-party packages, no network access, fully deterministic (Newton/IRLS
with a small L2 ridge, fixed 1-in-5 holdout; metrics: log-loss, accuracy,
AUC):

```bash
python3 tools/ml-assist/train.py rule /tmp/ml-labels/ml-rule-labels.jsonl \
  --version 0.2.0 --out docs/ml-assist/artifacts/rule-prioritizer.json
python3 tools/ml-assist/train.py path /tmp/ml-labels/ml-path-labels.jsonl \
  --version 0.2.0 --out docs/ml-assist/artifacts/path-pruner.json
```

Feature order and count are fixed by the C# `ExtractFeatures` definitions
and validated by the trainer. The path model trains the doom label
(`doomed = !succeeded`) to match
`LearnedPathPruner.PredictDoomProbability` semantics.

Artifact format (schema `ml-assist-artifact/1`): `model`,
`modelVersion`, `featureCount`, `featureNames`, `weights` (rule)
plus `confidenceThreshold` and `minPathCount` (path), `trainedOn`,
`metrics` (holdout) and `trainer`. See
[docs/ml-assist/README.md](ml-assist/README.md).

Versioning policy (versions are observable via `MlAssistStatus`):

- patch: retrained on the same corpus and features
- minor: new corpus snapshot, retuned thresholds
- major: breaking change to the feature/label contract

## 3. Ship and load

```csharp
using DevelApp.StepLexer;
using DevelApp.StepParser;

// weights loaded from docs/ml-assist/artifacts/*.json (System.Text.Json)
var prioritizer = new LearnedRulePrioritizer("0.2.0", ruleWeights);
var pruner = new LearnedPathPruner("0.2.0", confidenceThreshold: 0.9,
    minPathCount: 10, weights: prunerWeights);

using var engine = new StepParserEngine();
engine.RulePrioritizer = prioritizer;   // orders rule evaluation only
engine.PathPruner = pruner;             // prunes only confident doomed paths

MlAssistOptions.Enable(MlAssistFeature.LearnedRulePrioritization, "0.2.0");
MlAssistOptions.Enable(MlAssistFeature.LearnedPathPruning, "0.2.0");

Console.WriteLine(engine.MlAssistStatus);
```

Guard rails (issue #77, enforced by `MlGuardRailTests`): features default
off; the prioritizer only re-orders evaluation (the token stream stays
bit-identical); the pruner only acts above `minPathCount` and at
`confidenceThreshold` with full-GLR fallback; `DEVELAPP_STEPML_DISABLE_ALL`
disables everything at runtime without a rebuild.

## 4. Measure end-to-end (issue #93 definition of done)

```bash
MINOTAUR_GRAMMARS_DIR=/path/to/Minotaur-Grammars \
ML_EVAL_OUT=/tmp/ml-eval dotnet test -c Release \
  --filter "FullyQualifiedName~MlAssistEvaluation"
```

`MlAssistEvaluationTests` parses every corpus example twice — ML off (the
shipped default) and ML on (the committed artifacts) — and writes
`ml-assist-evaluation.csv` (per (grammar, example): success in both arms,
best parse time, wasted-attempt ratio, paths pruned, consultations). The
harness FAILS if ML changes any parse success: the zero-semantic-change
contract is asserted, not assumed.

Summarize:

```bash
python3 tools/ml-assist/train.py summarize-eval /tmp/ml-eval/ml-assist-evaluation.csv
```

Acceptance (definition of done for #58): a measurable end-to-end improvement
(parse time and/or wasted-attempt ratio) against the pre-ML baseline
([ml-baseline](ml-baseline/README.md), #73) with zero semantic changes and
no allocation regressions (guarded by `MlGuardRailTests`).

## 5. Run the whole pipeline in CI (manual only)

Every step above is wired into CI as a single workflow,
`.github/workflows/ml-assist-train.yml` ("ML-Assist Train Pipeline") -
but as a **`workflow_dispatch` workflow only, with no push,
pull_request or schedule triggers**. The full-corpus label capture,
training and end-to-end evaluation take ~21+ minutes of runner time, so
the pipeline is deliberately **never run automatically**: start it from
the Actions tab ("Run workflow") when retraining is intended.

Inputs: the model version (default `0.2.0`) and the Minotaur-Grammars
ref (default `main`). The run captures the labels (failing loudly if the
env gates produced no records), trains both models into
`docs/ml-assist/artifacts/`, asserts zero semantic change with the
evaluation harness, writes the holdout metrics and the end-to-end
measurement into the job summary, and uploads the models, the label
files and `ml-assist-evaluation.csv` as workflow artifacts. It never
commits: review the job summary, then ship the trained artifacts in a
follow-up PR (bump the model version per the policy above).

## Approaches 3-4 (strategy selection, token-reuse prediction)

Evaluated NO-GO with recorded rationale and re-evaluation criteria:
[ML-Assist-Evaluation-Approaches-3-4.md](ML-Assist-Evaluation-Approaches-3-4.md).
