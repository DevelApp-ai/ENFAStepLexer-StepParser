# Issue #104 measurement harness — two-phase-regex article

Supports `articles/two-phase-regex-architecture/content/main.md`
(measurement backlog tracked in DevelApp-ai/ENFAStepLexer-StepParser#104).

## What is measured

1. **Feature conformance** (`Feature Compatibility Analysis` table):
   `src/DevelApp.StepLexer.Tests/RegexFeatureConformanceTests.cs` runs the
   committed corpus `src/DevelApp.StepLexer.Tests/Data/regex-conformance-corpus.tsv`
   (feature, pattern, input) through the two-phase engine (single-rule greedy
   `StepLexer`, i.e. exactly what a grammar token rule compiles to) and through
   .NET `Regex` as the reference (`\A(?:pattern)`), diffs match/no-match and
   matched text per case, and computes per-feature pass rates.
   - The test runs in the normal `dotnet test` pipeline (CI gate): core
     features (literals, char classes, dot, escapes, Unicode classes, greedy
     and bounded quantifiers, alternation, groups, inline modifiers, atomic
     groups, `\Q...\E`, `(?#...)`) must pass 100%; anchors, lookaround,
     backreferences, named groups and scoped modifiers are pinned as
     compile-time rejections (forward-only NFA) and report as 0% support —
     the "unsupported features must be listed" outcome.
   - With `REGEX_CONFORMANCE_OUT=<dir>` set, the test also writes
     `regex-conformance-cases.csv` and `regex-feature-summary.csv`.
   - Lazy quantifiers (normalized to "exactly the minimum") and
     alternation order (engine takes the longest match) can diverge from
     .NET's leftmost-first backtracking; those tags are measured without a
     100% floor.
2. **Benign-pattern overhead** (`Performance Comparison on Benign Inputs`
   table): `src/DevelApp.Benchmarks/RegexOverheadBenchmarks.cs` +
   `Issue104Measurement.cs` compare per article pattern class (simple
   literal, character class, basic quantifier, complex) a single-rule greedy
   `StepLexer` against `Regex(RegexOptions.Compiled).Matches` on identical
   generated inputs; reports medians, overhead %, and allocation ratio
   (`GC.GetAllocatedBytesForCurrentThread`) — this recomputes the article's
   "+8–20% per class" and "<10% memory increase" claims. RE2 has no .NET
   binding here, so the reference is .NET Regex; the RE2 column of the
   article remains an external-baseline item.

## How to run

```bash
# Conformance suite with CSV output
REGEX_CONFORMANCE_OUT=out/regex-conformance \
  dotnet test src/DevelApp.StepLexer.Tests --filter FullyQualifiedName~RegexFeatureConformanceTests

# Overhead + allocation measurement
dotnet run -c Release --project src/DevelApp.Benchmarks -- --measure-issue104 out/issue-104

# BenchmarkDotNet variant (manual, longer)
dotnet run -c Release --project src/DevelApp.Benchmarks -- --filter '*RegexOverheadBenchmarks*' -j short
```

## CI

The `article-measurements` job in `.github/workflows/ci.yml` runs both on
every PR and push to main and uploads the `article-measurements` artifact
(CSVs, `issue-104-summary.json`, `RESULTS.md`). Measured values transcribed
from CI runs are recorded in `RESULTS.md` next to this README.

## Case study (article-side)

The "email validation, 6 months, 99.99% uptime" case study cannot be
reproduced from this repo (no production workload); per the issue it must be
relabeled as illustrative in the article unless a pilot workload is run —
article-side edit, tracked in #104.
