#!/usr/bin/env python3
"""Offline trainer for the ENFAStepLexer-StepParser ML-assist artifacts.

Implements the training pipeline of issue #93 (sub-issue of #58): reads the
label traces captured by MlLabelTraceTests ("ml-label-rule/1" and
"ml-label-path/1" JSONL records), fits a managed logistic model and emits a
versioned artifact JSON loadable by LearnedRulePrioritizer (DevelApp.StepLexer)
and LearnedPathPruner (DevelApp.StepParser).

Pure standard library: no third-party packages, no network access, fully
deterministic (Newton/IRLS with an L2 ridge on a fixed holdout split).
Feature order and count MUST match the C# ExtractFeatures definitions.

Labels are streamed and aggregated on the fly: identical feature vectors are
collapsed into (negative, positive) counts before fitting, so memory stays
proportional to the number of DISTINCT feature vectors (at most 2^10 for the
binary rule features) rather than to the number of records. Corpus-scale
captures (issue #93) run in a few MB of memory.

Usage:
  python3 train.py rule labels.jsonl --version 0.2.0 --out rule-prioritizer.json
  python3 train.py path labels.jsonl --version 0.2.0 --out path-pruner.json
  python3 train.py summarize-eval ml-assist-evaluation.csv
  python3 train.py report-eval ml-assist-evaluation.csv --out measurement-report.md

See docs/ML-Assist.md for the full training and shipping workflow.
"""

import argparse
import json
import math
import sys

TRAINER_VERSION = "train.py 1.1 (newton-l2, streaming)"

MODEL_RULE = "rule-prioritizer"
MODEL_PATH = "path-pruner"

RULE_SCHEMA = "ml-label-rule/1"
PATH_SCHEMA = "ml-label-path/1"

RULE_FEATURE_COUNT = 10
PATH_FEATURE_COUNT = 6

RULE_FEATURE_NAMES = [
    "bias",
    "literalFirstByte",
    "regexDigitClassDigitByte",
    "regexLetterClassLetterByte",
    "regexWsClassWsByte",
    "regexGeneric",
    "skippable",
    "ctxRestricted",
    "literalFirstByteMiss",
    "nonAsciiByte",
]

PATH_FEATURE_NAMES = [
    "bias",
    "scoreDeficit",
    "positionDeficit",
    "stalled",
    "lowScore",
    "age",
]

HOLDOUT_MODULUS = 5  # every 5th record is held out for metrics (deterministic)


def fail(message):
    raise SystemExit("error: " + message)


# ---------------------------------------------------------------------------
# Input parsing (streaming)
# ---------------------------------------------------------------------------

def iter_labels(path, expected_schema, expected_features, label_key, invert=False):
    """Stream (features, label) tuples from a label JSONL file.

    With invert=True the label is flipped (0/1): the path pruner predicts
    the DOOM probability (LearnedPathPruner.PredictDoomProbability), so its
    model is trained on "did NOT succeed" labels from the trace's
    "succeeded" field.
    """
    with open(path, "r", encoding="utf-8") as handle:
        for line_number, line in enumerate(handle, 1):
            line = line.strip()
            if not line:
                continue
            try:
                record = json.loads(line)
            except json.JSONDecodeError as exc:
                fail("%s:%d: invalid JSON (%s)" % (path, line_number, exc))
            schema = record.get("schema")
            if schema != expected_schema:
                fail(
                    "%s:%d: expected schema %r, found %r"
                    % (path, line_number, expected_schema, schema)
                )
            features = record.get("features")
            if not isinstance(features, list) or len(features) != expected_features:
                fail(
                    "%s:%d: expected %d features, found %r"
                    % (path, line_number, expected_features, features)
                )
            raw_label = 1 if record.get(label_key) else 0
            label = 1 - raw_label if invert else raw_label
            yield tuple(float(v) for v in features), label


