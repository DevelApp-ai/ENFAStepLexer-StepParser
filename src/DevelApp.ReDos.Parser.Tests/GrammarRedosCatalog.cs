using System.Linq;
using System.Text;

namespace DevelApp.ReDos.Parser.Tests
{
    /// <summary>
    /// One grammar under test in the parser-layer ReDoS suite: the full
    /// CEBNF grammar text, the adversarial input shape that makes its
    /// ambiguity explode, and the sizes the immunity theory runs it at.
    ///
    /// The parser layer is where groups, alternation and anchors actually
    /// live (the lexer's forward-only matcher rejects them, the GLR
    /// parser evaluates them). Its immunity is by construction: a hard
    /// active-path budget prunes every step, plus linear step caps
    /// (tokens x 20 parser steps, bytes x 10 lexer steps) and a stuck
    /// detector. These grammars are the parser-layer analogues of the
    /// lexer suite's article/Davis corpus: the classic exponential- and
    /// Catalan-ambiguity shapes, evaluated through the real grammar
    /// pipeline (CEBNF normalization, implicit literal/regex terminals,
    /// whitespace handling) instead of hand-built parser state.
    /// </summary>
    public sealed class ParserRedosGrammar
    {
        /// <summary>Human-readable name used in test output and reports.</summary>
        public required string Name { get; init; }

        /// <summary>Where the grammar comes from.</summary>
        public required string Source { get; init; }

        /// <summary>The full CEBNF grammar text, including the Grammar header.</summary>
        public required string GrammarText { get; init; }

        /// <summary>Description of the adversarial input shape.</summary>
        public required string InputShape { get; init; }

        /// <summary>
        /// Build the adversarial input. The size parameter is measured in
        /// tokens for the ambiguity-stress grammars and in bytes for the
        /// single-token scaling grammars (the scaling entry's input is
        /// one token whose length is the size).
        /// </summary>
        public required System.Func<int, string> BuildInput { get; init; }

        /// <summary>Sizes (tokens or bytes, see <see cref="BuildInput"/>) the immunity theory runs.</summary>
        public required int[] ImmunitySizes { get; init; }

        /// <summary>
        /// The expected parse outcome, asserted when non-null. The
        /// ambiguity-stress grammars deliberately leave this null: the
        /// property under test is boundedness (hard timeout + path
        /// budget), not whether a particular ambiguous input derives -
        /// the winning-path selection under a 10-path budget is engine
        /// semantics, and pinning it here would make the immunity suite
        /// brittle against legitimate pruning changes.
        /// </summary>
        public bool? ExpectedSuccess { get; init; }

        /// <summary>Whether this is a single-token grammar used for the linear-scaling fits.</summary>
        public bool IsScalingGrammar { get; init; }

        /// <summary>An honest-measurement note recorded with the grammar.</summary>
        public string Note { get; init; } = string.Empty;
    }

