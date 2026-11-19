#!/usr/bin/env python3
"""Fixed-hardware tree-sitter reference arm for the issue #101 measurements
(docs/measurements/incremental-parsing-ides).

This arm cannot run inside the dev container (no native compiler / no pip
installs); it is committed here to run on the same fixed hardware as the
main benchmark, producing the tree-sitter column of the article's
comparison on the very same documents and deterministic edit traces.

Prerequisites (fixed hardware):

    pip install tree-sitter tree-sitter-c-sharp

Usage:

    python3 tools/incremental-bench/treesitter_arm.py \
        --sizes 100,500,2000,5000 --reps 10 --out bench-fixed-hw/ts.csv

The document corpus and the edit traces replicate the C# benchmark
exactly (same file list and ordering, same deterministic position
schedule and seeds), so the tree-sitter rows are directly comparable to
the `realex`-style corpus documents at the parse level, which only
tree-sitter supports today.
"""

import argparse
import csv
import math
import random
import sys
import time
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parents[2]
SOURCE_DIRS = (
    "src/DevelApp.StepParser",
    "src/DevelApp.StepLexer",
    "src/DevelApp.StepParser.Tests",
    "src/DevelApp.StepLexer.Tests",
)
BLOCK_PASTE_LINES = 20
EDIT_TYPES = ("CharInsert", "WordCompletion", "LineInsertion", "BlockPaste")


def build_corpus(lines):
    """Replicate RealCSharp.Doc: files whole, round-robined across the
    four source directories, path-sorted within each, cut at exactly
    `lines` lines."""
    per_dir = []
    for d in SOURCE_DIRS:
        files = sorted((REPO_ROOT / d).rglob("*.cs"), key=lambda p: str(p))
        if files:
            per_dir.append(files)
    ordered = []
    i = 0
    while True:
        appended = False
        for files in per_dir:
            if i < len(files):
                ordered.append(files[i])
                appended = True
        if not appended:
            break
        i += 1
    doc = []
    for path in ordered:
        if len(doc) >= lines:
            break
        with path.open(encoding="utf-8", errors="replace") as handle:
            for line in handle:
                if len(doc) >= lines:
                    break
                doc.append(line.rstrip("\r\n"))
            doc.append("")  # separator between concatenated files
    if len(doc) < lines:
        raise SystemExit(f"corpus exhausted at {len(doc)} lines < {lines}")
    doc = doc[:lines]
    return "\n".join(doc) + "\n"


def make_edit(edit_type, offset):
    if edit_type == "CharInsert":
        return "x"
    if edit_type == "WordCompletion":
        return "bc"
    if edit_type == "LineInsertion":
        return "v999 = 1 + 2\n"
    if edit_type == "BlockPaste":
        return "".join(f"v9{k} = 3 + 4\n" for k in range(BLOCK_PASTE_LINES))
    raise ValueError(edit_type)


def single_edit(text, edit_type, relative_position):
    target = int(len(text) * relative_position)
    offset = max(0, min(len(text) - 1, target))
    return offset, make_edit(edit_type, offset)


def sequential_edits(doc, count, seed):
    """Replicate Traces.Sequential (same schedule and seed, offset
    recomputed against the evolving text)."""
    edits = []
    text = doc
    rng = random.Random(seed)
    for i in range(count):
        rel = 0.10 + 0.80 * (0.5 + 0.5 * math.sin(seed + i * 1.7)) + (rng.random() - 0.5) * 0.05
        rel = min(0.90, max(0.10, rel))
        target = int(len(text) * rel)
        offset = max(0, min(len(text) - 1, target))
        insert = make_edit("CharInsert", offset)
        edits.append((offset, insert))
        text = text[:offset] + insert + text[offset:]
    return edits


def load_language():
    try:
        import tree_sitter_c_sharp
        try:
            from tree_sitter import Language
            return Language(tree_sitter_c_sharp.language())
        except TypeError:  # older binding style
            return tree_sitter_c_sharp.language()
    except Exception as exc:  # pragma: no cover
        raise SystemExit(
            f"Could not load tree-sitter C# grammar: {exc}\n"
            "Install with: pip install tree-sitter tree-sitter-c-sharp"
        ) from exc


class Timer:
    """perf_counter-based, sub-millisecond resolution."""

    def __enter__(self):
        self.start = time.perf_counter()
        return self

    def __exit__(self, *exc):
        self.ms = (time.perf_counter() - self.start) * 1000.0


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--sizes", default="100,500,2000,5000")
    parser.add_argument("--reps", type=int, default=10)
    parser.add_argument("--out", required=True, help="Output CSV path (ts.csv)")
    args = parser.parse_args()

    import tree_sitter

    language = load_language()
    parser_ts = tree_sitter.Parser(language)

    out_path = Path(args.out)
    out_path.parent.mkdir(parents=True, exist_ok=True)
    write_header = not out_path.exists() or out_path.stat().st_size == 0
    with out_path.open("a", newline="", encoding="utf-8") as handle:
        writer = csv.writer(handle)
        if write_header:
            writer.writerow(["scenario", "size", "arm", "type", "rep",
                             "editIndex", "offset", "insertLength", "editMs"])
        for size in [int(s) for s in args.sizes.split(",")]:
            doc = build_corpus(size)
            source = doc.encode("utf-8")

            # Edit-case bytes precomputed once (same for all arms/rows).
            for rep in range(1, args.reps + 1):
                # Full reparse with a fresh parser state per edit type.
                for edit_type in EDIT_TYPES:
                    offset, insert = single_edit(doc, edit_type, 0.5)
                    new_source = source[:offset] + insert.encode() + source[offset:]
                    with Timer() as t:
                        tree = parser_ts.parse(new_source)
                        _ = tree.root_node
                    writer.writerow(["ts", size, "full", edit_type, rep, -1,
                                     offset, len(insert), round(t.ms, 3)])
                handle.flush()

                # Incremental edit parse: apply byte edit to the tree,
                # reparse incrementally, per the 20-edit CharInsert trace.
                tree = parser_ts.parse(source)
                edited = source
                for i, (offset, insert) in enumerate(sequential_edits(doc, 20, size)):
                    Ins = insert.encode()
                    start_byte, old_end_byte, new_end_byte = offset, offset, offset + len(Ins)
                    start_point_row = edited[:offset].count(b"\n")
                    start_point_col = offset - (edited.rfind(b"\n", 0, offset) + 1)
                    new_source2 = edited[:offset] + Ins + edited[offset:]
                    tree.edit(start_byte, old_end_byte, new_end_byte,
                              (start_point_row, start_point_col),
                              (start_point_row, start_point_col),
                              (start_point_row, start_point_col + len(Ins)))
                    with Timer() as t:
                        tree2 = parser_ts.parse(new_source2, tree)
                        _ = tree2.root_node
                    writer.writerow(["ts", size, "inc", "CharInsert", rep, i,
                                     offset, len(Ins), round(t.ms, 3)])
                    tree = tree2
                    edited = new_source2
                handle.flush()
            print(f"ts: size {size} done", flush=True)

    print(f"wrote {out_path}")


if __name__ == "__main__":
    sys.exit(main())
