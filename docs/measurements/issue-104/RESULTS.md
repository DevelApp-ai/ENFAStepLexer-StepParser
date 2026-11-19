# Issue #104 — measured results (two-phase-regex article)

Measured on CI (GitHub Actions `ubuntu-latest`, .NET 8.0.34, X64, 4 cores,
2026-11-19) by the `article-measurements` job of `.github/workflows/ci.yml`;
raw artifacts: `article-measurements` run artifact (`regex-conformance/` and
`issue-104/`). Harnesses: `RegexFeatureConformanceTests` +
`Data/regex-conformance-corpus.tsv` (conformance) and
`--measure-issue104` (`RegexOverheadBenchmarks` / `Issue104Measurement`, overhead).

## 1. Feature conformance vs .NET Regex (65 corpus cases, 26 feature tags)

Reference = `System.Text.RegularExpressions.Regex` with `\A(?:pattern)`;
engine = single-rule greedy `StepLexer` (what a grammar token rule compiles to).
Full per-case data: `regex-conformance-cases.csv`; summary:

| Feature | Cases | Pass | Support |
|---|---:|---:|---:|
| literal, char-class, dot, escape-shorthand, escape-hex, escape-control, unicode-class (incl. non-ASCII `é`), quantifier-greedy, quantifier-bounded, quantifier-possessive, alternation (prefix-free), group (capturing + non-capturing), inline-modifier `(?i)`, atomic-group `(?>)`, quoted-literal `\Q..\E`, comment-group `(?#..)` | 53 | 53 | **100%** |
| quantifier-lazy | 2 | 1 | 50% — lazy is normalized to "exactly the minimum" (no backtracking); `a+?b` on `aab` diverges |
| alternation-order | 1 | 0 | 0% — engine takes the **longest** match (`foo\|foobar` → `foobar`), .NET takes the first alternative |
| anchor (`^` `$`), lookaround (all 4 forms), backreference, named group, scoped modifier, word boundary `\b` | 12 | 0 | **0%** — rejected at compile time (forward-only NFA); `\b` compiles as a literal quirk |
| **OVERALL** | **65** | **52** | **80%** |

Article correction: the claimed "lookahead/lookbehind 95%, capture groups 98%,
backreferences 85%" are **not supported by measurement**. Measured: lookaround
and backreferences are 0% (compile-time rejected — the unsupported list the
article must carry), capture groups are 100% at match-semantics level (the
lexer exposes no capture extraction), anchors are 0%. The engine is a
forward-only NFA by design (issue #129); the article's compatibility table
must be replaced with this measured table.

## 2. Benign-pattern overhead vs .NET Regex (median of 10 reps after 3 warmup)

Identical deterministic inputs per class; engine paths: **phase1** =
`Phase1_LexicalScan` (raw greedy-NFA scan per position — the direct analogue
of a traditional regex match loop), **step** = full `Initialize`+`Step` loop.
Match counts are identical for engine and .NET per class.

| Pattern class | Input chars | Phase1 (ms) | Step (ms) | .NET (ms) | Phase1 overhead | Step overhead | Phase1 alloc vs .NET | Step alloc vs .NET |
|---|---:|---:|---:|---:|---:|---:|---:|---:|
| simple-literal (`/hello/`) | 12,000 | 0.771 | 6.936 | 0.276 | +179% | +2411% | +49.6% | +741% |
| character-class (`/[a-z]+/`) | 39,297 | 3.315 | 36.043 | 1.424 | +133% | +2431% | −6.1% | +710% |
| basic-quantifier (`/[0-9]{1,5}/`) | 58,829 | 1.608 | 19.016 | 1.043 | +54% | +1723% | +40.6% | +710% |
| complex (`/(value\|name)[0-9]* [0-9]+/`) | 27,990 | 0.775 | 24.908 | 0.300 | +158% | +8205% | +196% | +1527% |

Article correction: the claimed "+8–20% overhead per pattern class" and
"<10% memory increase" are **not supported by measurement**. Measured on this
runner, the phase-1 scan runs at ~1.5–2.8x .NET Regex (median overhead +54% to
+179% per class), and the full step pipeline at ~18–83x (path/step machinery
dominates; see `docs/BENCHMARKING.md`). Allocation follows the same shape
(phase-1: −6% to +196%; step: +710% to +1527%). RE2 has no .NET binding in
this environment, so the article's RE2 column remains an external-baseline
item; the .NET column must be replaced with the measured values above.

## 3. Case study

The "email validation, 1M+/day, zero ReDoS incidents over 6 months, 99.99%
uptime" case study cannot be reproduced from this repository (no production
workload). Per issue #104 it must be relabeled **illustrative** article-side
unless a real pilot is run.

## Reproducing

```bash
REGEX_CONFORMANCE_OUT=out/regex-conformance \
  dotnet test src/DevelApp.StepLexer.Tests --filter FullyQualifiedName~RegexFeatureConformanceTests
dotnet run -c Release --project src/DevelApp.Benchmarks -- --measure-issue104 out/issue-104
```