    /// <summary>
    /// The grammar catalog for the parser layer of the issue-#103 suite:
    /// the classic ambiguity shapes (the article's regex patterns,
    /// re-expressed as CEBNF grammars), deep right-recursive chains, the
    /// dangling-else shape, the end-of-input anchor shape, and the
    /// single-token grammars used for the parser-layer linear-scaling
    /// validation (1 KB -&gt; 100 KB).
    /// </summary>
    public static class GrammarRedosCatalog
    {
        /// <summary>
        /// The ambiguity-stress grammars, run at multiple token counts
        /// under a hard wall timeout. Every one is a shape whose full
        /// parse-forest is exponential (or Catalan) in the input size; a
        /// traditional exhaustive parser explodes on them, the budgeted
        /// GLR stays bounded.
        /// </summary>
        public static readonly System.Collections.Generic.IReadOnlyList<ParserRedosGrammar> AmbiguityStressGrammars =
            new ParserRedosGrammar[]
        {
            new ParserRedosGrammar
            {
                Name = "Ambiguity: ( 'a' | 'a' 'a' )+",
                Source = "Davis (a|aa)+$ parser-layer analogue",
                GrammarText = "Grammar: RedosAmbiguousAlternation\n<s> ::= ( 'a' | 'a' 'a' )+",
                InputShape = "n space-separated 'a' tokens (every partition into 1s and 2s is a derivation)",
                BuildInput = n => string.Join(" ", Enumerable.Repeat("a", n)),
                ImmunitySizes = new[] { 64, 256, 1024 },
                Note = "Fibonacci-many partitions of the a-run; the classic overlapping-alternation blowup, evaluated by the GLR path budget.",
            },
            new ParserRedosGrammar
            {
                Name = "Ambiguity: ( ( 'a' )* 'a' )+",
                Source = "Davis (a+)+ parser-layer analogue",
                GrammarText = "Grammar: RedosNestedRepetition\n<s> ::= ( ( 'a' )* 'a' )+",
                InputShape = "n space-separated 'a' tokens",
                BuildInput = n => string.Join(" ", Enumerable.Repeat("a", n)),
                ImmunitySizes = new[] { 64, 256, 1024 },
                Note = "Nested quantifier shape: the inner ( 'a' )* admits every split of each repetition step.",
            },
            new ParserRedosGrammar
            {
                Name = "Ambiguity: ( 'a' | 'a' )+",
                Source = "Davis (a|a)* parser-layer analogue (duplicate alternatives)",
                GrammarText = "Grammar: RedosDuplicateAlternative\n<s> ::= ( 'a' | 'a' )+",
                InputShape = "n space-separated 'a' tokens",
                BuildInput = n => string.Join(" ", Enumerable.Repeat("a", n)),
                ImmunitySizes = new[] { 64, 256, 1024 },
                Note = "Two identical alternatives double the path count at every token - the pure path-merge stress.",
            },
            new ParserRedosGrammar
            {
                Name = "Ambiguity: <s> ::= 'a' <s> <s> | 'a' (Catalan)",
                Source = "Classic Catalan ambiguity",
                GrammarText = "Grammar: RedosCatalan\n<s> ::= 'a' <s> <s>\n<s> ::= 'a'",
                InputShape = "n space-separated 'a' tokens (every binary tree over the run is a derivation)",
                BuildInput = n => string.Join(" ", Enumerable.Repeat("a", n)),
                ImmunitySizes = new[] { 32, 128, 512 },
                Note = "Catalan-many parse trees in right-recursive form (no left recursion): the hardest GLR ambiguity shape.",
            },
            new ParserRedosGrammar
            {
                Name = "Deep chain: <sum> ::= <N> '+' <sum> | <N>",
                Source = "Deep right-recursive chain (many-token stress)",
                GrammarText = "Grammar: RedosSumList\n<N> ::= /[0-9]+/\n<sum> ::= <N> '+' <sum>\n<sum> ::= <N>",
                InputShape = "n numbers joined by ' + '",
                BuildInput = n => string.Join(" + ", Enumerable.Repeat("1", n)),
                ImmunitySizes = new[] { 128, 1024 },
                Note = "Unambiguous but deep: the end-of-input reduction chain and the many-token line/column bookkeeping are the costs; capped sizes only, no linear-scaling assertion.",
            },
            new ParserRedosGrammar
            {
                Name = "Dangling else: nested if/else",
                Source = "Classic dangling-else ambiguity",
                GrammarText = "Grammar: RedosDanglingElse\n<cond> ::= 'c'\n<stmt> ::= 'if' <cond> <stmt> ( 'else' <stmt> )?\n<stmt> ::= 's'",
                InputShape = "n nested 'if c' followed by 's' and n ' else s' - each else can bind to any enclosing if",
                BuildInput = n => string.Concat(Enumerable.Repeat("if c ", n)) + "s" + string.Concat(Enumerable.Repeat(" else s", n)),
                ImmunitySizes = new[] { 8, 64 },
                Note = "The optional else clause makes every else-attachment a live path; the budget must keep the fan-out bounded.",
            },
            new ParserRedosGrammar
            {
                Name = "End anchor: full input <WORD> '!' parses",
                Source = "Davis (a+)+$ parser-layer anchor analogue",
                GrammarText = "Grammar: RedosEndAnchor\n<WORD> ::= /[a]+/\n<doc> ::= <WORD> '!'",
                InputShape = "an a-run followed by '!'",
                BuildInput = n => new string('a', n) + "!",
                ImmunitySizes = new[] { 1024 },
                ExpectedSuccess = true,
                Note = "The trailing '!' demands full input consumption - the parser-layer $-anchor.",
            },
            new ParserRedosGrammar
            {
                Name = "End anchor: prefix without '!' fails",
                Source = "Davis (a+)+$ parser-layer anchor analogue (failing prefix)",
                GrammarText = "Grammar: RedosEndAnchor\n<WORD> ::= /[a]+/\n<doc> ::= <WORD> '!'",
                InputShape = "the a-run alone, no trailing '!'",
                BuildInput = n => new string('a', n),
                ImmunitySizes = new[] { 1024 },
                ExpectedSuccess = false,
                Note = "The prefix derives <WORD> but the demanded trailing '!' is missing: the parse must fail (no complete derivation) without exploring partitions - the anchored-fail blowup shape.",
            },
        };

