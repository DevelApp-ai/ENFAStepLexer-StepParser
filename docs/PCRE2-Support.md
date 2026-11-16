---
layout: default
title: PCRE2 Regex Features Support
---

# PCRE2 Regex Features Support in ENFAStepLexer-StepParser

## Overview

This document describes the PCRE2 (Perl Compatible Regular Expression) features supported by the ENFAStepLexer-StepParser implementation based on the @DevelApp/enfaparser project.

## Supported PCRE2 Features

> **⚠️ Scope of this document (issue #106).** There are two different layers,
> and several features below apply to only one of them:
>
> 1. **Grammar-level production rules** — alternation (`<start> ::= <A> | <B>`),
>    inline groups `( ... )` and EBNF optionality/repetition are fully
>    supported by the grammar loader (`SplitAlternatives` and EBNF
>    normalization). Use this layer to express choice.
> 2. **Token-rule regex patterns** (`<word> ::= /pattern/`) — compiled once
>    at rule-add time and matched at runtime by a forward matcher that never
>    backtracks. It supports literal text, character classes (with ranges
>    and escapes), shorthand classes, `.`, `\p{...}`/`\P{...}` with
>    quantifiers, inline modifiers, `(?#...)` comments, `\Q...\E`, and —
>    since issue #129 — **groups and alternation**: such patterns are
>    compiled internally to a small NFA and matched by longest-match
>    forward simulation (linear in the input, no backtracking), so grammar
>    authors can write ordinary regexes. Constructs that cannot be
>    evaluated without backtracking/lookaround — anchors (`^`, `$`, `\A`,
>    `\Z`, `\z`, `\G`, `\b`, `\B`), backreferences (`\1`, `\k<name>`),
>    lookaround, named groups and scoped modifiers — are rejected with a
>    compile-time validation error (surfaced by the grammar loader at load
>    time, and queryable via `StepLexer.TryValidateRulePattern`); the rule
>    keeps a conservative **no-match** as a backstop (input reported as
>    unexpected via `LX1001`) — never a wrong match.
>
> The pinned behavior for both layers is in
> `src/DevelApp.StepParser.Tests/IssueReproTests.cs`
> (`Issue106_TokenRuleRegexSubsetTests`, `Issue129_TokenRuleGroupAlternationTests`)
> and `src/DevelApp.StepLexer.Tests/RegexNfaGroupAlternationTests.cs`.

### ✅ Basic Constructs
- **Literal characters**: Regular characters match themselves
- **Character classes**: `[abc]`, `[^abc]` (character sets and negated sets)
- **Character ranges**: `[a-z]`, `[0-9]` (ranges within character classes)
- **Quantifiers**: 
  - `*` (zero or more)
  - `+` (one or more) 
  - `?` (zero or one)
  - `{n}` (exactly n)
  - `{n,m}` (between n and m)
  - `{n,}` (n or more)
- **Lazy/greedy modifiers**: `*?`, `+?`, `??`, `{n,m}?` and `*+`, `++`, etc.

### ✅ Character Class Shortcuts
- `\w` - Word characters (letters, digits, underscore)
- `\W` - Non-word characters
- `\d` - Digits (0-9)
- `\D` - Non-digits
- `\s` - Whitespace characters
- `\S` - Non-whitespace characters
- `\l` - Letters (custom extension, not standard PCRE)
- `\L` - Non-letters (custom extension, not standard PCRE)

### ❌ Anchors — not evaluated in token-rule patterns (issue #106)
- `^` - Start of line
- `$` - End of line
- `\A` - Start of string
- `\Z` - End of string (before final newline if present)
- `\z` - Absolute end of string
- `\G` - Continue from previous match position
- `\b` - Word boundary
- `\B` - Non-word boundary

**Verified behavior:** the token-rule matcher rejects `^` and `$` as
unsupported literals (`IsUnsupportedLiteral`), so a pattern such as
`/^foo/` never matches. Escape-form anchors are unknown escapes to the
matcher and also produce a conservative no-match.

### ✅ Unicode and Extended Character Support (NEW)
- `\x{FFFF}` - Unicode code points
- `\xFF` - Hexadecimal character codes
- `\cA-\cZ` - Control characters
- `\p{property}` - Unicode property classes (general categories, scripts, blocks, binary properties)
- `\P{property}` - Negated Unicode property classes
- `\R` - Any Unicode newline sequence

### ✅ POSIX Character Classes (NEW)
- `[:alpha:]` - Alphabetic characters
- `[:alnum:]` - Alphanumeric characters
- `[:digit:]` - Digit characters
- `[:lower:]` - Lowercase letters
- `[:upper:]` - Uppercase letters
- `[:space:]` - Whitespace characters
- `[:blank:]` 
- Space and tab
- `[:punct:]` - Punctuation characters
- `[:xdigit:]` - Hexadecimal digits
- `[:cntrl:]` - Control characters
- `[:graph:]` - Graphical characters
- `[:print:]` - Printable characters

### ✅ Escape Sequences
- `\0` - Null character
- `\a` - Alert (bell)
- `\e` - Escape
- `\f` - Form feed
- `\n` - Newline
- `\r` - Carriage return
- `\t` - Horizontal tab
- `\v` - Vertical tab
- `\b` - Backspace (in character classes)
- `\\` - Literal backslash
- Standard escaped characters: `\"`, `\[`, `\]`, `\(`, `\)`, `\{`, `\}`, `\|`, `\.`, `\^`, `\$`, `\?`, `\*`, `\+`

### ✅ Capturing / Non-Capturing Groups in token-rule patterns (issue #129), ❌ assertions
- `()` - Capturing groups — **supported** in token-rule patterns (captures
  are ignored; the lexer needs only the matched text span)
- `(?:...)` - Non-capturing groups — **supported**
- `(?<name>...)` - Named capturing groups — not supported (the name syntax
  is rejected, same as lookaround)
- `(?=...)` - Positive lookahead — not supported
- `(?!...)` - Negative lookahead — not supported
- `(?<=...)` - Positive lookbehind — not supported
- `(?<!...)` - Negative lookbehind — not supported

**Verified behavior:** groups (capturing and `(?:...)`, arbitrarily nested,
with alternation and quantifiers) are compiled at rule-add time into a
forward-only NFA and matched by longest-match simulation
(`StepLexer.RegexNfa.cs`, linear in input length — no backtracking), e.g.
`/\*([^*]|\*+[^*/])*\*+\//` matches C-style block comments. Lookaround,
named groups and scoped modifiers need backtracking/lookaround state and
are rejected at compile time with a specific validation error (reported at
grammar load; the rule keeps a conservative no-match). Grammar-level
alternatives remain available as well — see
`Issue106_TokenRuleRegexSubsetTests` and
`Issue129_TokenRuleGroupAlternationTests`.

### ❌ Back References — not supported in token-rule patterns (issue #106/#129)
- `\1`, `\2`, etc. - Numbered back references
- `\k<name>` - Named back references

**Verified behavior:** backreferences are rejected at compile time with a
"backreference is not supported" validation error (surfaced at grammar load
via `StepLexer.TryValidateRulePattern`), and the rule keeps a conservative
no-match. Back references require backtracking against earlier match state,
which the never-backtracking forward matcher cannot provide.

### Alternation — ✅ at grammar level AND in token-rule patterns (issue #129)
- **Grammar level: supported.** `<start> ::= <A> | <B>` and inline groups
  like `( <A> | <B> )` are expanded into one production per alternative by
  the grammar loader.
- **Token-rule patterns: supported since issue #129.** `/a|b/` compiles to
  a forward-only NFA with an alternation branch and matches normally.
  Same-named token rules also remain an alternative way to give one token
  type several spellings (the lexer's per-path ambiguity resolution handles
  multiple rules matching the same position).

### ✅ Special Characters
- `.` - Any character except newline

### ✅ Unicode Properties
- **Supported**: Full Unicode property validation and runtime matching (`\p{L}`, `\P{N}`, `\p{Script}`, `\p{Block}`, binary properties, etc.) with optional quantifiers (`+`, `*`, `?`, `{n}`, `{n,}`, `{n,m}`)
- **Property names**: Loose matching per Unicode standard — case-insensitive, ignoring `_`, `-` and spaces, with `Is`/`In` block prefixes; long general category names map to short codes (e.g. `Lowercase_Letter` → `Ll`)
- **Runtime matching**: General categories (including supplementary planes via full code point evaluation), scripts (primary ranges), blocks (common set) and commonly used binary properties
- **Known approximations**: Script and some binary property tests use simplified primary ranges; full coverage is tracked by the "Full Unicode ICU integration" roadmap item (normalization is now served by `IcuUnicodeIntegration`; property range coverage remains simplified). Property names that valid
ate but have no runtime implementation simply never match.

## ❌ Unsupported PCRE2 Features

### Advanced Features Not Implemented

1. **Conditional Patterns**: `(?(condition)yes|no)`
   - **Reasoning**: Adds significant complexity to state machine logic

2. **Recursive Patterns**: `(?R)`, `(?&name)`, `(?1)`
   - **Reasoning**: Requires stack-based recursion support in the StepLexer architecture

3. **Subroutines**: `(?1)`, `(?-1)`, `(?+1)`
   - **Reasoning**: Similar to recursive patterns, needs subroutine call mechanisms

4. **Advanced Escape Sequences**:
   - `\K` (keep everything up to this point)
   - `\X` (extended grapheme cluster)
   - **Reasoning**: These require advanced text processing beyond basic character matching

5. **Callouts and Code**: `(?C)`, `(?{...})`
   - **Reasoning**: Would require embedding executable code in patterns, significant security and complexity concerns

6. **Variable-Length Lookbehind**
   - **Reasoning**: Current implementation assumes fixed-length lookbehind for efficiency

> **Note:** Atomic grouping (`(?>...)`), possessive quantifiers (`*+`, `++`, `?+`),
> inline modifiers (`(?i)` etc.), quoted literal sequences, and `(?#...)`
> comments are now **supported** - see the re-evaluated atomic grouping section
> below and `docs/atomic-grouping-evaluation.md`.

## Excluded Features (By Design)

The following features are intentionally excluded from the StepLexer-StepParser system due to architectural design decisions that prioritize performance, predictability, and maintainability.

### ✅ Atomic Grouping Support (Supported via Normalization)

**Pattern Examples:** `(?>atomic)`, possessive quantifiers such as `/\Qab\E++/`

**How Supported:**
- **Never-backtracking architecture makes every group atomic by construction**: The StepLexer consumes input strictly forward and never gives consumed input back, so the semantic guarantee of an atomic group is inherently satisfied
- **Normalization during preprocessing**: `(?>` markers and thei
r matching `)` are stripped while contents are kept; possessive markers (`++`, `*+`, `?+`) are reduced to their greedy forms
- **Nested groups tracked with a paren stack**: only the atomic group's own parentheses are removed

**Scope:**
- Since issue #129, alternation and nested quantified groups inside an
  unwrapped atomic group are supported (they compile to the forward-only
  NFA); only constructs needing backtracking/lookaround (anchors,
  backreferences, lookaround, scoped modifiers) remain rejected

See `docs/atomic-grouping-evaluation.md` for the full evaluation.

### ❌ Recursive Pattern Support

**Pattern Examples:** `(?R)`, `(?&name)`, `(?1)`, `(?-2)`

**Why Excluded:**
- **Adds unnecessary complexity to lexer architecture**: Recursive patterns require stack management and dynamic state tracking that significantly complicates the lexer's streamlined architecture
- **Better handled by grammar-based StepParser for recursive constructs**: The StepParser is specifically designed to handle recursive language constructs through production rules, making it the more appropriate layer for recursion
- **Would compromise predictable memory usage and performance**: Recursive patterns can lead to unbounded memory usage and unpredictable performance characteristics, violating the StepLexer's performance guarantees

**Alternative Approaches:**
- **Use StepParser with production rules for recursive language constructs**: Define recursive patterns using grammar production rules like `<expr> ::= <expr> '+' <expr> | <number>`
- **Implement balanced parsing through grammar rules rather than regex recursion**: Use grammar-based approaches for balanced constructs like parentheses matching or nested structures
- **Hierarchical pattern decomposition**: Break complex recursive patterns into simpler, non-recursive components that can be combined at the parser level

**Technical Benefits:**
- Maintains linear memory usage characteristics
- Enables proper error recovery and reporting for recursive constructs
- Provides better debugging and analysis capab
ilities through explicit grammar structure

**Example Alternative Pattern:**

Instead of recursive regex:
```regex
(?R)  # Match nested parentheses recursively
```

Use StepParser grammar:
```grammar
<balanced> ::= '(' <content> ')'
<content>  ::= <balanced> <content> | <char> <content> | ε
<char>     ::= /[^()]/
```

## Architecture Notes

### vNext Architecture Compatibility

The current implementation maintains compatibility with the planned vNext architecture by:

1. **Modular Design**: Clear separation between tokenizer, parser, and state machine components
2. **Extensible Transitions**: New transition types can be easily added to the `RegexTransitionType` enum
3. **Factory Pattern**: New functionality can be added through factory extensions
4. **Step-wise Processing**: The tokenizer processes patterns step-by-step, enabling future step-based optimizations

### Performance Considerations

- **Memory Efficiency**: The ENFA structure allows for efficient memory usage compared to traditional NFA/DFA approaches
- **Compilation Speed**: Pattern compilation is fast due to the direct ENFA construction
- **Runtime Performance**: Match performance depends on pattern complexity and backtracking requirements

## Implementation Quality

### Code Quality
- ✅ Compiles successfully on .NET 8.0
- ✅ Modular, extensible architecture
- ✅ Comprehensive error handling with descriptive error messages
- ✅ Type-safe enum-based transition system

### Testing Status
- ✅ Token-rule pattern subset pinned in `Issue106_TokenRuleRegexSubsetTests` and `Issue129_TokenRuleGroupAlternationTests` (grammar-level + NFA-level group/alternation pins; no-match pins for anchors/backreferences/lookaround) plus `RegexNfaGroupAlternationTests` (lexer NFA behavior and validation API)
- ⚠️ Basic compilation testing completed
- ❌ Performance benchmarking needed
- ❌ Edge case validation needed

## Future Enhancement Roadmap

### Phase 1 (Immediate)
1. Add comprehensive unit tests
2. Fix nullable reference warnings
3. Implement basic Unicode property validation
4. Add pattern compilation validation

### Phase 2 (Short-term)
1. Implement inline modifiers (`(?i)`, `(?m)`, etc.)
2. Add `\Q...\E` literal text support
3. Implement comment support `(?#...)`
4. Add more comp
rehensive error reporting

### Phase 3 (Long-term)
1. Consider atomic grouping support
2. Evaluate recursive pattern feasibility
3. Advanced Unicode support with ICU integration
4. Performance optimization and benchmarking

## Conclusion

The ENFAStepLexer-StepParser provides robust support for the most commonly
used PCRE2 features **in token-rule patterns** — literal text, character
classes, quantifiers, shorthand classes, Unicode properties, and (since
issue #129) groups and alternation compiled internally to a forward-only
NFA — while grammar-level production rules additionally offer alternatives,
inline groups and EBNF constructs (issue #106). Constructs that need
backtracking or lookaround (anchors, backreferences, lookaround, scoped
modifiers) are rejected with a compile-time validation error reported at
grammar load and otherwise fail conservatively (no match, `LX1001`
diagnostic) rather than matching wrongly, so correctness is never silently
compromised.