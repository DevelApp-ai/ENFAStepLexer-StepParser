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
The CI suite (`RedosImmunityTests`, `RedosImmunityGridTests`,
`LinearScalingTests`) enforces the same properties continuously on
every build.

## Measurement run

- Generated: 2026-11-15T03:32:29Z
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
| `(a+)+` | 0.006 ms (fail fast, no match) | 0.028 ms | 0.02 ms @ 1 KB (matches prefix) |
| `(a*)*b` | 0.006 ms (fail fast) | 1.846 ms | **> 500 ms timeout** @ 1 KB |
| `(a|a)*` | 0.022 ms (fail fast) | 0.007 ms | 0.08 ms @ 1 KB (matches prefix) |
| `(.*a){10}` | 0.015 ms (fail fast) | 2.166 ms | **> 500 ms timeout** @ 100 KB |
| `.*(?:.*=.*)` (Cloudflare-style) | 0.005 ms (fail fast) | 1.562 ms | **> 500 ms timeout** @ 100 KB |
| `^([\w\-.]+)*@([\w\-.]+)$` (SO-style) | 0.011 ms (fail fast) | 1.926 ms | **> 500 ms timeout** @ 1 KB |
| `(a+)+$` (Davis) | 0.011 ms (fail fast) | 3.220 ms | **> 500 ms timeout** @ 1 KB |
| `(a|aa)+$` (Davis) | 0.005 ms (fail fast) | 3.045 ms | **> 500 ms timeout** @ 1 KB |
| `([a-zA-Z]+)*$` (Davis) | 0.009 ms (fail fast) | 3.184 ms | **> 500 ms timeout** @ 1 KB |
| `(x+x+)+y` (Davis) | 0.020 ms (fail fast) | 1.581 ms | **> 500 ms timeout** @ 1 KB |
| `(a+)+b` (Davis) | 0.010 ms (fail fast) | 1.605 ms | **> 500 ms timeout** @ 1 KB |
| `(a|b|ab)*c` (Davis) | 0.007 ms (fail fast) | 1.534 ms | **> 500 ms timeout** @ 100 KB |
| `(a+)*$` (Davis) | 0.005 ms (fail fast) | 3.139 ms | **> 500 ms timeout** @ 1 KB |
| `(\w+\s?)*$` (Davis) | 0.009 ms (fail fast) | 3.103 ms | **> 500 ms timeout** @ 1 KB |

## Honest-measurement findings

1. **Immunity mechanism is fail-fast, not fast matching.** The shipped
   two-phase matcher does not support groups, alternation or anchors
   (issue #83): every article/corpus pattern above is rejected at
   position 0 in microseconds. The article's ReDoS table should say
   "rejected instantly (unsupported construct)" rather than presenting
   a match time for these patterns.
2. **Unanchored `(a+)+` and `(a|a)*` are not ReDoS patterns at all.** In
   every engine they match the `a`-run prefix instantly. The blowup
   requires anchoring, which the Davis variants `(a+)+$` and `(a|aa)+$`
   demonstrate: both time out the traditional engine.
3. **The traditional engine still blows up on modern .NET.** The .NET 8
   backtracking engine (with its auto-atomicization optimizations)
   exceeds the 500 ms match timeout on 12 of the 14 corpus patterns on
   1-100 KB inputs, while the StepLexer and the non-backtracking
   reference complete every input in single-digit milliseconds.
4. **Overlapping multi-rule lexers have superlinear path overhead.**
   With rules whose matches overlap (`/[ab]+/` + `/a+/` + `/b+/` on an
   `ab` stream), every position spawns paths that carry different
   token-type sequences, so path merging cannot fold them. Matching
   stays forward-only and a no-match terminates the run (no classical
   ReDoS), but the surviving-path bookkeeping grows superlinearly:
   ~16 ms at 256 B, ~259 ms at 4 KB, ~3.4 s at 16 KB. The suite asserts
   completion at capped lengths only and flags the parser layer's hard
   active-path budget (10 paths) as the mitigation model for a
   lexer-layer follow-up.
5. **`\Q...\E` support is narrower than PCRE2.** The shipped matcher
   evaluates a quoted literal only as the sole atom (a trailing
   quantifier is accepted); composed with other atoms (`[b]+\Q(a)+\E`,
   `\Qa\E[a]*`) the rule fails fast at position 0. That fail-fast
   bound is itself the immunity for this composition; `\Q..\E` is also
   PCRE2-only syntax with no .NET baseline equivalent.

## Linear scaling of the supported subset (1 KB -> 100 KB)

Supported-subset analogues of the pathological families - now
including multibyte UTF-8 (Danish æ/ø/å), Unicode-property and
case-folding shapes - minimum of 5 repetitions per length,
least-squares fit of time against length:

| Pattern | Slope | R² | t(100 KB)/t(16 KB) | StepLexer 100 KB |
|---|---|---|---|---|
| `[a]+` full run | 1.1 ns/byte | 0.969 | 3.01 | 0.245 ms |
| `[a]*b` full-scan no-match | 1.1 ns/byte | 0.999 | 2.88 | 0.208 ms |
| `[ab]*` full run | 1.5 ns/byte | 0.999 | 3.01 | 0.294 ms |
| `[^x]*` negated full run | 11.5 ns/byte | 0.9998 | 5.33 | 1.558 ms |
| `.*b` dot full-scan no-match | 2.0 ns/byte | 0.961 | 5.50 | 0.294 ms |
| `\p{L}+` full run | 121 ns/byte | 0.960 | 3.67 | 16.56 ms |
| `[a-zæøå]+` Danish multibyte run | 2.4 ns/byte | 0.984 | 2.92 | 0.305 ms |
| `[^x]*` negated Danish run | 8.4 ns/byte | 0.999 | 5.65 | 0.942 ms |
| `(?i)[a-z]+` upper-case run | 23.4 ns/byte | 0.9999 | 5.98 | 2.512 ms |

Linear scaling predicts a growth ratio of 6.25 (= 102400/16384); a
quadratic regression would show 39. All measured curves are linear
(R² ≥ 0.96, growth ≤ 6.0). The most expensive pattern per byte is the
Unicode-property scan `\p{L}+` at ~121 ns/byte (the per-code-point
property-table lookup), followed by the negated class `[^x]*` at
~11-12 ns/byte; the CI suite enforces R² ≥ 0.90 plus growth ≤ 12.0 on
every build.

## Known characteristic: many-token streams

`[a-z]+[0-9]` over an `ab1`-repeated stream emits one token per 3 bytes.
Matching stays forward-only, but the per-token line/column computation
scans from the input start (`CalculateLineColumn`), so token count drives
a quadratic term in total time: 71.6 ms at 10 KB (3,413 tokens). This is
a tokenization-overhead characteristic, not a ReDoS exposure (no
exponential matching exists, and a no-match terminates the run); it is
measured at capped lengths only and pinned here as the follow-up
optimization opportunity (incremental line/column tracking).