def stream_split(iterable):
    """Stream records into aggregated train/holdout count maps.

    Deterministically holds out every HOLDOUT_MODULUS-th record (0-based
    index 0, 5, 10, ...), matching the pre-aggregation split semantics.
    Returns (train_counts, holdout_counts, records, positives) where each
    count map is {features: [negative_count, positive_count]}.
    """
    train_counts = {}
    holdout_counts = {}
    records = 0
    positives = 0
    for features, label in iterable:
        records += 1
        positives += label
        counts = holdout_counts if records % HOLDOUT_MODULUS == 1 else train_counts
        entry = counts.get(features)
        if entry is None:
            counts[features] = [0, 1] if label else [1, 0]
        else:
            entry[label] += 1
    return train_counts, holdout_counts, records, positives


def count_records(counts):
    """Total number of records represented by an aggregated count map."""
    return sum(negatives + positives for negatives, positives in counts.values())


# ---------------------------------------------------------------------------
# Logistic regression (Newton / IRLS with L2 ridge on all but the bias)
# ---------------------------------------------------------------------------

def sigmoid(z):
    if z >= 0:
        return 1.0 / (1.0 + math.exp(-z))
    scaled = math.exp(z)
    return scaled / (1.0 + scaled)


def fit_logistic(aggregated, dimension, l2, max_iterations=100, tolerance=1e-10):
    """Fit weights[dimension] by Newton's method on the aggregated rows."""
    weights = [0.0] * dimension
    for iteration in range(1, max_iterations + 1):
        gradient = [0.0] * dimension
        hessian = [[0.0] * dimension for _ in range(dimension)]
        for features, (negatives, positives) in aggregated.items():
            z = 0.0
            for i in range(dimension):
                z += weights[i] * features[i]
            p = sigmoid(z)
            # Weighted residuals: negatives (y=0) contribute p, positives
            # (y=1) contribute p-1.
            residual = negatives * p + positives * (p - 1.0)
            curvature = (negatives + positives) * p * (1.0 - p)
            for i in range(dimension):
                gradient[i] += residual * features[i]
                for j in range(i, dimension):
                    hessian[i][j] += curvature * features[i] * features[j]
        # Ridge on every coefficient except the bias (index 0).
        for i in range(1, dimension):
            gradient[i] += l2 * weights[i]
            hessian[i][i] += l2
        for i in range(dimension):
            for j in range(i):
                hessian[i][j] = hessian[j][i]
        step = solve(hessian, [-g for g in gradient])
        if step is None:
            fail("Newton system became singular; increase --l2")
        delta = math.sqrt(sum(s * s for s in step))
        for i in range(dimension):
            weights[i] += step[i]
        if delta < tolerance:
            return weights, iteration
    return weights, max_iterations


def solve(matrix, vector):
    """Gaussian elimination with partial pivoting; returns None if singular."""
    n = len(vector)
    augmented = [list(matrix[i]) + [vector[i]] for i in range(n)]
    for column in range(n):
        pivot = max(range(column, n), key=lambda r: abs(augmented[r][column]))
        if abs(augmented[pivot][column]) < 1e-14:
            return None
        augmented[column], augmented[pivot] = augmented[pivot], augmented[column]
        pivot_value = augmented[column][column]
        for r in range(column + 1, n):
            factor = augmented[r][column] / pivot_value
            if factor != 0.0:
                for c in range(column, n + 1):
                    augmented[r][c] -= factor * augmented[column][c]
    result = [0.0] * n
    for r in range(n - 1, -1, -1):
        total = augmented[r][n]
        for c in range(r + 1, n):
            total -= augmented[r][c] * result[c]
        result[r] = total / augmented[r][r]
    return result


# ---------------------------------------------------------------------------
# Metrics (on aggregated count maps; identical results to per-record
# evaluation of the same rows)
# ---------------------------------------------------------------------------

def predict(weights, features):
    z = 0.0
    for i in range(len(weights)):
        z += weights[i] * features[i]
    return sigmoid(z)


