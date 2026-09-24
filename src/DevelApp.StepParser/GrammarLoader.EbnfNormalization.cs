using System;
using System.Collections.Generic;
using System.Linq;
using DevelApp.StepLexer;

namespace DevelApp.StepParser
{
    /// <summary>
    /// CEBNF-to-BNF production normalization (issue #83, Cluster C).
    ///
    /// <see cref="GrammarLoader"/>'s legacy
    /// <c>ParseRightHandSide</c> extracted symbols with a token regex and
    /// silently dropped EBNF operators, distorting productions:
    /// <c>&lt;middle-field&gt;?</c> became mandatory, repetition groups
    /// became single occurrences, grouping parentheses and inline
    /// alternation were lost, and <c>@DIALECT[...]</c> annotations were
    /// spliced into symbol lists. This partial adds a real EBNF tokenizer
    /// and normalizer used ahead of the legacy path in
    /// <c>ParseProductionRule</c>: optionals expand to present/absent
    /// alternatives (no epsilon — the GLR step loop has no lookahead
    /// gating), parenthesized groups inline their alternation, and
    /// repetition quantifiers synthesize right-recursive helper rules.
    /// Unsupported syntax falls back to the legacy parser unchanged.
    /// </summary>
    public partial class GrammarLoader
    {
        /// <summary>
        /// The grammar definition currently being parsed (token rules are
        /// consulted to decide whether a symbol may legitimately be absent,
        /// e.g. whitespace-only patterns that the lexer skips as zero-width).
        /// </summary>
        private GrammarDefinition? _currentGrammarDefinition;
        /// <summary>
        /// Upper bound on BNF alternatives synthesized per production
        /// before the normalizer gives up and falls back to legacy
        /// behavior. Guards against exponential blow-up of many optionals
        /// in one sequence.
        /// </summary>
        private const int MaxEbnfAlternatives = 4096;

        /// <summary>Counter for synthesized helper rule names.</summary>
        private int _ebnfHelperCounter;

        /// <summary>
        /// One EBNF right-hand-side item: a symbol
        /// (<c>&lt;name&gt;</c>, quoted or regex terminal, bare TERMINAL)
        /// or a parenthesized group, plus its quantifier
        /// (<c>' '</c>, <c>'?'</c>, <c>'*'</c>, <c>'+'</c>).
        /// </summary>
        private sealed class EbnfItem
        {
            /// <summary>Symbol text (without the quantifier), or null for groups.</summary>
            public string? Symbol;

            /// <summary>For groups: the alternative sequences inside the parentheses.</summary>
            public List<List<EbnfItem>>? GroupAlternatives;

            /// <summary>The quantifier: ' ' (none), '?', '*', or '+'.</summary>
            public char Quantifier;
        }

        /// <summary>
        /// One open parenthesized group during tokenization: the current
        /// alternative being read plus the already completed ones.
        /// </summary>
        private sealed class EbnfFrame
        {
            /// <summary>Items of the alternative currently being read.</summary>
            public List<EbnfItem> Current = new();

            /// <summary>Completed alternatives of this group.</summary>
            public List<List<EbnfItem>> Alternatives = new();
        }

