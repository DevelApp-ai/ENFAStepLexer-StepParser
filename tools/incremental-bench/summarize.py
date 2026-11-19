#!/usr/bin/env python3
"""Summarize DevelApp.IncrementalBench CSVs into the issue #101 measurement
report tables (docs/measurements/incremental-parsing-ides).

Standard library only. Usage:

    python3 tools/incremental-bench/summarize.py --results DIR [--out FILE]

Reads peredit.csv, trace.csv, coldstart.csv, realex.csv and environment.json
from --results (as written by the benchmark runner) and prints (or writes) a
Markdown report: per-edit latency p50/p95, cache hit rates per edit type,
size scaling fits, cold-start curve, memory overhead, edit-distance/latency
correlation, and the correctness-guard census.
"""

import argparse
import csv
import json
import math
import statistics
from pathlib import Path

ARMS = ("spi", "full", "lex")
TYPES = ("CharInsert", "WordCompletion", "LineInsertion", "BlockPaste")


def read_csv(path):
    if not path.exists():
        return []
    with path.open(newline="", encoding="utf-8") as handle:
        return list(csv.DictReader(handle))


def fnum(row, key, default=None):
    value = row.get(key, "")
    if value in ("", None):
        return default
    try:
        return float(value)
    except ValueError:
        return default


def p50(values):
    return statistics.median(values) if values else float("nan")


def p95(values):
    if not values:
        return float("nan")
    ordered = sorted(values)
    index = min(len(ordered) - 1, math.ceil(0.95 * len(ordered)) - 1)
    return ordered[index]


def pearson(xs, ys):
    n = len(xs)
    if n < 3:
        return float("nan")
    mx, my = sum(xs) / n, sum(ys) / n
    sxx = sum((x - mx) ** 2 for x in xs)
    syy = sum((y - my) ** 2 for y in ys)
    sxy = sum((x - mx) * (y - my) for x, y in zip(xs, ys))
    if sxx == 0 or syy == 0:
        return float("nan")
    return sxy / math.sqrt(sxx * syy)


def loglog_fit(pairs):
    """Least-squares slope of log(y) vs log(x); y is a median per x."""
    xs = [math.log(x) for x, _ in pairs]
    ys = [math.log(y) for _, y in pairs]
    n = len(xs)
    if n < 2:
        return float("nan")
    mx, my = sum(xs) / n, sum(ys) / n
    den = sum((x - mx) ** 2 for x in xs)
    if den == 0:
        return float("nan")
    return sum((x - mx) * (y - my) for x, y in zip(xs, ys)) / den


def fmt(value, digits=1):
    if value is None or math.isnan(value):
        return "-"
    return f"{value:.{digits}f}"


def env_header(results):
    env_path = results / "environment.json"
    if not env_path.exists():
        return ["_environment.json missing_"]
    env = json.loads(env_path.read_text(encoding="utf-8"))
    return [
        f"- Machine: `{env.get('machine', '?')}`, {env.get('os', '?')} "
        f"({env.get('osArchitecture', '?')}), {env.get('processorCount', '?')} core(s), "
        f".NET {env.get('dotnetVersion', '?')}",
        f"- Recorded: {env.get('timestampUtc', '?')} (reps: {env.get('reps', '?')}, "
        f"seed: {env.get('seed', '?')}, args: `{env.get('args', '')}`)",
    ]


def peredit_table(rows):
    lines = ["", "## Per-edit latency, p50 / p95 (ms)", ""]
    lines.append("| Size | Edit type | spi p50 (p95) | full p50 (p95) | lex p50 (p95) | spi/full |")
    lines.append("|---|---|---|---|---|---|")
    for size in sorted({int(r["size"]) for r in rows}):
        for typ in TYPES:
            cells, medians = [], {}
            for arm in ARMS:
                ms = [fnum(r, "editMs") for r in rows
                      if r["arm"] == arm and r["size"] == str(size) and r["type"] == typ
                      and fnum(r, "editMs") is not None]
                medians[arm] = p50(ms)
                cells.append(f"{fmt(medians[arm])} ({fmt(p95(ms))})")
            ratio = medians["spi"] / medians["full"] if medians.get("full") else float("nan")
            lines.append(f"| {size} | {typ} | {cells[0]} | {cells[1]} | {cells[2]} | {fmt(ratio, 2)}x |")
    return lines