def log_loss_accuracy(weights, counts):
    records = count_records(counts)
    if not records:
        return None
    loss = 0.0
    correct = 0
    for features, (negatives, positives) in counts.items():
        p = predict(weights, features)
        p = min(max(p, 1e-12), 1.0 - 1e-12)
        loss -= positives * math.log(p) + negatives * math.log(1.0 - p)
        if p >= 0.5:
            correct += positives
        else:
            correct += negatives
    return {
        "records": records,
        "logLoss": round(loss / records, 6),
        "accuracy": round(correct / records, 6),
    }


def auc(weights, counts):
    """Rank-based AUC (Mann-Whitney) on aggregated counts; returns None on a
    single-class set."""
    positive = sum(positives for _, (negatives, positives) in counts.items())
    negative = sum(negatives for _, (negatives, positives) in counts.items())
    if positive == 0 or negative == 0:
        return None
    # Sort distinct feature groups by score; assign every record in a group
    # the group's average 1-based rank (ties handled exactly as in the
    # per-record formulation).
    scored = sorted(
        ((predict(weights, features), negatives, positives)
         for features, (negatives, positives) in counts.items()),
        key=lambda triple: triple[0],
    )
    rank_sum = 0.0
    position = 0  # 0-based index of the first record of the current group
    for score, negatives, positives in scored:
        group_size = negatives + positives
        average_rank = position + (group_size + 1) / 2.0  # 1-based average
        rank_sum += average_rank * positives
        position += group_size
    return round(
        (rank_sum - positive * (positive + 1) / 2.0) / (positive * negative), 6)


# ---------------------------------------------------------------------------
# Artifact emission
# ---------------------------------------------------------------------------

def build_artifact(model, model_version, feature_names, weights, source_path,
                   source_schema, records, positives, iterations, l2,
                   holdout_metrics, holdout_auc, extra=None):
    artifact = {
        "schema": "ml-assist-artifact/1",
        "model": model,
        "modelVersion": model_version,
        "featureCount": len(feature_names),
        "featureNames": list(feature_names),
        "weights": [round(w, 10) for w in weights],
    }
    if extra:
        artifact.update(extra)
    artifact["trainedOn"] = {
        "schema": source_schema,
        "label": "doomed (= !succeeded)" if model == MODEL_PATH else "matched",
        "records": records,
        "positives": positives,
        "corpus": "Minotaur-Grammars (see docs/ML-Assist.md)",
    }
    metrics = dict(holdout_metrics) if holdout_metrics else {"records": 0}
    if holdout_auc is not None:
        metrics["auc"] = holdout_auc
    artifact["metrics"] = metrics
    artifact["trainer"] = {
        "implementation": TRAINER_VERSION,
        "method": "Newton/IRLS, L2 ridge (bias unpenalized)",
        "l2": l2,
        "iterations": iterations,
        "holdout": "every %dth record" % HOLDOUT_MODULUS,
    }
    return artifact