        /// <summary>
        /// Tokenize a right-hand side into top-level alternatives of
        /// EBNF items. Quoted terminals and regexes are single symbols;
        /// nested groups and inline alternation are preserved;
        /// <c>@NAME[...]</c> annotations are skipped. Returns false for
        /// unsupported syntax (the caller falls back to legacy behavior).
        /// </summary>
        private static bool TryTokenizeEbnfRhs(string rhs, out List<List<EbnfItem>> alternatives)
        {
            alternatives = new List<List<EbnfItem>>();
            var stack = new Stack<EbnfFrame>();
            var root = new EbnfFrame();
            stack.Push(root);

            int i = 0;
            while (i < rhs.Length)
            {
                char c = rhs[i];
                if (char.IsWhiteSpace(c))
                {
                    i++;
                    continue;
                }

                if (c == '|')
                {
                    var frame = stack.Peek();
                    frame.Alternatives.Add(frame.Current);
                    frame.Current = new List<EbnfItem>();
                    i++;
                    continue;
                }

                if (c == '(')
                {
                    stack.Push(new EbnfFrame());
                    i++;
                    continue;
                }

                if (c == ')')
                {
                    // No matching '(' or empty group: unsupported.
                    if (stack.Count == 1)
                    {
                        return false;
                    }

                    var frame = stack.Pop();
                    frame.Alternatives.Add(frame.Current);
                    i++;
                    char quantifier = ReadEbnfQuantifier(rhs, ref i);
                    stack.Peek().Current.Add(new EbnfItem
                    {
                        GroupAlternatives = frame.Alternatives,
                        Quantifier = quantifier
                    });
                    continue;
                }

                if (c == '@')
                {
                    if (!TrySkipAnnotation(rhs, ref i))
                    {
                        return false;
                    }
                    continue;
                }

                string? symbol = c switch
                {
                    '<' => ReadDelimitedSymbol(rhs, ref i, '>', requiresOpen: false),
                    '/' => ReadRegexSymbol(rhs, ref i),
                    '\'' => ReadDelimitedSymbol(rhs, ref i, '\'', requiresOpen: false),
                    '"' => ReadDelimitedSymbol(rhs, ref i, '"', requiresOpen: false),
                    _ => null
                };

                if (symbol == null && (char.IsLetter(c) || c == '_'))
                {
                    int start = i;
                    while (i < rhs.Length && (char.IsLetterOrDigit(rhs[i]) || rhs[i] == '_' || rhs[i] == '-'))
                    {
                        i++;
                    }
                    symbol = rhs[start..i];
                }

                if (symbol == null)
                {
                    // Unsupported syntax character (EBNF [ ... ] optionals,
                    // braces, ...): legacy fallback.
                    return false;
                }

                char itemQuantifier = ReadEbnfQuantifier(rhs, ref i);
                stack.Peek().Current.Add(new EbnfItem
                {
                    Symbol = symbol,
                    Quantifier = itemQuantifier
                });
            }

            // Unterminated '('.
            if (stack.Count != 1)
            {
                return false;
            }

            root.Alternatives.Add(root.Current);
            alternatives = root.Alternatives;
            return true;
        }

        /// <summary>
        /// Read a single-character-delimited symbol (angle brackets or
        /// quotes) starting at <paramref name="index"/>. The delimiter
        /// character is part of the returned symbol text.
        /// </summary>
        private static string? ReadDelimitedSymbol(string rhs, ref int index, char close, bool requiresOpen)
        {
            int end = rhs.IndexOf(close, index + 1);
            if (end < 0)
            {
                return null;
            }

            var symbol = rhs[index..(end + 1)];
            index = end + 1;
            return symbol;
        }

        /// <summary>
        /// Read a <c>/regex/</c> symbol starting at the opening slash,
        /// honoring backslash-escaped slashes inside the regex.
        /// </summary>
        private static string? ReadRegexSymbol(string rhs, ref int index)
        {
            int i = index + 1;
            while (i < rhs.Length)
            {
                if (rhs[i] == '\\')
                {
                    i += 2;
                    continue;
                }
                if (rhs[i] == '/')
                {
                    var symbol = rhs[index..(i + 1)];
                    index = i + 1;
                    return symbol;
                }
                i++;
            }
            return null; // unterminated regex
        }

        /// <summary>
        /// Skip an <c>@NAME[ ... ]</c> annotation (a no-op for parsing,
        /// e.g. <c>@DIRECTION[...]</c> / <c>@DIALECT[...]</c>), including
        /// nested brackets. A bare annotation name without brackets is
        /// also accepted.
        /// </summary>
        private static bool TrySkipAnnotation(string rhs, ref int index)
        {
            int i = index + 1;
            while (i < rhs.Length && (char.IsLetterOrDigit(rhs[i]) || rhs[i] == '_'))
            {
                i++;
            }

            if (i < rhs.Length && rhs[i] == '[')
            {
                int depth = 0;
                while (i < rhs.Length)
                {
                    if (rhs[i] == '[')
                    {
                        depth++;
                    }
                    else if (rhs[i] == ']')
                    {
                        depth--;
                        if (depth == 0)
                        {
                            i++;
                            break;
                        }
                    }
                    i++;
                }

                if (depth != 0)
                {
                    return false; // unterminated annotation
                }
            }

            index = i;
            return true;
        }

        /// <summary>
        /// Read the quantifier at <paramref name="index"/>, if any.
        /// </summary>
        private static char ReadEbnfQuantifier(string rhs, ref int index)
        {
            if (index < rhs.Length && (rhs[index] == '?' || rhs[index] == '*' || rhs[index] == '+'))
            {
                return rhs[index++];
            }
            return ' ';
        }