def scaling_fits(rows):
    lines = ["", "## Size scaling (log-log exponent, p50 latency vs lines)", ""]
    lines.append("Exponent 1.0 = linear in document size; O(log n) resume behavior would\n"
                 "push the incremental arm toward 0.")
    lines.append("")
    lines.append("| Arm | Exponent |")
    lines.append("|---|---|")
    sizes = sorted({int(r["size"]) for r in rows})
    for arm in ARMS:
        pairs = []
        for size in sizes:
            ms = [fnum(r, "editMs") for r in rows
                  if r["arm"] == arm and r["size"] == str(size)
                  and fnum(r, "editMs") is not None]
            if ms:
                pairs.append((size, p50(ms)))
        lines.append(f"| {arm} | {fmt(loglog_fit(pairs), 2)} |")
    return lines


def hit_rate_table(rows):
    lines = ["", "## Cache hit rates per edit type (warm 15-edit traces)", ""]
    lines.append("| Size | Edit type | parse cache hit | resume boundary/tokens (p50) | lexer reuse (p50) | parse steps (p50) |")
    lines.append("|---|---|---|---|---|---|")
    for size in sorted({int(r["size"]) for r in rows}):
        for typ in TYPES:
            sel = [r for r in rows if r["size"] == str(size) and r["type"] == typ]
            if not sel:
                continue
            hits = sum(1 for r in sel if r.get("parseCacheHit") == "True")
            frac = f"{hits / len(sel):.2f}"
            boundaries = [fnum(r, "parseResumeBoundary") for r in sel if fnum(r, "parseResumeBoundary") is not None]
            token_counts = [fnum(r, "tokens") for r in sel if fnum(r, "tokens") is not None]
            if boundaries and token_counts:
                bfrac = f"{p50(boundaries) / max(1, statistics.median(token_counts)):.2f}"
            else:
                bfrac = "-"
            reuse = [fnum(r, "lexReuseRatio") for r in sel if fnum(r, "lexReuseRatio") is not None]
            steps = [fnum(r, "parseSteps") for r in sel if fnum(r, "parseSteps") is not None]
            lines.append(f"| {size} | {typ} | {frac} (n={len(sel)}) | {bfrac} | "
                         f"{fmt(p50(reuse), 4)} | {fmt(p50(steps), 0)} |")
    return lines


def coldstart_curve(rows):
    lines = ["", "## Cold-start curve (first 20 edits after document open, per rep)", ""]
    lines.append("| Edit # | spi p50 (ms) | full p50 (ms) |")
    lines.append("|---|---|---|")
    indices = sorted({int(r["editIndex"]) for r in rows if r.get("editIndex") not in ("", "-1", None)})
    for i in indices:
        cells = []
        for arm in ("spi", "full"):
            ms = [fnum(r, "editMs") for r in rows
                  if r["arm"] == arm and r["editIndex"] == str(i)
                  and fnum(r, "editMs") is not None]
            cells.append(fmt(p50(ms)))
        lines.append(f"| {i + 1} | {cells[0]} | {cells[1]} |")
    return lines


def memory_table(rows):
    lines = ["", "## Memory overhead (allocated bytes per timed edit, p50)", ""]
    lines.append("| Size | spi | full | lex |")
    lines.append("|---|---|---|---|")
    for size in sorted({int(r["size"]) for r in rows}):
        cells = []
        for arm in ARMS:
            allocs = [fnum(r, "allocBytes") for r in rows
                      if r["arm"] == arm and r["size"] == str(size)
                      and fnum(r, "allocBytes") is not None]
            cells.append(f"{p50(allocs) / 1048576:.1f} MB" if allocs else "-")
        lines.append(f"| {size} | {cells[0]} | {cells[1]} | {cells[2]} |")
    lines.append("")
    lines.append("NOTE: allocations, not retention: the engine as shipped also retains")
    lines.append("parse-context symbol-table state across parses on one engine instance")
    lines.append("(see the engine issue linked from the README); sequential-edit SPI")
    lines.append("latency grows across a session in fixed-hardware long runs.")
    return lines