def train_model(model, labels_path, model_version, l2, out_path):
    if model == MODEL_RULE:
        expected_schema, feature_count, label_key, feature_names = (
            RULE_SCHEMA, RULE_FEATURE_COUNT, "matched", RULE_FEATURE_NAMES)
    else:
        expected_schema, feature_count, label_key, feature_names = (
            PATH_SCHEMA, PATH_FEATURE_COUNT, "succeeded", PATH_FEATURE_NAMES)

    invert = model == MODEL_PATH
    train_counts, holdout_counts, records, positives = stream_split(
        iter_labels(labels_path, expected_schema, feature_count, label_key, invert))

    if not records:
        fail("%s: no %r records found" % (labels_path, expected_schema))
    if positives == 0 or positives == records:
        fail(
            "%s: degenerate label set (%d positive of %d records); "
            "the corpus must exercise both classes"
            % (labels_path, positives, records)
        )

    weights, iterations = fit_logistic(train_counts, feature_count, l2)

    holdout_metrics = log_loss_accuracy(weights, holdout_counts)
    holdout_auc = auc(weights, holdout_counts)

    extra = None
    if model == MODEL_PATH:
        extra = {"confidenceThreshold": 0.9, "minPathCount": 10}

    artifact = build_artifact(
        model, model_version, feature_names, weights, labels_path,
        expected_schema, records, positives, iterations, l2,
        holdout_metrics, holdout_auc, extra)

    text = json.dumps(artifact, indent=2, sort_keys=False) + "\n"
    if out_path:
        with open(out_path, "w", encoding="utf-8") as handle:
            handle.write(text)
    else:
        sys.stdout.write(text)

    # Human-readable summary on stderr so stdout stays pipeable.
    print(
        "trained %s v%s: %d records (%d positive), %d Newton iterations, "
        "holdout metrics: %s"
        % (model, model_version, records, positives, iterations,
           json.dumps(holdout_metrics)), file=sys.stderr)


# ---------------------------------------------------------------------------
# Evaluation summarizer
# ---------------------------------------------------------------------------

def percentile(values, fraction):
    if not values:
        return 0.0
    ordered = sorted(values)
    index = min(len(ordered) - 1, int(round(fraction * (len(ordered) - 1))))
    return ordered[index]


def summarize_eval(csv_path):
    """Summarize the MlAssistEvaluationTests comparison CSV (both arms)."""
    with open(csv_path, "r", encoding="utf-8") as handle:
        lines = [line.strip() for line in handle if line.strip()]
    if len(lines) < 2:
        fail("%s: no records to summarize" % csv_path)
    header = lines[0].split(",")
    rows = [dict(zip(header, line.split(","))) for line in lines[1:]]

    def arm(suffix):
        times = [
            float(r["parseTime%sMicros" % suffix]) for r in rows
            if r.get("parseTime%sMicros" % suffix)
        ]
        ratios = [
            float(r["wastedRatio%s" % suffix]) for r in rows
            if r.get("wastedRatio%s" % suffix)
        ]
        successes = sum(1 for r in rows if r.get("success%s" % suffix) == "True")
        return {
            "parses": len(rows),
            "successes": successes,
            "parseTimeP50Micros": round(percentile(times, 0.50), 1),
            "parseTimeP95Micros": round(percentile(times, 0.95), 1),
            "parseTimeTotalMicros": round(sum(times), 1),
            "wastedRatioMean": round(sum(ratios) / len(ratios), 4) if ratios else 0.0,
        }

    off = arm("Off")
    on = arm("On")
    pruned = sum(int(r.get("pathsPruned", "0") or 0) for r in rows)
    consultations = sum(int(r.get("consultations", "0") or 0) for r in rows)

    report = {
        "schema": "ml-assist-eval-summary/1",
        "records": len(rows),
        "mlOff": off,
        "mlOn": on,
        "pathsPruned": pruned,
        "consultations": consultations,
    }
    if off["parseTimeTotalMicros"] > 0:
        report["parseTimeChange"] = round(
            (on["parseTimeTotalMicros"] - off["parseTimeTotalMicros"])
            / off["parseTimeTotalMicros"] * 100.0, 2)
    if off["wastedRatioMean"] > 0:
        report["wastedRatioChange"] = round(
            (on["wastedRatioMean"] - off["wastedRatioMean"])
            / off["wastedRatioMean"] * 100.0, 2)
    print(json.dumps(report, indent=2))


# ---------------------------------------------------------------------------
# Measurement report (issue #93 definition of done)
# ---------------------------------------------------------------------------

def read_eval_rows(csv_path):
    with open(csv_path, "r", encoding="utf-8") as handle:
        lines = [line.strip() for line in handle if line.strip()]
    if len(lines) < 2:
        fail("%s: no records to summarize" % csv_path)
    header = lines[0].split(",")
    return [dict(zip(header, line.split(","))) for line in lines[1:]]


