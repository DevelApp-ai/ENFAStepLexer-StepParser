#!/bin/bash

# ML-Assist training pipeline (issue #93, sub-issue of #58): the local
# replacement for the former .github/workflows/ml-assist-training.yml.
# Runs the whole sequence in one command on your own machines:
#
#   1. capture training labels from the Minotaur-Grammars corpus
#   2. train the #74 rule-prioritizer and #75 path-pruner artifacts
#   3. evaluate zero-semantic-change + end-to-end improvement
#   4. write the measurement report and apply the acceptance gate
#
# It is deliberately NOT wired into CI: a full run spends 21+ minutes
# (in practice 120+) in corpus label capture alone, far too slow for any
# automatic trigger. Run it by hand when retraining is intended.
#
# Usage (from the repository root):
#   tools/ml-assist/train-pipeline.sh -g /path/to/Minotaur-Grammars
#
# Options:
#   -g DIR    grammar repository checkout (or set MINOTAUR_GRAMMARS_DIR)
#   -v VER    model version stamped on the artifacts (default: 0.2.0)
#   -w DIR    work directory for labels/eval outputs (default: fresh temp dir)
#   -a DIR    artifacts output directory (default: docs/ml-assist/artifacts)
#   -r DIR    reports output directory (default: docs/ml-assist/reports)
#   -c CONFIG dotnet configuration (default: Release)
#   -s        skip restore + build (reuse an existing build)
#
# Windows: run under Git Bash ("bash tools/ml-assist/train-pipeline.sh ...");
# paths are converted for the dotnet test host automatically.
#
# Acceptance gate exit codes (from train.py report-eval):
#   0 = zero semantic changes AND end-to-end improvement
#   1 = semantic divergence (contract breach)
#   2 = no end-to-end improvement

set -euo pipefail

GRAMMARS_DIR="${MINOTAUR_GRAMMARS_DIR:-}"
MODEL_VERSION="0.2.0"
WORK_DIR=""
ARTIFACTS_DIR="docs/ml-assist/artifacts"
REPORTS_DIR="docs/ml-assist/reports"
CONFIGURATION="Release"
SKIP_BUILD="no"

while getopts "g:v:w:a:r:c:sh" opt; do
  case "$opt" in
    g) GRAMMARS_DIR="$OPTARG" ;;
    v) MODEL_VERSION="$OPTARG" ;;
    w) WORK_DIR="$OPTARG" ;;
    a) ARTIFACTS_DIR="$OPTARG" ;;
    r) REPORTS_DIR="$OPTARG" ;;
    c) CONFIGURATION="$OPTARG" ;;
    s) SKIP_BUILD="yes" ;;
    h) grep -E '^#( |$)' "$0" | sed 's/^#\{1,2\} \{0,1\}//'; exit 0 ;;
    *) echo "Unknown option (see -h)" >&2; exit 2 ;;
  esac
done

fail() { echo "train-pipeline: ERROR: $*" >&2; exit 1; }
note() { echo; echo "==> $*"; }

# --- dependency + argument checks (fail loudly, never silently skip) ------
command -v dotnet >/dev/null 2>&1 || fail "dotnet not found in PATH"
PYTHON=""
for candidate in python3 python; do
  if command -v "$candidate" >/dev/null 2>&1; then PYTHON="$candidate"; break; fi
done
[ -n "$PYTHON" ] || fail "python not found in PATH (train.py is pure stdlib, any Python 3 works)"

[ -n "$GRAMMARS_DIR" ] || fail "grammar repository not set (use -g /path/to/Minotaur-Grammars or MINOTAUR_GRAMMARS_DIR)"
[ -d "$GRAMMARS_DIR" ] || fail "grammar repository not found: $GRAMMARS_DIR"
[ -f "tools/ml-assist/train.py" ] || fail "run this from the repository root (tools/ml-assist/train.py not found)"

if [ -z "$WORK_DIR" ]; then
  WORK_DIR="$(mktemp -d "${TMPDIR:-/tmp}/ml-assist-pipeline.XXXXXX")"
fi
LABELS_DIR="$WORK_DIR/ml-labels"
EVAL_DIR="$WORK_DIR/ml-eval"
mkdir -p "$LABELS_DIR" "$EVAL_DIR" "$ARTIFACTS_DIR" "$REPORTS_DIR"

# Native-path conversion for env vars consumed by the dotnet test host
# (no-op outside Git Bash / MSYS on Windows).
to_native() {
  if command -v cygpath >/dev/null 2>&1; then cygpath -w "$1"; else printf '%s' "$1"; fi
}
GRAMMARS_NATIVE="$(to_native "$(cd "$GRAMMARS_DIR" && pwd)")"
LABELS_NATIVE="$(to_native "$(cd "$LABELS_DIR" && pwd)")"
EVAL_NATIVE="$(to_native "$(cd "$EVAL_DIR" && pwd)")"