def correlation_table(rows):
    lines = ["", "## Edit size vs latency correlation (Pearson, inserted bytes vs ms)", ""]
    lines.append("| Arm | Size | r |")
    lines.append("|---|---|---|")
    for size in sorted({int(r["size"]) for r in rows}):
        for arm in ARMS:
            xs, ys = [], []
            for r in rows:
                ms = fnum(r, "editMs")
                ins = fnum(r, "insertLength")
                if r["arm"] == arm and r["size"] == str(size) and ms is not None and ins is not None:
                    xs.append(ins)
                    ys.append(ms)
            r_val = pearson(xs, ys)
            lines.append(f"| {arm} | {size} | {fmt(r_val, 2)} |")
    return lines


def guard_census(*rowsets):
    lines = ["", "## Correctness guards", ""]
    total = mismatch = 0
    for rows in rowsets:
        for row in rows:
            guard = row.get("guard", "")
            if guard:
                total += 1
                if guard != "ok":
                    mismatch += 1
    ok = total - mismatch
    lines.append(f"- Cross-arm/cold-reference guards: {ok}/{total} ok, {mismatch} mismatch.")
    if mismatch:
        lines.append("- **MISMATCHES PRESENT: investigate before using the numbers.**")
    return lines


def realex_table(rows):
    lines = ["", "## Real C# at the lexer level (CSharp10 grammar, GreedyLongestMatch)", ""]
    lines.append("| Size (lines) | cold full lex (ms) | tokens | edit type | suffix re-lex p50 (ms) | lexer reuse p50 |")
    lines.append("|---|---|---|---|---|---|")
    for size in sorted({int(r["size"]) for r in rows}):
        cold = [fnum(r, "editMs") for r in rows
                if r["arm"] == "coldlex" and r["size"] == str(size)]
        tokens = [fnum(r, "tokens") for r in rows
                  if r["arm"] == "coldlex" and r["size"] == str(size)
                  and fnum(r, "tokens") is not None]
        header = f"| {size} | {fmt(p50(cold))} | {fmt(p50(tokens), 0)} | "
        for typ in TYPES:
            warm = [fnum(r, "editMs") for r in rows
                    if r["arm"] == "lex" and r["size"] == str(size) and r["type"] == typ
                    and fnum(r, "editMs") is not None]
            reuse = [fnum(r, "lexReuseRatio") for r in rows
                     if r["arm"] == "lex" and r["size"] == str(size) and r["type"] == typ
                     and fnum(r, "lexReuseRatio") is not None]
            lines.append(f"{header}{typ} | {fmt(p50(warm))} | {fmt(p50(reuse), 4)} |")
    return lines


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--results", required=True, help="Benchmark results directory")
    parser.add_argument("--out", help="Write the report here instead of printing")
    args = parser.parse_args()

    results = Path(args.results)
    peredit = read_csv(results / "peredit.csv")
    trace = read_csv(results / "trace.csv")
    coldstart = read_csv(results / "coldstart.csv")
    realex = read_csv(results / "realex.csv")

    lines = ["# Incremental parsing measurements (issue #101)", ""]
    lines += env_header(results)
    if peredit:
        lines += peredit_table(peredit)
        lines += scaling_fits(peredit)
        lines += memory_table(peredit)
        lines += correlation_table(peredit)
    if trace:
        lines += hit_rate_table(trace)
    if coldstart:
        lines += coldstart_curve(coldstart)
    if realex:
        lines += realex_table(realex)
    if peredit or trace:
        lines += guard_census(peredit, trace, coldstart)

    report = "\n".join(lines) + "\n"
    if args.out:
        Path(args.out).write_text(report, encoding="utf-8")
        print(f"wrote {args.out}")
    else:
        print(report)


if __name__ == "__main__":
    main()
