using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace DevelApp.StepLexer
{
    public partial class StepLexer
    {
        /// <summary>
        /// Within-rule groups and alternation for token-rule patterns
        /// (issue #129, Gap 1), handled internally at rule-compile time so
        /// grammar authors can write ordinary regexes.
        ///
        /// The lexer owns the token-rule pattern language: a pattern that the
        /// fast character-class-sequence matcher cannot express is compiled
        /// once, when the rule is added, into a small NFA (Thompson
        /// construction over code-point predicates). Matching then simulates
        /// the NFA strictly forward over the UTF-8 input and keeps the
        /// longest accepting position: no backtracking, cost linear in the
        /// input length times the (compile-time bounded) state count, so the
        /// ReDoS protection and speed profile of the lexer/parser split is
        /// unchanged. Capturing and non-capturing groups are equivalent for
        /// matching (the lexer never exposes capture contents).
        ///
        /// Constructs that cannot be evaluated without backtracking or
        /// lookahead - backreferences, lookaround, anchors/zero-width
        /// assertions, scoped modifier groups - are rejected with a specific
        /// compile-time error (see <see cref="TryValidateRulePattern"/>)
        /// instead of silently becoming a never-matching rule.
        /// </summary>

        /// <summary>
        /// One state of a compiled token-rule NFA. A transition with a null
        /// predicate is an epsilon transition; otherwise it consumes one code
        /// point satisfying the predicate.
        /// </summary>
        private sealed class NfaState
        {
            public List<(Func<uint, bool>? Predicate, int Target)> Transitions = new();
            public bool IsAccept;
        }

        /// <summary>A compiled NFA: the state list plus start/accept state ids.</summary>
        private sealed class NfaGraph
        {
            public required List<NfaState> States { get; init; }
            public required int Start { get; init; }
            public required int Accept { get; init; }
        }

        /// <summary>
        /// Upper bound for expanding a bounded <c>{n,m}</c> quantifier over a
        /// grouped atom into explicit NFA copies. Pure class-sequence patterns
        /// keep the full <see cref="MaxQuantifierCount"/> range (they are
        /// matched by counting, not by graph expansion); the cap only applies
        /// to patterns that contain groups or alternation.
        /// </summary>
        private const int NfaMaxBoundedRepeat = 64;

        /// <summary>
        /// Upper bound on NFA states per token-rule pattern. Keeps compile-time
        /// cost and match-time state-set size bounded so a pathological
        /// pattern fails validation instead of exhausting memory.
        /// </summary>
        private const int NfaMaxStates = 4096;

        /// <summary>
        /// Compile a regex body containing groups/alternation into an NFA.
        /// Atoms reuse the same parsers as the class-sequence matcher
        /// (character classes, escapes, shorthand classes, Unicode properties,
        /// '.', literals) so both matcher kinds accept the same atom syntax.
        /// </summary>
        /// <param name="pattern">The pattern body (without the surrounding slashes).</param>
        /// <param name="ignoreCase">Whether to wrap atom predicates case-insensitively.</param>
        /// <param name="graph">The compiled NFA on success.</param>
        /// <param name="error">A specific reason on failure.</param>
        /// <returns>True when the pattern compiled.</returns>
        private static bool TryCompileRegexNfa(string pattern, bool ignoreCase,
            [NotNullWhen(true)] out NfaGraph? graph, out string? error)
        {
            graph = null;
            error = null;

            if (pattern.Length == 0)
            {
                error = "empty pattern matches zero width";
                return false;
            }

            var states = new List<NfaState>();
            int i = 0;
            if (!TryParseNfaAlternation(pattern, ref i, states, ignoreCase, out var root, out error))
            {
                return false;
            }

            if (i != pattern.Length)
            {
                error = $"unexpected '{pattern[i]}' at position {i}";
                return false;
            }

            if (states.Count > NfaMaxStates)
            {
                error = $"pattern compiles to {states.Count} states, above the cap of {NfaMaxStates}";
                return false;
            }

            states[root.End].IsAccept = true;
            graph = new NfaGraph { States = states, Start = root.Start, Accept = root.End };
            return true;
        }

        private static int NewNfaState(List<NfaState> states)
        {
            states.Add(new NfaState());
            return states.Count - 1;
        }

        private static void NfaEpsilon(List<NfaState> states, int from, int to)
            => states[from].Transitions.Add((null, to));

        private static (int Start, int End) NfaCharFragment(List<NfaState> states, Func<uint, bool> predicate)
        {
            int start = NewNfaState(states);
            int end = NewNfaState(states);
            states[start].Transitions.Add((predicate, end));
            return (start, end);
        }

        private static (int Start, int End) NfaEpsilonFragment(List<NfaState> states)
        {
            int start = NewNfaState(states);
            int end = NewNfaState(states);
            NfaEpsilon(states, start, end);
            return (start, end);
        }

        /// <summary>
        /// Deep-copy a fragment subgraph so a quantified atom can be repeated
        /// without sharing states (shared states would corrupt the counters
        /// implied by the graph shape).
        /// </summary>
        private static (int Start, int End) CopyNfaFragment(List<NfaState> states, (int Start, int End) fragment)
        {
            var map = new Dictionary<int, int>();
            var queue = new Queue<int>();
            map[fragment.Start] = NewNfaState(states);
            queue.Enqueue(fragment.Start);
            while (queue.Count > 0)
            {
                int old = queue.Dequeue();
                int copy = map[old];
                foreach (var (predicate, target) in states[old].Transitions)
                {
                    if (!map.TryGetValue(target, out int targetCopy))
                    {
                        targetCopy = NewNfaState(states);
                        map[target] = targetCopy;
                        queue.Enqueue(target);
                    }
                    states[copy].Transitions.Add((predicate, targetCopy));
                }
            }
            return (map[fragment.Start], map[fragment.End]);
        }

        /// <summary>Parse <c>seq ('|' seq)*</c> into one fragment.</summary>
        private static bool TryParseNfaAlternation(string pattern, ref int i, List<NfaState> states, bool ignoreCase,
            out (int Start, int End) fragment, out string? error)
        {
            fragment = default;
            error = null;
            var alternatives = new List<(int Start, int End)>();

            while (true)
            {
                if (!TryParseNfaSequence(pattern, ref i, states, ignoreCase, out var sequence, out error))
                {
                    return false;
                }
                alternatives.Add(sequence);
                if (i < pattern.Length && pattern[i] == '|')
                {
                    i++;
                    continue;
                }
                break;
            }

            if (alternatives.Count == 1)
            {
                fragment = alternatives[0];
                return true;
            }

            int start = NewNfaState(states);
            int end = NewNfaState(states);
            foreach (var alt in alternatives)
            {
                NfaEpsilon(states, start, alt.Start);
                NfaEpsilon(states, alt.End, end);
            }
            fragment = (start, end);
            return true;
        }

        /// <summary>Parse a sequence of quantified terms (stops at ')', '|' or end).</summary>
        private static bool TryParseNfaSequence(string pattern, ref int i, List<NfaState> states, bool ignoreCase,
            out (int Start, int End) fragment, out string? error)
        {
            fragment = default;
            error = null;
            var terms = new List<(int Start, int End)>();

            while (i < pattern.Length && pattern[i] != ')' && pattern[i] != '|')
            {
                if (!TryParseNfaTerm(pattern, ref i, states, ignoreCase, out var term, out error))
                {
                    return false;
                }
                terms.Add(term);
            }

            if (terms.Count == 0)
            {
                fragment = NfaEpsilonFragment(states);
                return true;
            }

            int start = NewNfaState(states);
            int current = start;
            foreach (var term in terms)
            {
                NfaEpsilon(states, current, term.Start);
                current = term.End;
            }
            fragment = (start, current);
            return true;
        }

        /// <summary>Parse one atom plus its optional quantifier.</summary>
        private static bool TryParseNfaTerm(string pattern, ref int i, List<NfaState> states, bool ignoreCase,
            out (int Start, int End) fragment, out string? error)
        {
            if (!TryParseNfaAtom(pattern, ref i, states, ignoreCase, out var atom, out error))
            {
                fragment = default;
                return false;
            }

            if (!TryParseInlineQuantifier(pattern, ref i, out int minCount, out int maxCount))
            {
                fragment = default;
                error = $"invalid quantifier at position {i}";
                return false;
            }

            return TryBuildNfaQuantified(states, atom, minCount, maxCount, out fragment, out error);
        }

        /// <summary>
        /// Apply a quantifier to an atom fragment by graph construction:
        /// <c>{n}</c> chains n copies, <c>{n,}</c>/<c>*</c>/<c>+</c> chain n
        /// copies plus a star loop, <c>{n,m}</c> chains n copies plus (m-n)
        /// optional copies, <c>?</c> adds an epsilon bypass. Lazy markers were
        /// already normalized to "exactly the minimum" by
        /// <see cref="TryParseInlineQuantifier"/>, matching the no-backtracking
        /// convention of the class-sequence matcher.
        /// </summary>
        private static bool TryBuildNfaQuantified(List<NfaState> states, (int Start, int End) atom,
            int minCount, int maxCount, out (int Start, int End) fragment, out string? error)
        {
            fragment = default;
            error = null;

            if (minCount == 1 && maxCount == 1)
            {
                fragment = atom;
                return true;
            }

            bool unbounded = maxCount >= MaxQuantifierCount;

            if (unbounded && minCount > NfaMaxBoundedRepeat)
            {
                error = $"quantifier with minimum {minCount} exceeds the expansion cap of {NfaMaxBoundedRepeat} in patterns with groups/alternation";
                return false;
            }
            if (!unbounded && maxCount > NfaMaxBoundedRepeat)
            {
                error = $"bounded quantifier {{{minCount},{maxCount}}} exceeds the expansion cap of {NfaMaxBoundedRepeat} in patterns with groups/alternation; use {{{minCount},}} instead";
                return false;
            }

            if (maxCount == 0)
            {
                fragment = NfaEpsilonFragment(states);
                return true;
            }

            int start = NewNfaState(states);
            int current = start;

            for (int k = 0; k < minCount; k++)
            {
                var copy = CopyNfaFragment(states, atom);
                NfaEpsilon(states, current, copy.Start);
                current = copy.End;
            }

            if (unbounded)
            {
                var loop = CopyNfaFragment(states, atom);
                int loopStart = NewNfaState(states);
                int loopEnd = NewNfaState(states);
                NfaEpsilon(states, current, loopStart);
                NfaEpsilon(states, loopStart, loop.Start);
                NfaEpsilon(states, loopStart, loopEnd);
                NfaEpsilon(states, loop.End, loop.Start);
                NfaEpsilon(states, loop.End, loopEnd);
                current = loopEnd;
            }
            else
            {
                for (int k = minCount; k < maxCount; k++)
                {
                    var copy = CopyNfaFragment(states, atom);
                    int optionStart = NewNfaState(states);
                    int optionEnd = NewNfaState(states);
                    NfaEpsilon(states, current, optionStart);
                    NfaEpsilon(states, optionStart, copy.Start);
                    NfaEpsilon(states, optionStart, optionEnd);
                    NfaEpsilon(states, copy.End, optionEnd);
                    current = optionEnd;
                }
            }

            fragment = (start, current);
            return true;
        }

        /// <summary>
        /// Parse one atom: a group, character class, '.', escape sequence or
        /// literal code point. Groups support plain capturing and
        /// non-capturing <c>(?:...)</c> forms with arbitrary nested
        /// alternation; everything that needs backtracking or lookahead is
        /// rejected with a specific error.
        /// </summary>
        private static bool TryParseNfaAtom(string pattern, ref int i, List<NfaState> states, bool ignoreCase,
            out (int Start, int End) fragment, out string? error)
        {
            fragment = default;
            error = null;

            char c = pattern[i];

            if (c == '(')
            {
                if (i + 1 >= pattern.Length)
                {
                    error = "unclosed group";
                    return false;
                }

                bool nonCapturing = i + 2 < pattern.Length && pattern[i + 1] == '?' && pattern[i + 2] == ':';
                if (pattern[i + 1] == '?' && !nonCapturing)
                {
                    error = $"unsupported group construct at position {i} (lookahead/lookbehind, named groups and scoped modifiers cannot be evaluated without backtracking)";
                    return false;
                }

                i += nonCapturing ? 3 : 1;
                if (!TryParseNfaAlternation(pattern, ref i, states, ignoreCase, out var inner, out error))
                {
                    return false;
                }
                if (i >= pattern.Length || pattern[i] != ')')
                {
                    error = "unclosed group";
                    return false;
                }
                i++;
                fragment = inner;
                return true;
            }

            Func<uint, bool> predicate;
            if (c == '[')
            {
                if (!TryParseCharacterClass(pattern, ref i, out predicate))
                {
                    error = $"invalid character class at position {i}";
                    return false;
                }
            }
            else if (c == '.')
            {
                i++;
                // PCRE default: '.' matches any code point except LF.
                predicate = static cp => cp != '\n';
            }
            else if (c == '\\')
            {
                if (i + 1 >= pattern.Length)
                {
                    error = "trailing backslash";
                    return false;
                }

                char escaped = pattern[i + 1];
                if (escaped is >= '1' and <= '9' || escaped == 'k')
                {
                    error = $"backreference at position {i} is not supported (token-rule patterns cannot reference earlier groups)";
                    return false;
                }
                if (escaped is 'b' or 'B' or 'A' or 'Z' or 'z' or 'G')
                {
                    error = $"anchor/zero-width assertion '\\{escaped}' at position {i} is not supported in token-rule patterns";
                    return false;
                }
                if (!TryParseEscapeAtom(pattern, ref i, out predicate))
                {
                    error = $"invalid escape at position {i}";
                    return false;
                }
            }
            else if (c == '^' || c == '$')
            {
                error = $"anchor '{c}' at position {i} is not supported in token-rule patterns";
                return false;
            }
            else
            {
                var (cp, consumed) = DecodePatternCodepoint(pattern, i);
                if (consumed == 0 || IsUnsupportedLiteral(cp))
                {
                    error = $"unsupported syntax character '{pattern[i]}' at position {i}";
                    return false;
                }
                i += consumed;
                predicate = cp2 => cp2 == cp;
            }

            if (ignoreCase)
            {
                predicate = WrapIgnoreCase(predicate);
            }

            fragment = NfaCharFragment(states, predicate);
            return true;
        }

        /// <summary>
        /// Match a compiled NFA against the input by forward simulation:
        /// keep the set of states reachable after each consumed code point
        /// (epsilon closure included) and remember the furthest position at
        /// which the accept state is reachable. That is the longest match;
        /// the loop runs in O(input code points x NFA transitions) with no
        /// backtracking. Zero-width matches are rejected like every other
        /// matcher kind (issue #111).
        /// </summary>
        private static (bool success, int length) MatchRegexNfa(CompiledTokenRule compiled, ReadOnlySpan<byte> input)
        {
            var states = compiled.NfaStates!;
            var current = new HashSet<int> { compiled.NfaStart };
            var next = new HashSet<int>();
            EpsilonClosure(states, current);

            int best = current.Contains(compiled.NfaAccept) ? 0 : -1;
            int position = 0;

            while (position < input.Length && current.Count > 0)
            {
                var (codepoint, bytesConsumed) = UTF8Utils.GetNextCodepoint(input, position);
                if (bytesConsumed == 0)
                {
                    break;
                }

                next.Clear();
                foreach (var state in current)
                {
                    foreach (var (predicate, target) in states[state].Transitions)
                    {
                        if (predicate != null && predicate(codepoint))
                        {
                            next.Add(target);
                        }
                    }
                }

                EpsilonClosure(states, next);
                position += bytesConsumed;
                (current, next) = (next, current);

                if (current.Contains(compiled.NfaAccept))
                {
                    best = position;
                }
            }

            return best > 0 ? (true, best) : (false, 0);
        }

        /// <summary>Expand epsilon transitions inside a reachable state set.</summary>
        private static void EpsilonClosure(List<NfaState> states, HashSet<int> set)
        {
            var stack = new Stack<int>(set);
            while (stack.Count > 0)
            {
                var state = stack.Pop();
                foreach (var (predicate, target) in states[state].Transitions)
                {
                    if (predicate == null && set.Add(target))
                    {
                        stack.Push(target);
                    }
                }
            }
        }
    }
}
