# Incremental parsing measurements (issue #101)

- Machine: `99dc36b4-80a9-4b5c-8c83-dba28ebc3ecc`, Debian GNU/Linux 13 (trixie) (X64), 1 core(s), .NET 8.0.34
- Recorded: 2026-11-19T05:49:08Z (reps: 3, seed: 101, args: `--scenario realex --sizes 100,500,2000,5000 --reps 3 --out /tmp/bench`)

## Per-edit latency, p50 / p95 (ms)

| Size | Edit type | spi p50 (p95) | full p50 (p95) | lex p50 (p95) | spi/full |
|---|---|---|---|---|---|
| 100 | CharInsert | 4.7 (12.2) | 10.3 (13.8) | 0.2 (0.3) | 0.45x |
| 100 | WordCompletion | 4.6 (10.0) | 9.8 (10.0) | 0.2 (0.3) | 0.47x |
| 100 | LineInsertion | 9.7 (19.0) | 14.5 (19.0) | 0.2 (0.3) | 0.67x |
| 100 | BlockPaste | 16.2 (26.4) | 20.7 (25.1) | 0.7 (0.7) | 0.78x |
| 500 | CharInsert | 43.6 (47.1) | 53.7 (65.9) | 0.5 (0.7) | 0.81x |
| 500 | WordCompletion | 46.5 (47.8) | 58.0 (76.0) | 0.5 (0.8) | 0.80x |
| 500 | LineInsertion | 41.2 (99.3) | 81.3 (104.1) | 0.6 (0.9) | 0.51x |
| 500 | BlockPaste | 105.2 (220.7) | 118.8 (142.7) | 1.3 (1.9) | 0.89x |
| 2000 | CharInsert | 192.2 (219.1) | 263.3 (283.8) | 1.7 (2.7) | 0.73x |
| 2000 | WordCompletion | 148.8 (213.4) | 177.7 (272.0) | 1.8 (4.0) | 0.84x |
| 2000 | LineInsertion | 116.3 (171.8) | 134.1 (182.1) | 1.4 (2.3) | 0.87x |
| 2000 | BlockPaste | 113.4 (160.0) | 132.5 (201.2) | 1.5 (2.8) | 0.86x |
| 5000 | CharInsert | 174.4 (547.4) | 298.1 (743.8) | 4.7 (11.0) | 0.58x |
| 5000 | WordCompletion | 195.3 (547.2) | 253.8 (696.5) | 4.8 (8.1) | 0.77x |
| 5000 | LineInsertion | 336.3 (991.0) | 479.1 (674.4) | 4.4 (7.6) | 0.70x |
| 5000 | BlockPaste | 297.5 (1320.6) | 375.2 (1385.9) | 4.3 (8.8) | 0.79x |

## Size scaling (log-log exponent, p50 latency vs lines)

Exponent 1.0 = linear in document size; O(log n) resume behavior would
push the incremental arm toward 0.

| Arm | Exponent |
|---|---|
| spi | 0.89 |
| full | 0.89 |
| lex | 0.76 |

## Memory overhead (allocated bytes per timed edit, p50)

| Size | spi | full | lex |
|---|---|---|---|
| 100 | 6.2 MB | 6.1 MB | 0.1 MB |
| 500 | 31.2 MB | 29.8 MB | 0.5 MB |
| 2000 | 127.7 MB | 122.2 MB | 1.8 MB |
| 5000 | 309.9 MB | 296.6 MB | 4.5 MB |

NOTE: allocations, not retention: the engine as shipped also retains
parse-context symbol-table state across parses on one engine instance
(see the engine issue linked from the README); sequential-edit SPI
latency grows across a session in fixed-hardware long runs.

## Edit size vs latency correlation (Pearson, inserted bytes vs ms)

| Arm | Size | r |
|---|---|---|
| spi | 100 | 0.63 |
| full | 100 | 0.81 |
| lex | 100 | 0.98 |
| spi | 500 | 0.58 |
| full | 500 | 0.77 |
| lex | 500 | 0.83 |
| spi | 2000 | -0.29 |
| full | 2000 | -0.35 |
| lex | 2000 | -0.04 |
| spi | 5000 | 0.33 |
| full | 5000 | 0.22 |
| lex | 5000 | 0.07 |