CORPUS_REF="main"
if git -C "$GRAMMARS_DIR" rev-parse --short HEAD >/dev/null 2>&1; then
  CORPUS_REF="main@$(git -C "$GRAMMARS_DIR" rev-parse --short HEAD)"
fi

echo "ML-Assist training pipeline"
echo "  corpus:        $GRAMMARS_DIR ($CORPUS_REF)"
echo "  model version: $MODEL_VERSION"
echo "  work dir:      $WORK_DIR"
echo "  artifacts ->   $ARTIFACTS_DIR"
echo "  reports   ->   $REPORTS_DIR"

# --- 1. build --------------------------------------------------------------
if [ "$SKIP_BUILD" != "yes" ]; then
  note "Restore + build ($CONFIGURATION)"
  dotnet restore
  dotnet build --configuration "$CONFIGURATION" --no-restore
fi

# --- 2. capture training labels --------------------------------------------
note "Capture training labels (MlLabel corpus traces)"
MINOTAUR_GRAMMARS_DIR="$GRAMMARS_NATIVE" ML_TRACE_OUT="$LABELS_NATIVE" \
  dotnet test src/DevelApp.StepParser.Tests/DevelApp.StepParser.Tests.csproj \
  --configuration "$CONFIGURATION" --no-build \
  --filter "FullyQualifiedName~MlLabel"

note "Validate label capture"
for f in ml-rule-labels.jsonl ml-path-labels.jsonl; do
  if [ ! -s "$LABELS_DIR/$f" ]; then
    fail "$LABELS_DIR/$f is missing or empty - label capture produced no records"
  fi
  echo "  $f: $(wc -l < "$LABELS_DIR/$f") records"
done

# --- 3. train both models --------------------------------------------------
note "Train rule prioritizer (#74)"
"$PYTHON" tools/ml-assist/train.py rule "$LABELS_DIR/ml-rule-labels.jsonl" \
  --version "$MODEL_VERSION" --out "$ARTIFACTS_DIR/rule-prioritizer.json"

note "Train path pruner (#75)"
"$PYTHON" tools/ml-assist/train.py path "$LABELS_DIR/ml-path-labels.jsonl" \
  --version "$MODEL_VERSION" --out "$ARTIFACTS_DIR/path-pruner.json"

# --- 4. evaluate -----------------------------------------------------------
note "Measure end-to-end (zero-semantic-change asserted)"
MINOTAUR_GRAMMARS_DIR="$GRAMMARS_NATIVE" ML_EVAL_OUT="$EVAL_NATIVE" \
  dotnet test src/DevelApp.StepParser.Tests/DevelApp.StepParser.Tests.csproj \
  --configuration "$CONFIGURATION" --no-build \
  --filter "FullyQualifiedName~MlAssistEvaluation"

note "Validate evaluation output"
if [ ! -s "$EVAL_DIR/ml-assist-evaluation.csv" ]; then
  fail "$EVAL_DIR/ml-assist-evaluation.csv is missing or empty"
fi
echo "  evaluation rows: $(($(wc -l < "$EVAL_DIR/ml-assist-evaluation.csv") - 1))"

# --- 5. measurement report + acceptance gate -------------------------------
note "Measurement report"
"$PYTHON" tools/ml-assist/train.py summarize-eval "$EVAL_DIR/ml-assist-evaluation.csv"
REPORT="$REPORTS_DIR/measurement-report-v$MODEL_VERSION.md"
"$PYTHON" tools/ml-assist/train.py report-eval "$EVAL_DIR/ml-assist-evaluation.csv" \
  --rule-artifact "$ARTIFACTS_DIR/rule-prioritizer.json" \
  --path-artifact "$ARTIFACTS_DIR/path-pruner.json" \
  --corpus-ref "$CORPUS_REF" \
  --out "$REPORT"
echo "  report: $REPORT"

note "Acceptance gate (exit 0 = zero semantic changes AND end-to-end improvement)"
set +e
"$PYTHON" tools/ml-assist/train.py report-eval "$EVAL_DIR/ml-assist-evaluation.csv" \
  --rule-artifact "$ARTIFACTS_DIR/rule-prioritizer.json" \
  --path-artifact "$ARTIFACTS_DIR/path-pruner.json"
GATE=$?
set -e
case "$GATE" in
  0) echo "  ACCEPTED: zero semantic changes and a real end-to-end win." ;;
  1) fail "REJECTED: semantic divergence between the ML-off and ML-on arms (contract breach)" ;;
  2) fail "REJECTED: no end-to-end improvement over the ML-off arm" ;;
  *) fail "report-eval exited with $GATE" ;;
esac

note "Done"
cat <<EOF
Trained artifacts:
  $ARTIFACTS_DIR/rule-prioritizer.json
  $ARTIFACTS_DIR/path-pruner.json
Measurement report:
  $REPORT

Next: review the report, then commit artifacts + report and ship them
(git add $ARTIFACTS_DIR $REPORTS_DIR).
EOF