def _load_json_or_none(path):
    if not path:
        return None
    try:
        with open(path, "r", encoding="utf-8") as handle:
            return json.load(handle)
    except (OSError, ValueError) as error:
        fail("cannot read artifact %s: %s" % (path, error))


def report_eval(csv_path, out_path, rule_artifact_path, path_artifact_path,
                corpus_ref):
    """Write the #93 measurement report (markdown) from the eval CSV.

    Exits non-zero when the zero-semantic-change contract is breached
    (exit 1) or when acceptance fails: no end-to-end improvement against
    the ML-off arm (exit 2).
    """
    import datetime

    rows = read_eval_rows(csv_path)
    semantic_changes = [
        r for r in rows if r.get("successOff") != r.get("successOn")]

    def arm(suffix):
        times = [float(r["parseTime%sMicros" % suffix]) for r in rows
                 if r.get("parseTime%sMicros" % suffix)]
        ratios = [float(r["wastedRatio%s" % suffix]) for r in rows
                  if r.get("wastedRatio%s" % suffix)]
        successes = sum(1 for r in rows if r.get("success%s" % suffix) == "True")
        return {
            "parses": len(rows),
            "successes": successes,
            "p50": percentile(times, 0.50),
            "p95": percentile(times, 0.95),
            "total": sum(times),
            "wastedMean": (sum(ratios) / len(ratios)) if ratios else 0.0,
        }

    off = arm("Off")
    on = arm("On")
    pruned = sum(int(r.get("pathsPruned", "0") or 0) for r in rows)
    consultations = sum(int(r.get("consultations", "0") or 0) for r in rows)

    time_change = ((on["total"] - off["total"]) / off["total"] * 100.0
                   if off["total"] > 0 else 0.0)
    wasted_change = ((on["wastedMean"] - off["wastedMean"])
                     / off["wastedMean"] * 100.0
                     if off["wastedMean"] > 0 else 0.0)

    improved = sum(1 for r in rows
                   if float(r.get("parseTimeOnMicros") or 0)
                   < float(r.get("parseTimeOffMicros") or 0))
    regressed = sum(1 for r in rows
                    if float(r.get("parseTimeOnMicros") or 0)
                    > float(r.get("parseTimeOffMicros") or 0))

    grammars = {}
    for row in rows:
        name = row.get("grammar", "(unknown)")
        entry = grammars.setdefault(name, {"records": 0, "off": 0.0, "on": 0.0})
        entry["records"] += 1
        entry["off"] += float(row.get("parseTimeOffMicros") or 0)
        entry["on"] += float(row.get("parseTimeOnMicros") or 0)

    rule_artifact = _load_json_or_none(rule_artifact_path)
    path_artifact = _load_json_or_none(path_artifact_path)
    version = (rule_artifact or path_artifact or {}).get("modelVersion", "unknown")
    rule_metrics = (rule_artifact or {}).get("metrics", {}) if rule_artifact else {}
    path_metrics = (path_artifact or {}).get("metrics", {}) if path_artifact else {}

    zero_semantic = not semantic_changes
    improvement = time_change < 0.0 or wasted_change < 0.0
    overall = zero_semantic and improvement

    lines = []
    lines.append("---")
    lines.append("layout: default")
    lines.append("title: ML-Assist Measurement Report v%s" % version)
    lines.append("---")
    lines.append("")
    lines.append("# ML-Assist Measurement Report - v%s (issues #93 / #58)" % version)
    lines.append("")
    lines.append("Recorded: %s (UTC) - schema `ml-assist-report/1`" % datetime.datetime.now(datetime.timezone.utc).strftime("%Y-%m-%d"))
    if corpus_ref:
        lines.append("Corpus: Minotaur-Grammars `%s`" % corpus_ref)
    lines.append("Trainer: %s" % ((rule_artifact or {}).get("trainer", {}).get("implementation", "n/a")))
    lines.append("Records: %d (grammar, example) pairs, parsed twice - ML off (shipped"
                 " default) vs ML on (v%s artifacts)." % (len(rows), version))
    lines.append("")
    lines.append("## Artifacts")
    lines.append("")
    lines.append("| Artifact | modelVersion | holdout records | holdout log-loss | holdout accuracy | holdout AUC |")
    lines.append("|---|---|---|---|---|---|")
    for label, artifact, metrics in (
            ("`rule-prioritizer.json`", rule_artifact, rule_metrics),
            ("`path-pruner.json`", path_artifact, path_metrics)):
        if not artifact:
            lines.append("| %s | (not provided) | - | - | - | - |" % label)
            continue
        lines.append("| %s | %s | %s | %s | %s | %s |" % (
            label, artifact.get("modelVersion", "?"),
            metrics.get("records", "?"), metrics.get("logLoss", "?"),
            metrics.get("accuracy", "?"), metrics.get("auc", "?")))
    if path_artifact:
        lines.append("")
        lines.append("Path pruner gates: `confidenceThreshold` = %s, "
                     "`minPathCount` = %s." % (
                         path_artifact.get("confidenceThreshold", "?"),
                         path_artifact.get("minPathCount", "?")))
    lines.append("")
    lines.append("## Zero semantic change")
    lines.append("")
    lines.append("`successOff == successOn` for **all %d records**: **0 semantic"
                 " changes** (asserted by `MlAssistEvaluationTests`, which fails"
                 " the run otherwise; this report re-verifies and exits non-zero"
                 " on any divergence)." % len(rows))
    lines.append("")
    lines.append("## End-to-end measurement")
    lines.append("")
    lines.append("| Arm | Parses | Successes | p50 (us) | p95 (us) | Total (us) | Wasted-attempt ratio (mean) |")
    lines.append("|---|---|---|---|---|---|---|")
    lines.append("| ML off (shipped default) | %d | %d | %.1f | %.1f | %.1f | %.4f |" % (
        off["parses"], off["successes"], off["p50"], off["p95"], off["total"],
        off["wastedMean"]))
    lines.append("| ML on (v%s) | %d | %d | %.1f | %.1f | %.1f | %.4f |" % (
        version, on["parses"], on["successes"], on["p50"], on["p95"],
        on["total"], on["wastedMean"]))
    lines.append("")
    lines.append("- Parse time (total): %.1f -> %.1f us (**%.2f%%**) (negative = faster)" % (
        off["total"], on["total"], time_change))
    lines.append("- Wasted-attempt ratio (mean): %.4f -> %.4f (**%.2f%%**) (negative = better)" % (
        off["wastedMean"], on["wastedMean"], wasted_change))
    lines.append("- Examples faster with ML: **%d**, slower: **%d**, unchanged: %d"
                 % (improved, regressed, len(rows) - improved - regressed))
    lines.append("- Paths pruned: **%d** - pruner consultations: **%d**"
                 % (pruned, consultations))
    lines.append("")
    lines.append("## Per-grammar changes (total parse time)")
    lines.append("")
    lines.append("| Grammar | Records | ML off total (us) | ML on total (us) | Change |")
    lines.append("|---|---|---|---|---|")
    for name, entry in sorted(grammars.items(),
                              key=lambda kv: kv[1]["off"], reverse=True):
        change = ((entry["on"] - entry["off"]) / entry["off"] * 100.0
                  if entry["off"] > 0 else 0.0)
        lines.append("| `%s` | %d | %.1f | %.1f | %+.2f%% |" % (
            name, entry["records"], entry["off"], entry["on"], change))
    lines.append("")
    lines.append("## Acceptance (#58 definition of done)")
    lines.append("")
    lines.append("- Zero semantic changes: **%s**" % ("PASS" if zero_semantic else "FAIL (%d divergent records)" % len(semantic_changes)))
    lines.append("- End-to-end improvement (parse time and/or wasted-attempt ratio): **%s**" % ("PASS" if improvement else "FAIL"))
    lines.append("- Overall verdict: **%s**" % ("PASS" if overall else "FAIL"))
    lines.append("")
    lines.append("Timings are machine-dependent and intended for relative,"
                 " same-run comparison only (both arms measured in the same"
                 " process; see [ml-baseline/README.md](../ml-baseline/README.md)"
                 " for the pre-ML #73 baseline and its caveats).")
    lines.append("")

    report_text = "\n".join(lines)
    if out_path:
        with open(out_path, "w", encoding="utf-8") as handle:
            handle.write(report_text + "\n")
    else:
        print(report_text)

    print(json.dumps({
        "schema": "ml-assist-report/1",
        "records": len(rows),
        "modelVersion": version,
        "semanticChanges": len(semantic_changes),
        "parseTimeChange": round(time_change, 2),
        "wastedRatioChange": round(wasted_change, 2),
        "faster": improved,
        "slower": regressed,
        "pathsPruned": pruned,
        "consultations": consultations,
        "verdict": "PASS" if overall else "FAIL",
    }, indent=2))
    if out_path:
        print("report written to %s" % out_path, file=sys.stderr)

    if not zero_semantic:
        sys.exit(1)
    if not overall:
        sys.exit(2)


