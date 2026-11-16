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

Usage:
  python3 train.py rule labels.jsonl --version 0.2.0 --out rule-prioritizer.json
  python3 train.py path labels.jsonl --version 0.2.0 --out path-pruner.json
  python3 train.py summarize-eval ml-assist-evaluation.csv

See docs/ML-Assist.md for the full training and shipping workflow.
"""

import argparse
import json
import math
import sys

TRAINER_VERSION = "train.py 1.0 (newton-l2)"

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
# Input parsing
# ---------------------------------------------------------------------------

def read_labels(path, expected_schema, expected_features, label_key, invert=False):
    """Read a label JSONL file and return (features, label) tuples.

    With invert=True the label is flipped (0/1): the path pruner predicts
    the DOOM probability (LearnedPathPruner.PredictDoomProbability), so its
    model is trained on "did NOT succeed" labels from the trace's
    "succeeded" field.
    """
    rows = []
    positives = 0
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
            positives += label
            rows.append((tuple(float(v) for v in features), label))
    if not rows:
        fail("%s: no %r records found" % (path, expected_schema))
    if positives == 0 or positives == len(rows):
        fail(
            "%s: degenerate label set (%d positive of %d records); "
            "the corpus must exercise both classes"
            % (path, positives, len(rows))
        )
    return rows, positives


def split_holdout(rows):
    """Deterministically hold out every HOLDOUT_MODULUS-th record."""
    train = []
    holdout = []
    for index, row in enumerate(rows):
        if index % HOLDOUT_MODULUS == 0:
            holdout.append(row)
        else:
            train.append(row)
    return train, holdout


def aggregate(rows):
    """Group identical feature vectors: {features: [negative, positive]}.

    The rule features are binary, so this collapses the corpus into at most
    a few thousand distinct rows and makes the Newton solve trivially fast.
    Continuous path features compress less but still benefit.
    """
    counts = {}
    for features, label in rows:
        entry = counts.get(features)
        if entry is None:
            counts[features] = [0, 0]
            entry = counts[features]
        entry[label] += 1
    return counts


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
# Metrics
# ---------------------------------------------------------------------------

def predict(weights, features):
    z = 0.0
    for i in range(len(weights)):
        z += weights[i] * features[i]
    return sigmoid(z)


def log_loss_accuracy(weights, rows):
    if not rows:
        return None
    loss = 0.0
    correct = 0
    for features, label in rows:
        p = predict(weights, features)
        p = min(max(p, 1e-12), 1.0 - 1e-12)
        loss -= label * math.log(p) + (1 - label) * math.log(1.0 - p)
        if (p >= 0.5) == (label == 1):
            correct += 1
    count = len(rows)
    return {
        "records": count,
        "logLoss": round(loss / count, 6),
        "accuracy": round(correct / count, 6),
    }


def auc(weights, rows):
    """Rank-based AUC (Mann-Whitney); returns None on a single-class set."""
    labels = {label for _, label in rows}
    if len(labels) < 2:
        return None
    scored = sorted(
        ((predict(weights, features), label) for features, label in rows),
        key=lambda pair: pair[0],
    )
    # Average ranks for ties.
    ranks = {}
    index = 0
    while index < len(scored):
        stop = index
        while stop < len(scored) and scored[stop][0] == scored[index][0]:
            stop += 1
        average_rank = (index + stop + 1) / 2.0  # 1-based average
        for k in range(index, stop):
            ranks[k] = average_rank
        index = stop
    positive = sum(1 for _, label in rows if label == 1)
    negative = len(rows) - positive
    rank_sum = sum(ranks[k] for k, (_, label) in enumerate(scored) if label == 1)
    return round((rank_sum - positive * (positive + 1) / 2.0) / (positive * negative), 6)


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
    rows, positives = read_labels(
        labels_path, expected_schema, feature_count, label_key, invert)
    train_rows, holdout_rows = split_holdout(rows)

    weights, iterations = fit_logistic(aggregate(train_rows), feature_count, l2)

    holdout_metrics = log_loss_accuracy(weights, holdout_rows)
    holdout_auc = auc(weights, holdout_rows)

    extra = None
    if model == MODEL_PATH:
        extra = {"confidenceThreshold": 0.9, "minPathCount": 10}

    artifact = build_artifact(
        model, model_version, feature_names, weights, labels_path,
        expected_schema, len(rows), positives, iterations, l2,
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
        % (model, model_version, len(rows), positives, iterations,
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

    args = parser.parse_args(argv)
    if args.command == "rule":
        train_model(MODEL_RULE, args.labels, args.version, args.l2, args.out)
    elif args.command == "path":
        train_model(MODEL_PATH, args.labels, args.version, args.l2, args.out)
    else:
        summarize_eval(args.csv)


if __name__ == "__main__":
    main()
