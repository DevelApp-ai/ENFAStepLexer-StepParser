# ReDoS Immunity Measurements (issue #103)

Committed measurement data backing the ReDoS-immunity test suite
(`src/DevelApp.ReDos.Tests`) and the article table claims of the
two-phase regex architecture article.

## How to regenerate

```
dotnet test src/DevelApp.ReDos.Tests/DevelApp.ReDos.Tests.csproj \
  --filter "FullyQualifiedName~RedosMeasurementReportTests"
```

The report test writes `redos-immunity-report.json` next to the test
assembly; copy it here and update this README with the fresh run.
The CI suite (`RedosImmunityTests`, `LinearScalingTests`) enforces the
same properties continuously on every build.

## Measurement run

- Generated: 2026-11-15T02:47:08Z
- Engine under test: DevelApp.StepLexer 1.0.0.0
- Baselines: .NET 8.0.34 `System.Text.RegularExpressions` backtracking engine, and `RegexOptions.NonBacktracking` (the in-box linear-time engine)
- Environment: Debian GNU/Linux 13 (trixie), 1 logical processor, invariant globalization
- Timeouts: 10,000 ms hard per-case timeout for StepLexer runs; 500 ms match timeout for the backtracking baseline
- Raw data: `report-2026-11-15.json`

## Article and corpus patterns, identical adversarial inputs

StepLexer times are the full tokenization of 100 KB of adversarial
input. Every article/corpus pattern contains groups, alternation or
anchors, which the forward-only matcher does not support: it rejects
them at position 0, in microseconds, and terminates. That fail-fast
rejection is the immunity - there is no backtracking machinery to
attack.

| Pattern | StepLexer 100 KB | .NET NonBacktracking 100 KB | .NET backtracking (baseline length) |
|---|---|---|---|
| `(a+)+` | 0.008 ms (fail fast, no match) | 0.021 ms | 0.022 ms @ 1 KB (matches prefix) |
| `(a*)*b` | 0.007 ms (fail fast) | 1.944 ms | **> 500 ms timeout** @ 1 KB |
| `(a|a)*` | 0.013 ms (fail fast) | 0.005 ms | 0.084 ms @ 1 KB (matches prefix) |
| `(.*a){10}` | 0.031 ms (fail fast) | 2.360 ms | **> 500 ms timeout** @ 100 KB |
| `.*(?:.*=.*)` (Cloudflare-style) | 0.005 ms (fail fast) | 1.754 ms | **> 500 ms timeout** @ 100 KB |
| `^([\w\-.]+)*@([\w\-.]+)$` (SO-style) | 0.007 ms (fail fast) | 2.007 ms | **> 500 ms timeout** @ 1 KB |
| `(a+)+$` (Davis) | 0.005 ms (fail fast) | 3.240 ms | **> 500 ms timeout** @ 1 KB |
| `(a|aa)+$` (Davis) | 0.023 ms (fail fast) | 3.625 ms | **> 500 ms timeout** @ 1 KB |
| `([a-zA-Z]+)*$` (Davis) | 0.005 ms (fail fast) | 3.108 ms | **> 500 ms timeout** @ 1 KB |
| `(x+x+)+y` (Davis) | 0.026 ms (fail fast) | 1.711 ms | **> 500 ms timeout** @ 1 KB |
| `(a+)+b` (Davis) | 0.063 ms (fail fast) | 1.672 ms | **> 500 ms timeout** @ 1 KB |
| `(a|b|ab)*c` (Davis) | 0.030 ms (fail fast) | 1.691 ms | **> 500 ms timeout** @ 100 KB |

## Honest-measurement findings

1. **Immunity mechanism is fail-fast, not fast matching.** The shipped
   two-phase matcher does not support groups, alternation or anchors
   (issue #83): every article/corpus pattern above is rejected at
   position 0 in microseconds. The article's ReDoS table should say
   "rejected instantly (unsupported construct)" rather than presenting
   a match time for these patterns.
2. **Unanchored `(a+)+` and `(a|a)*` are not ReDoS patterns at all.**
   In every engine they match the `a`-run prefix instantly. The blowup
   requires anchoring, which the Davis variants `(a+)+$` and `(a|aa)+$`
   demonstrate: both time out the traditional engine.
3. **The traditional engine still blows up on modern .NET.** The .NET 8
   backtracking engine (with its auto-atomicization optimizations)
   exceeds the 500 ms match timeout on 10 of the 12 corpus patterns on
   1-100 KB inputs, while the StepLexer and the non-backtracking
   reference complete every input in single-digit milliseconds.

## Linear scaling of the supported subset (1 KB -&gt; 100 KB)

Supported-subset analogues of the pathological families, minimum of 3
repetitions per length, least-squares fit of time against length:

| Pattern | Slope | R² | t(100 KB)/t(16 KB) | StepLexer 100 KB |
|---|---|---|---|---|
| `[a]+` full run | 1.72 ns/byte | 0.9994 | 3.14 | 0.236 ms |
| `[a]*b` full-scan no-match | 1.34 ns/byte | 0.9999 | 2.97 | 0.177 ms |
| `[ab]*` full run | 2.25 ns/byte | 0.9950 | 3.93 | 0.281 ms |
| `[^x]*` negated full run | 12.74 ns/byte | 0.9998 | 5.56 | 1.639 ms |
| `.*b` dot full-scan no-match | 2.94 ns/byte | 0.9998 | 4.25 | 0.370 ms |

Linear scaling predicts a growth ratio of 6.25 (= 102400/16384); a
quadratic regression would show 39. All measured curves are linear
(R² ≥ 0.995, growth ≤ 5.6). The worst-case pattern is the negated
class `[^x]*` at ~12.7 ns/byte - the per-code-point predicate cost -
and the CI suite enforces R² ≥ 0.90 plus growth ≤ 12.0 on every build.

## Known characteristic: many-token streams

`[a-z]+[0-9]` over an `ab1`-repeated stream emits one token per 3 bytes.
Matching stays forward-only, but the per-token line/column computation
scans from the input start (`CalculateLineColumn`), so token count drives
a quadratic term in total time: 3.2 ms at 1 KB (341 tokens), 76.6 ms at
10 KB (3,413 tokens). This is a tokenization-overhead characteristic,
not a ReDoS exposure (no exponential matching exists, and a no-match
terminates the run); it is measured at capped lengths only and pinned
here as the follow-up optimization opportunity (incremental
line/column tracking).
