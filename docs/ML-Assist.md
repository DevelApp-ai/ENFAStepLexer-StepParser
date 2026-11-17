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
All harnesses are env-gated and skip by default, CI included.

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

The rule-label capture is bounded: at most 20,000 rule labels are kept per
(grammar, example) parse — a uniform, deterministic reservoir sample of all
rule-evaluation attempts (`RuleMatchLabelCapture.Begin(int)`; ambiguous
grammars generate millions of attempts per example). The bounded sample
keeps corpus-scale captures practical in memory and time without changing
what a linear model learns from them; the path-label capture is bounded the same way (at most 20,000 path
labels per (grammar, example) parse via `PathLabelCapture.Begin(int)`):
over-budget regimes of ambiguous grammars generate millions of (parser
step, path) observations per parse, and the bounded, uniform, deterministic
sample keeps corpus-scale captures bounded in memory and time

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
and validated by the trainer. The trainer streams the label files and
aggregates identical feature vectors into counts before fitting, so
corpus-scale label sets train in a few MB of memory regardless of how many
records were captured. The path model trains the doom label
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

// weights loded from docs/ml-assist/artifacts/*.json (System.Text.Json)
var prioritizer = new LearnedRulePrioritizer("0.2.0", ruleWeights);
var pruner = new LearnedPathPruner("0.2.0", confidenceThreshold: 0.9,
    minPathCount: 10, weights: prunerWeights);

using var engine = new StepParserEngine();
engine.RulePrioritizer = prioritizer;   // orders rule evaluation only
engine.PathPruner = pruner;              // prunes only confident doomed paths

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

## 5. Training in CI (issue #93, manual only)

All of the above can be run as a single CI job on demand via
[.github/workflows/ml-assist-training.yml](../.github/workflows/ml-assist-training.yml):

- **Trigger**: `workflow_dispatch` from the Actions tab only — retrain
  on demand (e.g. after a corpus refresh); uncheck *commit_results* to
  only upload workflow artifacts. The workflow is deliberately **never
  automatic**: a full run spends 21+ minutes (in practice 120+) in
  corpus label capture alone, so it has no `push`, `pull_request` or
  `schedule` triggers. The job timeout is 300 minutes because the run
  is manual; cancel any dispatch that turns out to be pathologically
  slow.
- **Steps**: corpus checkout (Minotaur-Grammars `main`) → label capture
  (`--filter MlLabel`) → `train.py rule` / `train.py path` → evaluation
  (`--filter MlAssistEvaluation`, fails on any semantic divergence) →
  `train.py report-eval` → commit → upload.
- **Measurement report**: `train.py report-eval` writes
  `docs/ml-assist/reports/measurement-report-v<version>.md` — per-arm
  totals, per-grammar deltas, holdout model metrics, and the acceptance
  verdict. Exit codes: `0` = zero semantic changes **and** end-to-end
  improvement; `1` = semantic divergence (contract breach); `2` = no
  improvement. The workflow's acceptance gate fails the run on `1`/`2`, so
  a PR can only merge with recorded evidence of a real win.

Commits made by the workflow use the `GITHUB_TOKEN`, which never
re-triggers workflows, so a dispatched run commits its results at most
once.

## Approaches 3-4 (strategy selection, token-reuse prediction)

Evaluated NO-GO with recorded rationale and re-evaluation criteria:
[ML-Assist-Evaluation-Approaches-3-4.md](ML-Assist-Evaluation-Approaches-3-4.md).
