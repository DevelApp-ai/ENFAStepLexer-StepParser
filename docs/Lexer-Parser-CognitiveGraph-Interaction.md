---
layout: default
title: StepLexer, StepParser and CognitiveGraph — Ambiguity, Grammar Switching, Extensions and Grammar Learning
---

# StepLexer, StepParser and CognitiveGraph: How They Interact

This report explains, end to end, how the three subsystems of
ENFAStepLexer-StepParser cooperate: **DevelApp.StepLexer** (zero-copy,
forward-only ENFA tokenization), **DevelApp.StepParser** (GLR-style
grammar parsing) and the **CognitiveGraph** (the packed semantic graph
built *while* parsing). It focuses on four topics:

1. How ambiguous input flows through the pipeline (lexical ambiguity →
   syntactic ambiguity → packed graph representations).
2. How the system shifts between grammars and grammar contexts at runtime.
3. How grammars are extended (inheritance, overlays, semantic actions,
   metavariable tokens, refactoring operations).
4. What "learning a new grammar" looks like today: the authoring /
   validation / feedback loop, plus the ML-assist roadmap that is
   explicitly designed but disabled by default.

All statements are grounded in the current `main` source; file names are
given inline.

## 1. The Pipeline at a Glance

```
grammar file ──► GrammarLoader (inheritance, overlays, precedence, projections)
                     │
                     ├──► TokenRules ──► StepLexer (rules, context stack)
                     └──► ProductionRules ──► StepParser (_grammar)
                                              │
source bytes ──► StepLexer ──► StepToken[] ──► StepParser ──► ParserPaths
                     │                                   │
              LexerPath[] (parallel)          CognitiveGraphBuilder
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
regex-pattern compilation is an escape like `\x{41}`, which might be a
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

## 3. StepParser Under Ambiguity (GLR-style)

### 3.1 Every action is explored

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
  further. Without this, a complete prefix would be forced to consume the
  next token and destroy itself (this is documented in code against
  issue #80).

Multiple possible actions clone the path (`remainingActions > 1`), exactly
one action reuses the path in place — avoiding an O(stack) clone in the
common case.

### 3.2 Ambiguity containment in the parser

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

### 3.3 Context-sensitive rules and grammar switching

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
composed grammar, evaluated per path. Because context is part of the
lexer path key and the parser merge key, two regions in different
contexts never cross-contaminate.

For *static* composition of different grammars, see extensions below.

## 4. CognitiveGraph Construction During Ambiguity

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

`CognitiveGraphAnalytics` (CognitiveGraphAnalytics.cs) then traverses the
graph **as a DAG** (shared children visited once, fan-out = distinct
children) and reports depth, fan-out, ambiguity rate (`IsAmbiguous` or
>1 packed node), source coverage, structural hotspots and a composite
0..1 complexity score. This is the feedback signal for grammar quality.

## 5. Extensions: How Gramars Are Extended

### 5.1 Grammar inheritance (`Inherits:`)

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

### 5.2 Runtime overlays

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

### 5.3 Other extension points

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

## 6. Learning a New Grammar

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

### 6.1 The ML-assist roadmap (designed, off by default)

Issue #58 / MlAssist.cs define four *candidate* learned behaviors, all
**disabled by default** and observation-free until a prototype lands:

- `LearnedRulePrioritization` — learned ordering of token-rule
  evaluation (sub-issue #74).
- `LearnedPathPruning` — learned pruning of doomed GLR paths (sub-issue
  #75).
- `HotspotStrategySelection` — hotspot-guided parsing-strategy choice.
- `TokenReusePrediction` — learned token reuse for
  `RealTimeParserSession`.

The contract (`MlGuardRailTests`-enforced): every feature routes through
`MlAssistOptions.IsEnabled`, enabling requires a model version for
reproducibility, and `DisableAll` / the `DEVELAPP_STEPML_DISABLE_ALL`
environment variable is a runtime escape hatch. ML may never change token
boundaries, parse trees or emitted diagnostics — so "learning a grammar"
in the statistical sense will influence *performance decisions*, never
parsing *results*.

## 7. Summary of the Interaction Contract

| Concern | StepLexer | StepParser | CognitiveGraph |
|---|---|---|---|
| Ambiguity | Parallel `LexerPath` clones + `MergePaths` fingerprints; `SplittableToken` alternatives resolved in Phase 2 (longest match) | Parallel `ParserPath`s; shift/reduce/rest all explored; chained reductions; 128-bit stack-signature merging; budgets (32 intra-step, top-10 final) | Packed nodes per alternative derivation; `AmbiguousRoot` (type 300) with `ParseCount` when several parses complete |
| Grammar switching | Context-gated token rules per path | Context-gated production rules + preconditions; `SwitchGrammarContext` pushes a context | Node properties record the context each node was built in |
| Extensions | New token rules via overlays/inheritance; PCRE2 constructs normalized in preprocessing | New production rules, semantic action handlers, projections, refactoring ops | Semantic actions extend graph content at reduce time |
| Learning a grammar | Rules compile into the lexer on load | Rules compile into `_grammar` on load | Analytics + ambiguity rate give authoring feedback; ML-assist gates future learned optimization |

## 8. Pointers

- Lexer phases and splittable tokens: `src/DevelApp.StepLexer/StepLexer.Phases.cs`, `StepLexer.AmbiguityResolution.cs`, `SplittableToken.cs`
- GLR engine: `src/DevelApp.StepParser/StepParser.GLR.cs`
- Graph building: `src/DevelApp.StepParser/StepParser.GraphBuilding.cs`, `GraphNodeRef.cs`
- Inheritance / overlays: `GrammarLoader.Inheritance.cs`, `GrammarLoader.Overlay.cs`, `StepParserEngine.Overlay.cs`
- Analytics: `CognitiveGraphAnalytics.cs`
- Incremental editing: `RealTimeParserSession.cs`
- ML guard rails: `MlAssist.cs`
- Guides: `docs/StepLexer.md`, `docs/StepParser.md`, `docs/Grammar_File_Creation_Guide.md`, `docs/atomic-grouping-evaluation.md`