        /// <summary>
        /// The single-token scaling grammars: the whole input is one
        /// token whose length grows from 1 KB to 100 KB. Single-token on
        /// purpose - the parser's per-token bookkeeping is O(position),
        /// so a scaling fit with token count growing in n would measure
        /// that known quadratic characteristic instead of parser
        /// linearity (same reasoning as the lexer suite's many-token
        /// entry).
        /// </summary>
        public static readonly System.Collections.Generic.IReadOnlyList<ParserRedosGrammar> ScalingGrammars =
            new ParserRedosGrammar[]
        {
            new ParserRedosGrammar
            {
                Name = "Scaling: <doc> ::= <WORD> ASCII single token",
                Source = "Parser-layer linear-scaling shape",
                GrammarText = "Grammar: RedosScalingAscii\n<WORD> ::= /[a-z]+/\n<doc> ::= <WORD>",
                InputShape = "a single /[a-z]+/ token of n bytes",
                BuildInput = n => new string('a', n),
                ImmunitySizes = new[] { 102_400 },
                IsScalingGrammar = true,
            },
            new ParserRedosGrammar
            {
                Name = "Scaling: <doc> ::= <WORD> Danish multibyte single token",
                Source = "Parser-layer linear-scaling shape, multibyte UTF-8",
                GrammarText = "Grammar: RedosScalingDanish\n<WORD> ::= /[a-zæøå]+/\n<doc> ::= <WORD>",
                InputShape = "a single Danish-letter token of ~n bytes (æ/ø/å cycled, truncated on a character boundary)",
                BuildInput = BuildDanishLetters,
                ImmunitySizes = new[] { 102_400 },
                IsScalingGrammar = true,
            },
        };

        /// <summary>
        /// Build the Danish multibyte input: "æ", "ø", "å" cycled until
        /// the next letter would exceed the byte budget, so the result
        /// never ends inside a multi-byte UTF-8 sequence.
        /// </summary>
        private static string BuildDanishLetters(int lengthBytes)
        {
            var letters = new[] { "æ", "ø", "å" };
            var text = new StringBuilder();
            var bytes = 0;
            for (var i = 0; ; i++)
            {
                var nextBytes = Encoding.UTF8.GetByteCount(letters[i % letters.Length]);
                if (bytes + nextBytes > lengthBytes)
                {
                    break;
                }

                text.Append(letters[i % letters.Length]);
                bytes += nextBytes;
            }

            return text.ToString();
        }
    }
}
