# Corpus feasibility sweep (issue #101): can a real grammar power the benchmark?

Before building the per-edit benchmark, every candidate grammar from the
Minotaur-Grammars corpus was checked for file-scale parseability. The
2026-11-18 triage (subprocess-isolated, 75 s hard cap per grammar) parsed
each of the 128 grammars' own `Examples.txt` with its grammar:

- **82 grammars**: timeout (>75 s) on their own examples; 4 more over 5 s.
- **41 grammars**: healthy on example-scale input.

Examples-scale health does **not** imply file-scale health. The follow-up
file-scale probes (dev container, this arc) applied the healthy grammars
to purpose-built file-scale documents:

| Corpus grammar | File-scale probe result | Consequence for the benchmark |
|---|---|---|
| JSON | **Host process abort** on a 100-line real document ("Rejected zero-width token rule `<characters>`" warning, then crash) | unusable; also filed with issue #157 |
| INI, Makefile, YAML | Greedy multi-line token rules collapse whole files to 1-17 tokens; parse "succeeds" trivially, work O(1) | worthless as a parse workload |
| Natural language (Icelandic, Faroese, Bokmål, Irish) | Repeat-example documents yield tokens=0, success=false | unusable |
| CSharp10 | Healthy at the **lexer** level (the engine pins real-file lexing separately); parse layer catastrophically slow at file scale (647 productions; 12.4 s for a 34-token snippet, failure; triage-level) | C# usable only for the lexer arm of the benchmark |
| GitHubActionsWorkflow | 611 bytes -> 47 s, 840k tokens, success=False; loads despite referencing undefined nonterminals | unusable; validation gap + pathology filed (issue #157) |

**Conclusion:** no real-world corpus grammar can drive a file-scale
*parse-level* incremental benchmark today. The benchmark therefore uses:

1. **StepIdl** (committed at
   `src/DevelApp.IncrementalBench/Corpus/StepIdl.grammar`): a
   purpose-built, line-oriented assignment language whose documents
   parse to completion at file scale (100 -> 41 ms / 1400 tokens cold,
   5000 -> ~1.4 s / 70k tokens, ~linear on the dev container), and
2. **Real C#** at the **lexer level**, where the engine does support
   real files: the realex arm lexes real repository sources with the
   CSharp10 grammar in `GreedyLongestMatch` mode and measures cold full
   lex vs windowed suffix re-lex.

The general engine pathologies (slow grammars, undefined-terminal
validation gap, host crashes) are tracked in engine issue #157; the
benchmark documents them but does not fix them.
