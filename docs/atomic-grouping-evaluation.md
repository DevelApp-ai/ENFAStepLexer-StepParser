---
layout: default
title: Atomic Grouping Evaluation
---

# Atomic Grouping Evaluation

Issue #40 (Phase 3): *Evaluate atomic grouping support within forward-parsing constraints.*

## Background

Atomic grouping (`(?>...)`) and possessive quantifiers (`a++`, `a*+`, `a?+`) are
PCRE2 constructs that disable backtracking *inside* the group. In a
backtracking regex engine, `(?>ab|a)` matched against `"a"` fails: the group
commits to the `ab` alternative, consumes only `"a"`, and cannot retry the
shorter alternative. The construct exists purely as a performance/semantic
control over *backtracking* behavior.

## Why the constraint matters

The StepLexer is a **forward-parsing, multi-path matcher**:

1. Every lexer path consumes input strictly left-to-right and never gives
   input back once a step has committed to it.
2. Ambiguity is not resolved by backtracking over consumed input but by
   carrying multiple parallel paths (GLR-style) and resolving them via
   longest-match, priority, and ambiguity resolution rules.
3. Once a token is emitted, its characters are never re-examined by another
   path.

Consequently, every match the StepLexer produces is **already atomic**: there
is no backtracking whose scope an atomic group could restrict. Atomic
semantics are not merely *compatible* with forward parsing — they are an
inherent property of it.

## Evaluation outcome: supported by unwrapping

Because atomic grouping is a no-op semantically, the correct support strategy
is **normalization during pattern preprocessing**
(`StepLexer.Matching.cs`, `PreprocessRegexPattern`):

* `(?>` opening markers and their matching closing `)` are **stripped**, keeping
  the group contents. Nested groups are tracked with a paren stack so only the
  atomic group's own parentheses are removed.
* Possessive quantifier markers are **converted to their greedy forms**:
  `X++` → `X+`, `X*+` → `X*`, `X?+` → `X?`. The `+` immediately following a
  quantifier character is dropped (outside character classes and `\Q...\E`
  literals).

This matches the semanti
cs a forward-parsing engine would produce anyway,
while keeping rule patterns that use these constructs loadable and matchable
instead of being rejected as unsupported syntax.

### Examples

| Pattern | Preprocessed to | Behavior |
|---|---|---|
| `/(?>abc)/` | `abc` | literal match of `abc` |
| `/(?>abc)(?# tail)/` | `abc` | comments still stripped |
| `/\Qab\E++/` | `\Qab\E+` | greedy quoted-literal repetition |
| `/a*+/` | `a*` | still a metachar pattern → conservative no-match (same as `a*` today) |
| `/(?>a\|b)/` | `a\|b` | contains unsupported metachar → conservative no-match |

### Limitations (deliberate, conservative)

* The simplified matcher supports a subset of PCRE2 (literals, common char
  classes, `\p{...}`, `\Q...\E`, quantifiers on those). An atomic group whose
  *contents* use unsupported constructs (alternation, nested quantified
  groups, backreferences) still yields **no match** rather than a wrong match,
  exactly as the same contents would without the atomic wrapper.
* Possessive quantifiers on single literal characters (`/a++/`) reduce to
  greedy quantifiers, which are themselves outside the literal-fallback fast
  path; such patterns conservatively do not match. Possessive repetition is
  fully supported on quoted literals (`\Q...\E`) and Unicode property escapes,
  which are the constructs that support quantifiers at all.

## Conclusion

Atomic grouping is **supported** within the forward-parsing constraints, via
normalization, with identical observable behavior to a backtracking engine
for every pattern the simplified matcher can otherwise handle — because a
never-backtracking engine makes every group atomic by construction.