# ---------------------------------------------------------------------------
# CLI
# ---------------------------------------------------------------------------

def main(argv=None):
    parser = argparse.ArgumentParser(
        description="Offline trainer for the ML-assist artifacts (issue #93).")
    commands = parser.add_subparsers(dest="command", required=True)

    rule = commands.add_parser(
        "rule", help="Train the rule prioritizer on ml-label-rule/1 records.")
    rule.add_argument("labels", help="ml-rule-labels.jsonl from MlLabelTraceTests")
    rule.add_argument("--version", default="0.2.0", help="model version to stamp")
    rule.add_argument("--l2", type=float, default=1e-3, help="ridge strength")
    rule.add_argument("--out", help="output artifact JSON (default: stdout)")

    path = commands.add_parser(
        "path", help="Train the path pruner on ml-label-path/1 records.")
    path.add_argument("labels", help="ml-path-labels.jsonl from MlLabelTraceTests")
    path.add_argument("--version", default="0.2.0", help="model version to stamp")
    path.add_argument("--l2", type=float, default=1e-3, help="ridge strength")
    path.add_argument("--out", help="output artifact JSON (default: stdout)")

    summary = commands.add_parser(
        "summarize-eval",
        help="Summarize the MlAssistEvaluationTests comparison CSV.")
    summary.add_argument("csv", help="ml-assist-evaluation.csv")

    report = commands.add_parser(
        "report-eval",
        help="Write the #93 measurement report (markdown) from the eval CSV.")
    report.add_argument("csv", help="ml-assist-evaluation.csv")
    report.add_argument("--out", help="output markdown (default: stdout)")
    report.add_argument("--rule-artifact", default="docs/ml-assist/artifacts/rule-prioritizer.json",
                        help="trained rule prioritizer artifact JSON")
    report.add_argument("--path-artifact", default="docs/ml-assist/artifacts/path-pruner.json",
                        help="trained path pruner artifact JSON")
    report.add_argument("--corpus-ref", default="main",
                        help="corpus reference, e.g. 'main@abc1234'")

    args = parser.parse_args(argv)
    if args.command == "rule":
        train_model(MODEL_RULE, args.labels, args.version, args.l2, args.out)
    elif args.command == "path":
        train_model(MODEL_PATH, args.labels, args.version, args.l2, args.out)
    elif args.command == "summarize-eval":
        summarize_eval(args.csv)
    else:
        report_eval(args.csv, args.out, args.rule_artifact,
                    args.path_artifact, args.corpus_ref)


if __name__ == "__main__":
    main()
