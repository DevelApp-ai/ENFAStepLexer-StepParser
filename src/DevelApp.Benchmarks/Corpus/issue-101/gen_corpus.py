#!/usr/bin/env python3
"""Deterministic generator for the issue #101 (incremental-parsing-ides article)
edit-latency corpus: C#-like source files at 100/500/2000/5000 lines plus four
deterministic edit traces (char insert, word completion, line insertion, block
paste). Run from this directory:

    python3 gen_corpus.py

Outputs (committed):
    cs-100.txt cs-500.txt cs-2000.txt cs-5000.txt
    traces/char-insert.json traces/word-completion.json
    traces/line-insert.json traces/block-paste.json

Each trace is a JSON list of {"offset", "deleteLength", "insertText"} edits
applied in order. Offsets are character offsets into the *original* file and
are recomputed by the harness against the evolving text (the harness clamps
them), so traces stay deterministic regardless of earlier insertions.
"""
import json
import os
import random

SIZES = [100, 500, 2000, 5000]
TRACE_EDITS = 40
SEED = 20261104

HEADER = [
    "using System;",
    "using System.Collections.Generic;",
    "",
    "namespace BenchCorpus",
    "{",
]


def class_lines(idx):
    return [
        f"    // class C{idx}",
        f"    public class C{idx}",
        "    {",
        f"        private int _field{idx};",
        f'        private const string Name{idx} = "corpus{idx}";',
        f"        public int Compute{idx}(int a, int b) {{",
        f"            var x = a + b * 2;",
        f"            if (x > 10) {{ return x - 1; }}",
        f"            var s = \"value\" + {idx};",
        f"            return x + s.Length;",
        "        }",
        "    }",
        "",
    ]


def generate(lines_target):
    lines = list(HEADER)
    idx = 0
    while len(lines) < lines_target - 1:
        lines.extend(class_lines(idx))
        idx += 1
    lines.append("}")
    # pad/trim to exactly lines_target lines with simple comment lines
    while len(lines) < lines_target:
        lines.insert(len(lines) - 1, f"    // padding line {len(lines)}")
    del lines[lines_target:]
    return "\n".join(lines) + "\n"


def make_trace(kind, text, edits=TRACE_EDITS, seed=SEED):
    rng = random.Random(seed)
    traces = []
    if kind == "char-insert":
        for _ in range(edits):
            traces.append({"offset": rng.randrange(0, len(text)),
                           "deleteLength": 0, "insertText": "x"})
    elif kind == "word-completion":
        for _ in range(edits):
            traces.append({"offset": rng.randrange(0, len(text)),
                           "deleteLength": 0, "insertText": "Result"})
    elif kind == "line-insert":
        for _ in range(edits):
            # insert at a line boundary when possible
            pos = rng.randrange(0, len(text))
            nl = text.rfind("\n", 0, pos)
            if nl >= 0:
                pos = nl + 1
            traces.append({"offset": pos, "deleteLength": 0,
                           "insertText": "        var inserted = 1;\n"})
    elif kind == "block-paste":
        block = "".join(f"        // pasted line {i}\n        var p{i} = {i};\n"
                        for i in range(6))
        for _ in range(edits):
            pos = rng.randrange(0, len(text))
            nl = text.rfind("\n", 0, pos)
            if nl >= 0:
                pos = nl + 1
            traces.append({"offset": pos, "deleteLength": 0, "insertText": block})
    else:
        raise ValueError(kind)
    return traces


def main():
    here = os.path.dirname(os.path.abspath(__file__))
    os.makedirs(os.path.join(here, "traces"), exist_ok=True)
    reference = generate(5000)
    for size in SIZES:
        text = generate(size)
        path = os.path.join(here, f"cs-{size}.txt")
        with open(path, "w", encoding="utf-8", newline="\n") as f:
            f.write(text)
        print(f"wrote {path}: {size} lines, {len(text)} chars")
    for kind in ["char-insert", "word-completion", "line-insert", "block-paste"]:
        trace = make_trace(kind, reference)
        path = os.path.join(here, "traces", f"{kind}.json")
        with open(path, "w", encoding="utf-8", newline="\n") as f:
            json.dump(trace, f, indent=1)
        print(f"wrote {path}: {len(trace)} edits")


if __name__ == "__main__":
    main()