        /// <summary>
        /// Normalize one production right-hand side (already split from
        /// the rule text) into one BNF symbol sequence per alternative,
        /// synthesizing helper rules for repetition as needed. Returns
        /// null when the RHS is not fully supported (legacy fallback).
        /// </summary>
        private List<ProductionRule>? TryNormalizeEbnfRhs(
            string rhsText,
            string ruleName,
            string context,
            int priority,
            Action<GraphNodeRef, List<GraphNodeRef>, CognitiveGraph.Builder.CognitiveGraphBuilder>? semanticAction)
        {
            if (!TryTokenizeEbnfRhs(rhsText, out var alternatives))
            {
                return null;
            }

            var produced = new List<ProductionRule>();
            foreach (var alternative in alternatives)
            {
                if (!TryExpandEbnfSequence(alternative, ruleName, produced, out var sequences))
                {
                    return null;
                }

                foreach (var sequence in sequences)
                {
                    // Skip empty (epsilon) alternatives: the GLR step loop
                    // has no lookahead gating, so epsilon productions would
                    // be applicable on every step (same as the legacy path).
                    if (sequence.Count == 0)
                    {
                        continue;
                    }

                    produced.Add(new ProductionRule(ruleName, sequence, context)
                    {
                        Precedence = priority,
                        SemanticAction = semanticAction
                    });
                }
            }

            // An all-empty right-hand side (e.g. "a? b?") produces no rules;
            // treat that as unsupported so the legacy path handles it.
            return produced.Count > 0 ? produced : null;
        }