## Cache hit rates per edit type (warm 15-edit traces)

| Size | Edit type | parse cache hit | resume boundary/tokens (p50) | lexer reuse (p50) | parse steps (p50) |
|---|---|---|---|---|---|
| 100 | CharInsert | 1.00 (n=15) | 0.46 | 0.9878 | 439 |
| 100 | WordCompletion | 1.00 (n=15) | 0.46 | 0.9877 | 438 |
| 100 | LineInsertion | 1.00 (n=15) | 0.55 | 0.9866 | 269 |
| 100 | BlockPaste | 1.00 (n=15) | 0.17 | 0.9928 | 584 |
| 500 | CharInsert | 1.00 (n=15) | 0.00 | 0.9973 | 4004 |
| 500 | WordCompletion | 1.00 (n=15) | 0.00 | 0.9975 | 4003 |
| 500 | LineInsertion | 1.00 (n=15) | 0.00 | 0.9976 | 4042 |
| 500 | BlockPaste | 1.00 (n=15) | 0.00 | 0.9976 | 775 |

## Cold-start curve (first 20 edits after document open, per rep)

| Edit # | spi p50 (ms) | full p50 (ms) |
|---|---|---|
| 1 | 47.1 | 60.5 |
| 2 | 46.0 | 65.2 |
| 3 | 49.8 | 62.3 |
| 4 | 55.4 | 60.9 |
| 5 | 55.9 | 62.3 |
| 6 | 64.1 | 63.8 |
| 7 | 60.0 | 62.1 |
| 8 | 68.9 | 60.8 |
| 9 | 32.4 | 60.0 |
| 10 | 77.5 | 60.3 |
| 11 | 72.6 | 59.7 |
| 12 | 80.8 | 61.7 |
| 13 | 85.6 | 60.9 |
| 14 | 93.1 | 60.0 |
| 15 | 93.8 | 62.0 |
| 16 | 130.7 | 62.8 |
| 17 | 106.6 | 60.8 |
| 18 | 95.4 | 60.9 |
| 19 | 100.2 | 63.9 |
| 20 | 71.4 | 64.4 |

## Real C# at the lexer level (CSharp10 grammar, GreedyLongestMatch)

| Size (lines) | cold full lex (ms) | tokens | edit type | suffix re-lex p50 (ms) | lexer reuse p50 |
|---|---|---|---|---|---|
| 100 | 38.7 | 316 | CharInsert | 0.8 | 0.9244 |
| 100 | 38.7 | 316 | WordCompletion | 0.7 | 0.9244 |
| 100 | 38.7 | 316 | LineInsertion | 1.2 | 0.9244 |
| 100 | 38.7 | 316 | BlockPaste | 10.6 | 0.9244 |
| 500 | 169.1 | 2080 | CharInsert | 0.9 | 0.9919 |
| 500 | 169.1 | 2080 | WordCompletion | 0.9 | 0.9919 |
| 500 | 169.1 | 2080 | LineInsertion | 1.6 | 0.9919 |
| 500 | 169.1 | 2080 | BlockPaste | 14.6 | 0.9919 |
| 2000 | 599.7 | 5050 | CharInsert | 2.7 | 0.9925 |
| 2000 | 599.7 | 5050 | WordCompletion | 2.1 | 0.9925 |
| 2000 | 599.7 | 5050 | LineInsertion | 1.3 | 0.9925 |
| 2000 | 599.7 | 5050 | BlockPaste | 5.2 | 0.9925 |
| 5000 | 243.3 | 6149 | CharInsert | 0.6 | 1.0000 |
| 5000 | 243.3 | 6149 | WordCompletion | 0.5 | 1.0000 |
| 5000 | 243.3 | 6149 | LineInsertion | 0.5 | 1.0000 |
| 5000 | 243.3 | 6149 | BlockPaste | 0.5 | 1.0000 |

## Correctness guards

- Cross-arm/cold-reference guards: 312/312 ok, 0 mismatch.
