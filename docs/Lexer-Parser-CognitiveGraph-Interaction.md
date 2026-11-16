---
layout: default
title: StepLexer, StepParser and CognitiveGraph — Ambiguity, Zero-Copy, Grammar Switching, Extensions and Grammar Learning
---

# StepLexer, StepParser and CognitiveGraph: How They Interact

This report explains, end to end, how the three subsystems of
ENFAStepLexer-StepParser cooperate: **DevelApp.StepLexer** (zero-copy,
forward-only ENFA tokenization), **DevelApp.StepParser** (GLR-style
grammar parsing) and the **CognitiveGraph** (the packed semantic graph
built *while* parsing). It focuses on five topics:

1. How ambiguous input flows through the pipeline (lexical ambiguity →
   syntactic ambiguity → packed graph representations).
2. How zero-copy is designed to work, and **how the engine → parser →
   CognitiveGraph chain was restored to byte-native zero-copy**
   (issue #85 comment, fixed by issue #87).
3. How the system shifts between grammars and grammar contexts at runtime.
4. How grammars are extended: inheritance, overlays, and the designed
   extension-keyed external grammar importers (`.g4`, `.y`, `.l`, ...).
5. What "learning a new grammar" looks like today: the authoring /
   validation / feedback loop, plus the ML-assist features — two of the
   four designed behaviors now have in-engine hooks and an offline
   training pipeline, all still **disabled by default** (issue #58).

All statements are grounded in the current source; file names are
given inline. This revision re-evaluates the report against the
converged byte-native design (issues #86/#87) and the ML-assist
training pipeline (issue #58 sub-issues #92/#93): the former
"where the chain breaks" sections are now historical.

## 1. The Pipeline at a Glance

```
grammar file ──► GrammarLoader (inheritance, overlays, precedence, projections)
                     │
                     ├──► TokenRules ──► StepLexer (compiled rules, context stack)
                     └──► ProductionRules ──► StepParser (_grammar)
                                              │
UTF-8 bytes ──► StepLexer.Initialize(ReadOnlyMemory<byte>) ──► StepToken[] (view-backed) ──► StepParser ──► ParserPaths
                     │                                              │
              LexerPath[] (parallel)                     CognitiveGraphBuilder
                                                          │
                                            Build(root, ReadOnlyMemory<byte> sourceBytes)
                                                          │
                                                  CognitiveGraph (V1/V2)
                                                          │
                                        CognitiveGraphAnalytics (report)
```

- The **lexer** never backtracks over its input. Ambiguity is handled by
  *cloning lexer paths*, not by rewinding.
- The **parser** never backtracks either; it carries *many parser paths in
  parallel* (GLR-style) and merges/prunes them per step.
- The **CognitiveGraph is built incrementally inside the parser's shift
  and reduce actions** — it is not a post-pass. Every shifted terminal and
  every reduced non-terminal is written to a graph builder immediately.
- The **input is UTF-8 bytes end to end** (issue #87): string callers
  enter through `Parse(string)`, which encodes once at the boundary;
  `ParseFile` and `Parse(ReadOnlyMemory<byte>)` hand the file's bytes
  straight to the lexer with no UTF-16 round-trip.

## 2. StepLexer Under Ambiguity

### 2.1 Single-position multiple matches → split paths

When more than one token rule matches at the current position,
`StepLexer.Step()` (StepLexer.Matching.cs) does not pick a winner. It
calls `ProcessMultipleMatches`, which **clones the current `LexerPath`
once per matching rule** and advances each clone independently. The
active-path frontier therefore carries all lexical interpretations
forward in lockstep.

Key mechanics:

- A `LexerPath` holds position, current context, and the tokens consumed
  so far. Cloning is cheap and the path set stays small because of merging
  (below).
- Rules are filtered by context *before* matching:
  `IsRuleApplicableInContext` accepts a rule when it has no context, when
  its context equals the path's current context, or when the context is
  anywhere in the context stack. This is how a token like `<QUOTE>` can
  exist in one region of a file and mean something else in another.

### 2.2 Path merging (ambiguity containment)

`MergePaths` (StepLexer.AmbiguityResolution.cs) deduplicates the frontier
each step. Two paths merge only when they share **position, context and
token-type sequence**. The token-type sequence is compared via an
incrementally maintained fingerprint (`LexerPath.GetTokenFingerprint`),
so the merge check is O(1) amortized per step instead of O(tokens).
Fingerprint collisions fall back to an exact sequence comparison, so
merging is correct, not merely fast.

### 2.3 Splittable tokens (within-token ambiguity)

Some tokens are ambiguous *inside their own text*. The classic case in
regex-pattern compilation is an escape like `\\x{41}`, which might be a
two-character escape prefix plus literal, or a full hex escape. In
`ScanEscapeSequence` (StepLexer.Phases.cs) the lexer creates a
`SplittableToken` and attaches **alternatives** via `Split(...)`. The
two-phase regex compiler then resolves it:

- **Phase 1 (`Phase1_LexicalScan`)**: fast scan, ambiguous tokens emitted
  with alternatives attached.
- **Phase 2 (`Phase2_Disambiguation`)**: each ambiguous token is resolved
  with `SelectBestAlternative` (current heuristic: longest match) and
  fed into ENFA state construction.

So there are two distinct ambiguity layers in the lexer: *across tokens*
(parallel paths, merged) and *within a token* (splittable alternatives,
disambiguated in phase 2).

## 3. Zero-Copy: How It Works and How It Was Restored

This section answers the issue #85 comment: *"how does the zero-copy
work. It seems like that has been broken slowing down StepLexer and
StepParser as file loaded is the zero-copy version that CognitiveGraph
works on."* The original audit confirmed the suspicion: the zero-copy
**primitives were intact inside the pattern-compilation side of
StepLexer**, but the **hot tokenization path and the engine → parser →
CognitiveGraph chain materialized strings**. That break has since been
fixed (issue #87): the chain is now byte-native end to end. This
section describes the restored design first, then the historical break
it replaced.

### 3.1 What zero-copy means here

`ZeroCopyStringView` (ZeroCopyStringView.cs) is a `readonly struct`
wrapping `(ReadOnlyMemory<byte> buffer, int start, int length)`:

- `Slice(start, length)` returns a **new view over the same buffer** —
  no bytes are copied, nothing is allocated beyond the struct itself.
- `AsSpan()` hands out a `ReadOnlySpan<byte>` over the window for
  byte-level comparison.
- `this[index]` indexes bytes directly.
- `ToString()` is explicitly marked *"expensive operation — avoid when
  possible"*: it is the one place a view materializes a managed string.

The design intent: input arrives once as `ReadOnlyMemory<byte>`
(UTF-8), the lexer walks it forward-only, and every token is a window
into that buffer. `StepLexer.Initialize(ReadOnlyMemory<byte>, ...)`
stores the memory without copying and seeds a single path at position 0.

### 3.2 The restored chain (issue #87)

- **Input retention**: `StepLexer.Initialize` keeps the whole input as
  `ReadOnlyMemory<byte>`; `ProcessPath` slices `_input.Span` per
  position without copying.
- **Precompiled token rules**: each `TokenRule` pattern is compiled once
  at `AddRule` time into a `CompiledTokenRule`
  (StepLexer.RuleCompilation.cs) — literal byte sequences, run matchers,
  quoted-literal repeats, Unicode-property and class-sequence atoms — so
  pattern parsing and preprocessing never run per position.
- **Byte-span matching**: all matchers in StepLexer.Matching.cs work
  against `ReadOnlySpan<byte>` (exact-byte `SequenceEqual` for
  literals, ASCII-range checks for the common classes). The only decode
  on the matching path is the case-insensitive literal fallback: a
  bounded decode that runs only after the exact-byte path fails.
- **View-backed tokens**: `StepToken` (StepToken.cs) carries a
  `ZeroCopyStringView` with a lazy `Value`; `TryGetValueView` exposes
  the raw bytes so consumers can scan without decoding. Tokens consumed
  structurally (parser matching, path bookkeeping) never pay a decode.
- **Byte-native engine API**: `Parse(ReadOnlyMemory<byte>)`,
  `ParseFile(path)` (a UTF-8 BOM is stripped once at this boundary),
  byte-native `Lex` and `ParseAndMerge` overloads
  (StepParserEngine.Parsing.cs). `Parse(string)` encodes exactly once at
  the boundary and delegates to the byte-native path.
- **Parser and graph over the buffer**: `StepParser.Initialize(tokens,
  ReadOnlyMemory<byte>)` stores `_sourceUtf8`; graph construction calls
  the CognitiveGraph package's byte-native
  `Build(rootOffset, ReadOnlyMemory<byte>)` (package 1.1.20), so the
  graph's source of record is the same input buffer. `Value` /
  `GetSourceText()` decode on demand. One deliberate note: the ambiguous
  root's `SourceLength` counts UTF-8 bytes (the buffer's unit of
  account), not UTF-16 characters.
- **Diagnostics decode lazily and bounded**: `BuildSourceDiagnostic`
  scans the UTF-8 bytes for line/column (codepoint-aware columns via
  `UTF8Utils`) and decodes only the one excerpt line.
- **Incremental sessions**: `RealTimeParserSession` re-lexes edited
  ranges through the same byte-based matchers.

The restoration is regression-tested by `Utf8NativePipelineTests`,
`ByteNativeParseTests` and `Utf8NativeTokenTests`: byte-identical token
streams and graphs versus the former string path, BOM handling, lazy
materialization and non-ASCII spans.

### 3.3 The historical break (issue #85 audit, superseded)

For the record, the pre-#87 pipeline that the audit traced: the only
engine entry point was `Parse(string)`, which re-encoded the whole
source to bytes; matchers called `Encoding.UTF8.GetString` per rule ×
position × path; `PreprocessRegexPattern` rebuilt pattern strings per
match attempt; `StepToken` carried a materialized `string Value`; the
parser stored a `string _sourceText` and built the graph over it; and
`RealTimeParserSession` kept `_source` as a string. The allocation rate
scaled with rules × positions × paths, which matched the observed
slowdown.

### 3.4 Design rules that carry forward

- **Convert once, at the boundary** — `Parse(string)` and `ParseFile`'s
  BOM handling are the only conversion points; the pipeline interior is
  byte-only.
- **Decode on demand, bounded** — diagnostics decode one line;
  `Value`/`GetSourceText()` decode only when user code reads them.
- **Views, not copies** — every subsystem (matchers, tokens, parser,
  graph) references windows into the single input buffer.

## 4. StepParser Under Ambiguity (GLR-style)

### 4.1 Every action is explored

For each parser path and each token, `ProcessParserPath`
(StepParser.GLR.cs) plans **all** available actions against the
unmutated path state:

- **Shift** — push the token (possible when any applicable rule mentions
  the token type in its right-hand side and passes context + precondition
  checks).
- **Reduce** — apply any production rule whose right-hand side matches the
  top of the stack (checked in reverse order against `StackTopFirst`).
  Reductions **chain within the same step**: a reduction can enable further
  reductions, and the loop runs until no pending path can reduce (bounded
  by `maxRounds = 2 * tokenCount + 16`).
- **Rest** — a path whose stack has collapsed to a single entry is kept
  unchanged as a candidate complete parse, in addition to shifting
  further. Without this, a complete prefix would be forced to consume
  the next token and destroy itself (this is documented in code against
  issue #80).

Multiple possible actions clone the path (`remainingActions > 1`), exactly
one action reuses the path in place — avoiding an O(stack) clone in the
common case.

### 4.2 Ambiguity containment in the parser

- **`MergeParserPaths`** deduplicates paths by a key of
  `TokenPosition : CurrentState : <128-bit stack signature>`. The stack
  signature is an order-sensitive hash cached on persistent stack nodes,
  so key generation is O(1) per path.
- **Path budgets**: at most `MaxIntraStepPaths = 32` transient paths per
  step, and `Step()` enforces the final budget (top 10 by score). Pruning
  orders by *token position first, then score* — deeper paths survive
  over cheap resting prefixes (issue #80).
- **Scoring**: each shift multiplies score by 0.95, each successful
  reduction by 1.1 — reductions are rewarded, sheer token consumption is
  lightly penalized.
- **Precedence/associativity** (`%precedence`, `%left`, `%right` in the
  grammar file) is carried on every production rule and attached to the
  reduced graph node, so consumers can resolve operator ambiguity
  deterministically after parsing.
- **End of input**: `FinalizeEndOfInputReductions` keeps reducing with
  the last token as lookahead stand-in until nothing more applies, so
  trailing reductions (e.g. the final `expr ::= expr + expr`) are not
  lost.

### 4.3 Context-sensitive rules and grammar switching

Both lexer and parser filter rules through the same hierarchical
`IContextStack`:

- Token rules: `<STRING_CONTENT[string]>` only matches while the
  `string` context is on the stack.
- Production rules: `<expression[function]>` only reduces when the
  context stack contains `function` (`IsRuleApplicableInContext`).
- Semantic actions can enter/exit contexts (`enter_context`,
  `exit_context`), and the engine exposes `SwitchGrammarContext` for
  multi-language files — pushing a context switches the effective rule
  set without reloading a grammar.

This is the primary mechanism for "shifting between grammars" at parse
time: it is not a grammar swap but a **context-gated rule subset** of one
composed grammar, evaluated per path. Because context is part of
the lexer path key and the parser merge key, two regions in different
contexts never cross-contaminate.

For *static* composition of different grammars, see extensions below.

## 5. CognitiveGraph Construction During Ambiguity

The parser writes the graph **as it shifts and reduces**
(StepParser.GLR.cs):

- **Shift** → `WriteSymbolNode` with node type 100 (terminal), carrying
  TokenType, TokenValue, Context and source location.
- **Reduce** → children are popped, a **packed node** is written per
  reduction (`WritePackedNode`), and a symbol node of type 200
  (non-terminal) references it, carrying RuleName, Context, Precedence and
  Associativity. Semantic actions run inline at this moment (errors are
  logged, parsing continues).
- **Multiple complete parses** → `GenerateCompleteCognitiveGraphs`
  (StepParser.GraphBuilding.cs) wraps every successful root in a packed
  node and adds an **AmbiguousRoot symbol node (type 300)** with
  `ParseCount` and `IsAmbiguous = true`. The ambiguity is therefore
  *preserved structurally* in the graph, not resolved away by the parser.

Note that the node values written here are view-backed (§3.2):
`TokenValue` comes from a `StepToken` whose text is a
`ZeroCopyStringView` into the UTF-8 input buffer, materialized only if
a consumer reads it; the graph itself is built over the byte buffer,
so the packed representation shares the same source of record as the
lexer and parser.

`CognitiveGraphAnalytics` (CognitiveGraphAnalytics.cs) then traverses the
graph **as a DAG** (shared children visited once, fan-out = distinct
children) and reports depth, fan-out, ambiguity rate (`IsAmbiguous` or
>1 packed node), source coverage, structural hotspots and a composite
0..1 complexity score. This is the feedback signal for grammar quality.

## 6. Extensions: How Grammars Are Extended

### 6.1 Grammar inheritance (`Inherits:`)

(GrammarLoader.Inheritance.cs)

- Base grammars are registered via `RegisterBaseGrammar` (validated at
  registration time) or resolved from built-ins (`antlr4_base`,
  `bison_base`).
- `ProcessInheritanceCore` resolves imports **transitively** (a base's
  own bases are merged into it first) and **detects cycles** with a
  visiting-set; grammars are keyed by name, anonymous ones by object
  identity.
- Merging is the overlay merge with `BaseWins` semantics *from the derived
  grammar's perspective*: derived rules override same-named base rules,
  base-only rules are inherited.
- `Inheritable: false` blocks a grammar from being used as a base, with a
  dedicated diagnostic code.

### 6.2 Runtime overlays

(GrammarLoader.Overlay.cs, StepParserEngine.Overlay.cs — issue #66)

- `ComposeGrammars(base, overlay, conflictResolution)` merges two
  already-parsed grammars into a **new** grammar; neither input is
  mutated. `OverlayWins` (default) lets an overlay inject or replace
  rules (e.g. adding Labyrinth pattern tokens to a language grammar);
  collisions are recorded in `GrammarMergeResult.Conflicts` and surfaced
  as `LastMergeResult` on the engine.
- `LoadGrammarWithOverlay` / `ApplyOverlay` reconfigure lexer and parser
  with the composite grammar in one step — this is the supported way to
  "extend" a language at runtime without authoring a derived file.
- Both sides of a composition may themselves use `Inherits:`; inheritance
  and overlay are the same generalized merge with different
  conflict-resolution polarity.

### 6.3 Extension-keyed grammar loading (the `.extension` importer design)

The TDS design document (docs/"ENFAStepLexer-StepParser Enhancements
TDS.docx", section on External Grammar Importers) specifies an
**extension-based, pluggable importer system** for `GrammarLoader`:

- **Purpose**: interoperability — translate grammars from other popular
  formats into StepParser's `GrammarDefinition` format.
- **Mechanism**: when loading a file, `GrammarLoader` inspects the file
  **extension** and invokes the matching importer:
  - `.g4` → `AntlrImporter` (ANTLR v4 grammars)
  - `.y` → `YaccImporter` (Yacc/Bison parser grammars)
  - `.l` → `FlexImporter` (Flex/Lex lexer grammars)
- Each importer is a dedicated parser producing a native
  `GrammarDefinition`. Once imported, that definition can be used **as
  the target of an `Extends:` directive** in another grammar file — the
  designed counterpart of `Inherits:`, where `Inherits` specifies the
  meta-grammar (how the file itself is parsed) and `Extends` composes the
  language being defined. A `%remove <Rule>` directive is designed to
  delete an inherited rule during extension.

**Current implementation status**: not yet implemented.
`GrammarLoader.LoadGrammar(filePath)` (GrammarLoader.cs) reads any
file's text and parses it as the native grammar format regardless of
extension; only `Inherits:` (and the runtime overlays above) exist.
`Extends:`, `%remove` and the importers are design-stage, together
with the required `Grammar_File_Creation_Guide.md` documentation
mandate the TDS attaches to them.

### 6.4 Other extension points

- **Semantic action handlers** (`ISemanticActionHandler`, registry via
  `RegisterActionHandler`) decouple graph construction from grammar text.
- **Projections** (`GrammarLoader.PrecedenceAndProjections`,
  `ExecuteProjection`) trigger semantic code on structural matches.
- **Metavariable/ellipsis pattern tokens** (issue #65) usable in any
  grammar's token rules.
- **Refactoring operations** (`RefactoringOperation`) declared with
  applicable contexts and preconditions, executed against the parsed
  graph.
- **Inline regex preprocessing** in the lexer accepts PCRE2 constructs
  that conflict with forward-only parsing — inline modifiers,
  `(?#...)` comments, atomic groups `(?>...)` and possessive
  quantifiers are normalized during preprocessing (the never-backtracking
  lexer is inherently atomic; see docs/atomic-grouping-evaluation.md).

## 7. Learning a New Grammar

"Learning a new grammar" currently means the **authoring, validation and
feedback loop**, not statistical learning:

1. **Author** the grammar in the declarative format (`Grammar:`,
   `TokenSplitter:`, token rules, production rules, optional
   `%precedence`/`%left`/`%right`, contexts, actions). The format is
   documented in docs/Grammar_File_Creation_Guide.md.
2. **Load** it with `LoadGrammarFromContent` / `LoadGrammar`. The
   `GrammarLoader` parses token and production rules, processes
   inheritance and overlays, builds precedence tables, and validates
   rule references — malformed grammars fail fast with
   `ENFA_GrammarBuild_Exception` and diagnostic codes, including
   inheritance cycles and non-inheritable bases.
3. **Configure** happens automatically on load: token rules are pushed
   into a fresh `StepLexer`, production rules into the `StepParser`'s
   `_grammar` list, default refactoring operations are registered.
4. **Parse** with `engine.Parse(...)` or `ParseMultipleFiles(...)` (V1/V2
   schema; V2 is optimized for massive graphs). Errors and warnings come
   back in `StepParsingResult`.
5. **Iterate with feedback**:
   - `RealTimeParserSession` provides incremental editing: `ApplyEdit`
     re-lexes only the affected range, reuses tokens outside it, bumps a
     version and re-parses — ideal for developing a grammar against
     sample inputs in an editor/IDE loop.
   - `CognitiveGraphAnalytics.Analyze(result)` quantifies the grammar's
     behavior: ambiguity rate and hotspots point at rules that generate
     excessive parallel parses; source coverage below 1.0 points at
     tokens the grammar fails to consume; complexity score tracks
     overall grammar health.

### 7.1 ML-assist: two features implemented (off by default), two still designed

Issue #58 / MlAssist.cs define four *candidate* learned behaviors, all
**disabled by default**. Two now have working in-engine hooks plus an
offline training pipeline (sub-issues #92/#93):

- **`LearnedRulePrioritization`** (sub-issue #74) — **implemented**:
  `LearnedRulePrioritizer` orders token-rule evaluation from learned
  weights. It is installed on the lexer only while
  `MlAssistOptions.IsEnabled` says so, and ordering alone cannot change
  the token stream — tests assert the emitted tokens are bit-identical
  with the feature on and off.
- **`LearnedPathPruning`** (sub-issue #75) — **implemented**:
  `LearnedPathPruner` consults a learned model before the parser spends
  budget on a GLR path and prunes the ones predicted doomed, under a
  safety budget with consultation counters; full GLR remains the default
  behavior with the gate off.
- **`HotspotStrategySelection`** — hotspot-guided parsing-strategy
  choice. Design-stage.
- **`TokenReusePrediction`** — learned token reuse for
  `RealTimeParserSession`. Design-stage.

The training pipeline (docs/ML-Assist.md, tools/ml-assist/) closes the
loop offline: label-capture hooks record which token rule matched at
each position (`StepLexer` rule-match labels) and which parser paths
survived (`StepParser` path labels, exposed via `MlLabelCapture`),
environment-gated corpus trace tests (`MlLabelTraceTests`) emit the
training corpus, a dependency-free trainer (`tools/ml-assist/train.py`,
Newton/IRLS logistic regression) produces versioned model files for the
prioritizer and pruner, and an evaluation harness
(`MlAssistEvaluationTests`) asserts **zero semantic change**: identical
graphs, diagnostics and token boundaries with ML-assist enabled versus
disabled.

The contract (`MlGuardRailTests`-enforced) is unchanged: every feature
routes through `MlAssistOptions.IsEnabled`, enabling requires a model
version for reproducibility, and `DisableAll` / the
`DEVELAPP_STEPML_DISABLE_ALL` environment variable is a runtime escape
hatch. ML may never change token boundaries, parse trees or emitted
diagnostics — so "learning a grammar" in the statistical sense will
influence *performance decisions*, never parsing *results*. With the
zero-copy chain restored (§3), `LearnedRulePrioritization` and
`LearnedPathPruning` plug into a hot loop that is actually cheap to
run.

## 8. Summary of the Interaction Contract

| Concern | StepLexer | StepParser | CognitiveGraph |
|---|---|---|---|
| Ambiguity | Parallel `LexerPath` clones + `MergePaths` fingerprints; `SplittableToken` alternatives resolved in Phase 2 (longest match) | Parallel `ParserPath`s; shift/reduce/rest all explored; chained reductions; 128-bit stack-signature merging; budgets (32 intra-step, top-10 final) | Packed nodes per alternative derivation; `AmbiguousRoot` (type 300) with `ParseCount` when several parses complete |
| Zero-copy | Byte-native: rules precompiled to `CompiledTokenRule`, matchers run on `ReadOnlySpan<byte>`, tokens carry `ZeroCopyStringView` with lazy `Value` | `StepParser.Initialize(tokens, ReadOnlyMemory<byte>)`; `_sourceUtf8` is the same input buffer | Built over the UTF-8 byte buffer (`Build(offset, ReadOnlyMemory<byte>)`); decode only on demand (diagnostics, `Value`, `GetSourceText()`) |
| Grammar switching | Context-gated token rules per path | Context-gated production rules + preconditions; `SwitchGrammarContext` pushes a context | Node properties record the context each node was built in |
| Extensions | New token rules via overlays/inheritance; PCRE2 constructs normalized in preprocessing | New production rules, semantic action handlers, projections, refactoring ops | Semantic actions extend graph content at reduce time |
| Learning a grammar | Rules compile into the lexer on load; ML-assist may reorder rule evaluation (never the token stream) | Rules compile into `_grammar` on load; ML-assist may prune doomed paths (never the parse result) | Analytics + ambiguity rate give authoring feedback; ML-assist gates learned optimization |

## 9. Pointers

- Zero-copy primitive: `src/DevelApp.StepLexer/ZeroCopyStringView.cs`
- Zero-copy chain (restored, issue #87): `src/DevelApp.StepLexer/StepLexer.RuleCompilation.cs` (compiled token rules), `src/DevelApp.StepLexer/StepLexer.Matching.cs` (byte-span matchers), `src/DevelApp.StepLexer/StepToken.cs` (view-backed tokens), `src/DevelApp.StepParser/StepParserEngine.Parsing.cs` (byte-native entry points), `src/DevelApp.StepParser/StepParser.cs` (`_sourceUtf8`), `src/DevelApp.StepParser/StepParser.GraphBuilding.cs` (byte-native `Build`)
- Byte-native regression tests: `Utf8NativePipelineTests`, `ByteNativeParseTests`, `Utf8NativeTokenTests`
- Lexer phases and splittable tokens: `src/DevelApp.StepLexer/StepLexer.Phases.cs`, `StepLexer.AmbiguityResolution.cs`, `SplittableToken.cs`
- GLR engine: `src/DevelApp.StepParser/StepParser.GLR.cs`
- Graph building: `src/DevelApp.StepParser/StepParser.GraphBuilding.cs`, `GraphNodeRef.cs`
- Inheritance / overlays: `GrammarLoader.Inheritance.cs`, `GrammarLoader.Overlay.cs`, `StepParserEngine.Overlay.cs`
- Extension importers (design): `docs/ENFAStepLexer-StepParser Enhancements TDS.docx` (External Grammar Importers section)
- Analytics: `CognitiveGraphAnalytics.cs`
- Incremental editing: `RealTimeParserSession.cs`
- ML guard rails: `MlAssist.cs`
- ML-assist training pipeline: `docs/ML-Assist.md`, `tools/ml-assist/` (trainer and README), `LearnedRulePrioritizer` / `LearnedPathPruner`, label capture `MlLabelCapture.cs` (both projects), tests `MlLabelTraceTests` / `MlAssistEvaluationTests`
- Guides: `docs/StepLexer.md`, `docs/StepParser.md`, `docs/Grammar_File_Creation_Guide.md`, `docs/atomic-grouping-evaluation.md`, `docs/ML-Assist.md`