        /// <summary>
        /// Expand one EBNF alternative (a sequence of items) into BNF
        /// symbol sequences by cross product over each item's options,
        /// bounded by <see cref="MaxEbnfAlternatives"/>.
        /// </summary>
        private bool TryExpandEbnfSequence(List<EbnfItem> items, string ownerName,
            List<ProductionRule> produced, out List<List<string>> sequences)
        {
            sequences = new List<List<string>> { new List<string>() };

            foreach (var item in items)
            {
                List<List<string>> options;
                if (item.Symbol != null)
                {
                    if (!TryExpandEbnfSymbol(item, ownerName, produced, out options))
                    {
                        return false;
                    }
                }
                else if (!TryExpandEbnfGroup(item, ownerName, produced, out options))
                {
                    return false;
                }

                var next = new List<List<string>>(sequences.Count * options.Count);
                foreach (var prefix in sequences)
                {
                    foreach (var option in options)
                    {
                        var combined = new List<string>(prefix.Count + option.Count);
                        combined.AddRange(prefix);
                        combined.AddRange(option);
                        next.Add(combined);
                    }
                }

                sequences = next;
                if (sequences.Count > MaxEbnfAlternatives)
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Expand a symbol item's options: present/absent for optionals
        /// ('?' or a zero-width-matchable token such as
        /// <c>&lt;opt-whitespace&gt;</c>), a repetition helper for
        /// '*'/'+' quantifiers, present-only otherwise.
        /// </summary>
        private bool TryExpandEbnfSymbol(EbnfItem item, string ownerName,
            List<ProductionRule> produced, out List<List<string>> options)
        {
            options = new List<List<string>>();
            // Strip angle brackets: non-terminal production symbols are
            // stored bare (<expr> -> expr), like the legacy parser does.
            // Quoted literals and regex terminals keep their delimiters.
            var rawSymbol = item.Symbol!;
            var symbol = rawSymbol.Length > 2 && rawSymbol[0] == '<' && rawSymbol[^1] == '>'
                ? rawSymbol[1..^1]
                : rawSymbol;
            bool optional = item.Quantifier == '?' || SymbolAllowsZeroWidthToken(symbol);

            switch (item.Quantifier)
            {
                case ' ':
                case '?':
                    options.Add(new List<string> { symbol });
                    if (optional)
                    {
                        options.Add(new List<string>());
                    }
                    return true;

                case '*':
                {
                    var helper = SynthesizeRepetitionHelper(ownerName,
                        new List<List<string>> { new List<string> { symbol } }, produced);
                    options.Add(new List<string> { helper });
                    options.Add(new List<string>());
                    return true;
                }

                case '+':
                {
                    var helper = SynthesizeRepetitionHelper(ownerName,
                        new List<List<string>> { new List<string> { symbol } }, produced);
                    options.Add(new List<string> { helper });
                    return true;
                }

                default:
                    return false;
            }
        }

        /// <summary>
        /// Expand a group item's options: inline alternation for plain
        /// groups, alternation plus the absent alternative for
        /// <c>( ... )?</c>, and a repetition helper for
        /// <c>( ... )*</c> / <c>( ... )+</c>.
        /// </summary>
        private bool TryExpandEbnfGroup(EbnfItem item, string ownerName,
            List<ProductionRule> produced, out List<List<string>> options)
        {
            options = new List<List<string>>();

            var innerSequences = new List<List<string>>();
            foreach (var inner in item.GroupAlternatives!)
            {
                if (!TryExpandEbnfSequence(inner, ownerName, produced, out var sequences))
                {
                    return false;
                }
                innerSequences.AddRange(sequences);
            }

            switch (item.Quantifier)
            {
                case ' ':
                    options.AddRange(innerSequences);
                    return true;

                case '?':
                    options.AddRange(innerSequences);
                    options.Add(new List<string>());
                    return true;

                case '*':
                {
                    var helper = SynthesizeRepetitionHelper(ownerName, innerSequences, produced);
                    options.Add(new List<string> { helper });
                    options.Add(new List<string>());
                    return true;
                }

                case '+':
                {
                    var helper = SynthesizeRepetitionHelper(ownerName, innerSequences, produced);
                    options.Add(new List<string> { helper });
                    return true;
                }

                default:
                    return false;
            }
        }

        /// <summary>
        /// Synthesize a helper non-terminal covering one or more
        /// repetitions of any of the given sequences:
        /// <c>h ::= seq h</c> and <c>h ::= seq</c> for each non-empty
        /// distinct sequence. Right recursion reduces bottom-up in the
        /// GLR stack engine without epsilon productions.
        /// </summary>
        private string SynthesizeRepetitionHelper(string ownerName,
            List<List<string>> sequences, List<ProductionRule> produced)
        {
            var helperName = $"{ownerName}__rep{++_ebnfHelperCounter}";

            foreach (var sequence in sequences)
            {
                // A sequence that can match empty would make the helper
                // self-recursive without consuming input; skip it.
                if (sequence.Count == 0)
                {
                    continue;
                }

                var recursive = new List<string>(sequence) { helperName };
                produced.Add(new ProductionRule(helperName, recursive));
            }

            foreach (var sequence in sequences)
            {
                if (sequence.Count == 0)
                {
                    continue;
                }

                produced.Add(new ProductionRule(helperName, sequence));
            }

            return helperName;
        }

        /// <summary>
        /// Whether a production symbol refers to a token rule whose
        /// pattern can match zero width (for example
        /// <c>&lt;opt-whitespace&gt; ::= /[ \t\n\r]*/</c>): the lexer
        /// rejects zero-width matches, so the token may legitimately be
        /// absent and the symbol must expand to present/absent
        /// alternatives.
        /// </summary>
        private bool SymbolAllowsZeroWidthToken(string symbol)
        {
            var name = symbol.StartsWith('<') && symbol.EndsWith('>')
                ? symbol[1..^1]
                : symbol;

            foreach (var tokenRule in _currentGrammarDefinition?.TokenRules ?? Enumerable.Empty<TokenRule>())
            {
                if (string.Equals(tokenRule.Name, name, StringComparison.Ordinal)
                    && TokenPatternMatchesOnlyWhitespace(tokenRule.Pattern))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Simplified whitespace-only pattern check (see
        /// StepParserEngine.MatchesWhitespacePattern for the original):
        /// a quoted or slashed pattern whose atoms are all whitespace
        /// characters or whitespace escapes.
        /// </summary>
        private static bool TokenPatternMatchesOnlyWhitespace(string pattern)
        {
            string core;
            if (pattern.Length > 2 && pattern[0] == '/' && pattern[^1] == '/')
            {
                core = pattern[1..^1];
            }
            else if (pattern.Length > 2 && ((pattern[0] == '"' && pattern[^1] == '"')
                || (pattern[0] == '\'' && pattern[^1] == '\'')))
            {
                core = pattern[1..^1];
            }
            else
            {
                return false;
            }

            if (core.Length == 0 || core.Contains("[^"))
            {
                return false;
            }

            bool sawWhitespaceAtom = core.Any(static c => char.IsWhiteSpace(c))
                || core.Contains("\\t") || core.Contains("\\r") || core.Contains("\\n")
                || core.Contains("\\f") || core.Contains("\\v") || core.Contains("\\s");
            if (!sawWhitespaceAtom)
            {
                return false;
            }

            var reduced = core
                .Replace("\\t", string.Empty)
                .Replace("\\r", string.Empty)
                .Replace("\\n", string.Empty)
                .Replace("\\f", string.Empty)
                .Replace("\\v", string.Empty)
                .Replace("\\s", string.Empty)
                .Replace("\\ ", string.Empty);

            foreach (var structural in new[] { '[', ']', '+', '*', '?', '(', ')', '{', '}', ',', '|', '^', '-', '\\' })
            {
                reduced = reduced.Replace(structural.ToString(), string.Empty);
            }

            return reduced.All(static c => char.IsWhiteSpace(c));
        }
    }
}
