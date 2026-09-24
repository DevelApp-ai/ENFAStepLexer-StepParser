using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace DevelApp.StepLexer
{
    public partial class StepLexer
    {

        /// <summary>
        /// Process a single rule match
        /// </summary>
        private void ProcessSingleMatch(LexerPath path, TokenRule rule, string matchText, int length,
            int line, int column, PathStepResult result)
        {
            int startPosition = path.Position;
            path.Position += length;

            if (!rule.IsSkippable)
            {
                var location = new CodeLocation(_fileName, line, column, line, column + length, path.CurrentContext);
                var token = new StepToken(rule.Name, matchText, location, path.CurrentContext)
                {
                    StartPosition = startPosition,
                    Length = length
                };
                
                // Check if token can be split for ambiguity resolution
                if (CanSplitToken(token, rule))
                {
                    token.IsSplittable = true;
                    token.SplitTokens = GenerateSplitTokens(token, rule, location);
                }

                path.Tokens.Add(token);
                result.Tokens.Add(token);

                // Execute rule action if present
                rule.Action?.Invoke(token);
            }

            result.Paths.Add(path);
        }

        /// <summary>
        /// Process multiple rule matches by creating split paths
        /// </summary>
        private void ProcessMultipleMatches(LexerPath originalPath, 
            List<(TokenRule rule, int length, string text)> matches, 
            int line, int column, PathStepResult result)
        {
            foreach (var (rule, length, text) in matches)
            {
                var newPath = originalPath.Clone(_nextPathId++);
                ProcessSingleMatch(newPath, rule, text, length, line, column, result);
            }
        }

        /// <summary>
        /// Check if a rule is applicable in the current context
        /// </summary>
        private bool IsRuleApplicableInContext(TokenRule rule, string currentContext)
        {
            if (string.IsNullOrEmpty(rule.Context))
                return true; // Rule applies to all contexts

            return rule.Context == currentContext || _contextStack.Contains(rule.Context);
        }

        /// <summary>
        /// Try to match a rule pattern against input
        /// </summary>
        private (bool success, int length, string text) TryMatchRule(TokenRule rule, ReadOnlySpan<byte> input)
        {
            // Simplified pattern matching - in a real implementation, this would use proper regex/pattern engines
            var pattern = rule.Pattern;
            
            if (pattern.StartsWith("/") && pattern.EndsWith("/") && pattern.Length > 1)
            {
                // Regex pattern - simplified implementation
                var regex = pattern.Length > 2 ? pattern[1..^1] : "";
                return TryMatchRegex(regex, input);
            }
            else if (pattern.StartsWith("\"") && pattern.EndsWith("\"") && pattern.Length > 1)
            {
                // Literal string match
                var literal = pattern.Length > 2 ? pattern[1..^1] : "";
                return TryMatchLiteral(literal, input);
            }
            else if (pattern.StartsWith("'") && pattern.EndsWith("'") && pattern.Length > 2)
            {
                // Single-quoted literal string match (issue #80): without
                // this branch the quotes themselves were part of the matched
                // literal, so every single-quoted token rule matched nothing.
                var literal = pattern[1..^1];
                return TryMatchLiteral(literal, input);
            }
            
            // Default to literal match
            return TryMatchLiteral(pattern, input);
        }

        /// <summary>
        /// Regex matching for the supported pattern set: the common literal
        /// character-class patterns, the standard metavariable and ellipsis
        /// pattern tokens (issue #65), Unicode property escapes
        /// (<c>\p{Name}</c>/<c>\P{Name}</c>) with an optional quantifier,
        /// quoted literal sequences (<c>\Q...\E</c>) and plain literal text,
        /// with inline modifiers (<c>(?i)</c>, <c>(?x)</c>, ...),
        /// <c>(?#...)</c> comments, atomic groups (<c>(?&gt;...)</c>) and
        /// possessive quantifiers handled during preprocessing.
        /// </summary>
        private (bool success, int length, string text) TryMatchRegex(string pattern, ReadOnlySpan<byte> input)
        {
            // Strip (?#...) comments and inline modifier groups; honor (?i)
            // for case-insensitive matching and (?x) for extended mode.
            var (core, ignoreCase) = PreprocessRegexPattern(pattern);

            switch (core)
            {
                case "[0-9]+":
                    return MatchDigits(input);
                case "[a-zA-Z][a-zA-Z0-9]*":
                    return MatchIdentifier(input);
                case "[ \\t\\r\\n]+":
                    return MatchWhitespace(input);

                // Standard metavariable and ellipsis pattern tokens, usable
                // with any grammar (ENFAStepLexer-StepParser issue #65).
                case @"\$[A-Z_][A-Z0-9_]*":
                    return MatchMetavariable(input);
                case @"\.\.\.":
                    return TryMatchLiteral("...", input, ignoreCase: false);
            }

            if (core.Length == 0)
            {
                return (false, 0, string.Empty);
            }

            if (core.StartsWith("\\Q"))
            {
                return TryMatchQuotedLiteral(core, input, ignoreCase);
            }

            if (core.StartsWith("\\p{") || core.StartsWith("\\P{"))
            {
                return MatchUnicodePropertyPattern(core, input);
            }

            // Plain text without regex metacharacters is a literal pattern
            // (e.g. /abc/ matches "abc"); patterns with unsupported
            // metacharacters do not match rather than matching wrongly.
            if (!ContainsRegexMetacharacter(core))
            {
                return TryMatchLiteral(core, input, ignoreCase);
            }

            // General character-class sequence (issue #83): patterns made of
            // concatenated atoms - character classes ([...] / [^...] with
            // ranges and escapes), '\p{...}' properties, '.' and literal or
            // escaped characters - each with an optional quantifier.
            if (TryMatchCharacterClassSequence(core, input, ignoreCase, out var classMatch))
            {
                return classMatch;
            }

            return (false, 0, string.Empty);
        }

        /// <summary>
        /// A single quantified atom of a general character-class pattern
        /// (issue #83): a code-point predicate plus minimum and maximum
        /// repetition counts.
        /// </summary>
        private sealed class ClassSequenceAtom
        {
            /// <summary>Predicate over a Unicode code point (scalar value).</summary>
            public required Func<uint, bool> MatchesCodePoint { get; init; }

            /// <summary>Minimum repetition count (inclusive).</summary>
            public int MinCount { get; init; }

            /// <summary>Maximum repetition count (inclusive).</summary>
            public int MaxCount { get; init; }
        }

        /// <summary>Cap on quantifier repetition counts to keep matching linear.</summary>
        private const int MaxQuantifierCount = 1_000_000;

        /// <summary>
        /// Try to parse the pattern as a sequence of quantified atoms
        /// (see <see cref="ClassSequenceAtom"/>) and match it greedily
        /// against the input. Unsupported constructs (groups, alternation,
        /// anchors, ...) make the parse fail and the caller falls back to
        /// legacy behavior.
        /// </summary>
        private bool TryMatchCharacterClassSequence(string pattern, ReadOnlySpan<byte> input, bool ignoreCase,
            out (bool success, int length, string text) match)
        {
            if (!TryParseClassSequence(pattern, ignoreCase, out var atoms))
            {
                match = (false, 0, string.Empty);
                return false;
            }

            int position = 0;
            foreach (var atom in atoms)
            {
                int count = 0;
                while (position < input.Length && count < atom.MaxCount)
                {
                    var (codepoint, bytesConsumed) = UTF8Utils.GetNextCodepoint(input, position);
                    if (bytesConsumed == 0)
                    {
                        break;
                    }

                    if (!atom.MatchesCodePoint(codepoint))
                    {
                        break;
                    }

                    position += bytesConsumed;
                    count++;
                }

                if (count < atom.MinCount)
                {
                    // Pattern was supported; it just does not match here.
                    match = (false, 0, string.Empty);
                    return true;
                }
            }

            if (position == 0)
            {
                // Zero-width matches are rejected: the lexer advances by the
                // match length and could not make progress otherwise (same
                // convention as MatchUnicodePropertyPattern).
                match = (false, 0, string.Empty);
                return true;
            }

            var text = Encoding.UTF8.GetString(input.Slice(0, position));
            match = (true, position, text);
            return true;
        }

        /// <summary>
        /// Parse a pattern into a sequence of quantified atoms. Supported
        /// atoms: character classes ([...] / [^...]) with ranges and
        /// escapes, '\p{Name}' / '\P{Name}' properties, the any-character
        /// atom '.', shorthand classes (\d, \w, \s and their negations) and
        /// literal or escaped characters. Supported quantifiers: +, *, ?,
        /// {n}, {n,}, {n,m}. Anything else - groups, alternation, anchors -
        /// is unsupported.
        /// </summary>
        private bool TryParseClassSequence(string pattern, bool ignoreCase, out List<ClassSequenceAtom> atoms)
        {
            atoms = new List<ClassSequenceAtom>();
            if (pattern.Length == 0)
            {
                return false;
            }

            int i = 0;
            while (i < pattern.Length)
            {
                Func<uint, bool> predicate;

                if (pattern[i] == '[')
                {
                    if (!TryParseCharacterClass(pattern, ref i, out predicate))
                    {
                        return false;
                    }
                }
                else if (pattern[i] == '.')
                {
                    i++;
                    // PCRE default: '.' matches any code point except LF.
                    predicate = static cp => cp != '\n';
                }
                else if (pattern[i] == '\\')
                {
                    if (!TryParseEscapeAtom(pattern, ref i, out predicate))
                    {
                        return false;
                    }
                }
                else if (!char.IsWhiteSpace(pattern[i]))
                {
                    // A single literal character (any code point); the
                    // remaining syntax characters are not literals.
                    var (cp, consumed) = DecodePatternCodepoint(pattern, i);
                    if (consumed == 0 || IsUnsupportedLiteral(cp))
                    {
                        return false;
                    }
                    i += consumed;
                    predicate = cp2 => cp2 == cp;
                }
                else
                {
                    // Quantifiers are handled below; other syntax characters
                    // (parens, braces, alternation, anchors) are unsupported.
                    return false;
                }

                // Parse the optional quantifier that follows the atom.
                if (!TryParseInlineQuantifier(pattern, ref i, out int minCount, out int maxCount))
                {
                    return false;
                }

                atoms.Add(new ClassSequenceAtom
                {
                    MatchesCodePoint = ignoreCase ? WrapIgnoreCase(predicate) : predicate,
                    MinCount = minCount,
                    MaxCount = maxCount
                });
            }

            return atoms.Count > 0;
        }

        /// <summary>
        /// Wrap a code-point predicate so it also accepts the opposite case
        /// of each matching letter (ordinal case folding, limited to the
        /// BMP as in TryMatchLiteral(string, ReadOnlySpan&lt;byte&gt;, bool)).
        /// </summary>
        private static Func<uint, bool> WrapIgnoreCase(Func<uint, bool> predicate)
        {
            return cp =>
            {
                if (predicate(cp))
                {
                    return true;
                }
                if (cp <= char.MaxValue)
                {
                    var c = (char)cp;
                    var lower = char.ToLowerInvariant(c);
                    var upper = char.ToUpperInvariant(c);
                    return (lower != c && predicate(lower)) || (upper != c && predicate(upper));
                }
                return false;
            };
        }

        /// <summary>
        /// The syntax characters that can never start a literal atom in a
        /// supported character-class sequence: they denote structure
        /// (groups, alternation, anchors) this simplified matcher cannot
        /// evaluate without backtracking.
        /// </summary>
        private static bool IsUnsupportedLiteral(uint cp) =>
            cp is '(' or ')' or '|' or '{' or '}' or '^' or '$';

        /// <summary>
        /// Decode one code point from the pattern starting at
        /// <paramref name="index"/> (surrogate pairs become one atom).
        /// </summary>
        private static (uint codepoint, int consumed) DecodePatternCodepoint(string pattern, int index)
        {
            char hi = pattern[index];
            if (char.IsHighSurrogate(hi) && index + 1 < pattern.Length && char.IsLowSurrogate(pattern[index + 1]))
            {
                return ((uint)char.ConvertToUtf32(hi, pattern[index + 1]), 2);
            }
            if (char.IsSurrogate(hi))
            {
                return (0, 0); // lone surrogate: invalid
            }
            return (hi, 1);
        }

        /// <summary>
        /// Parse an inline quantifier at <paramref name="index"/>: one of
        /// +, *, ?, {n}, {n,} or {n,m}. A missing quantifier means
        /// "exactly once". A trailing '?' (lazy quantifier) makes the atom
        /// match exactly its minimum count: this engine never backtracks,
        /// so there is no observable difference between "match as few as
        /// possible and give back" and "match exactly the minimum".
        /// </summary>
        private static bool TryParseInlineQuantifier(string pattern, ref int index, out int minCount, out int maxCount)
        {
            minCount = 1;
            maxCount = 1;

            if (index >= pattern.Length)
            {
                return true;
            }

            char c = pattern[index];
            if (c == '+')
            {
                index++;
                minCount = 1;
                maxCount = MaxQuantifierCount;
                return TryConsumeLazyMarker(pattern, ref index, ref minCount, ref maxCount);
            }
            if (c == '*')
            {
                index++;
                minCount = 0;
                maxCount = MaxQuantifierCount;
                return TryConsumeLazyMarker(pattern, ref index, ref minCount, ref maxCount);
            }
            if (c == '?')
            {
                index++;
                minCount = 0;
                maxCount = 1;
                // A second '?' ("??") is the lazy marker.
                if (index < pattern.Length && pattern[index] == '?')
                {
                    index++;
                    maxCount = minCount;
                }
                return true;
            }
            if (c != '{')
            {
                return true;
            }

            int close = pattern.IndexOf('}', index + 1);
            if (close < 0)
            {
                return false;
            }

            var body = pattern.Substring(index + 1, close - index - 1);
            var commaIndex = body.IndexOf(',');
            var minText = commaIndex < 0 ? body : body[..commaIndex];
            var maxText = commaIndex < 0 ? body : body[(commaIndex + 1)..];

            if (minText.Length == 0 || !int.TryParse(minText, out minCount) || minCount < 0)
            {
                return false;
            }

            if (commaIndex < 0)
            {
                maxCount = minCount;
            }
            else if (maxText.Length == 0)
            {
                maxCount = MaxQuantifierCount;
            }
            else if (!int.TryParse(maxText, out maxCount) || maxCount < minCount)
            {
                return false;
            }

            index = close + 1;
            return TryConsumeLazyMarker(pattern, ref index, ref minCount, ref maxCount);
        }

        /// <summary>
        /// Consume an optional lazy quantifier marker ('?' after a
        /// quantifier). This engine never backtracks, so a lazy atom
        /// matches exactly its minimum count.
        /// </summary>
        private static bool TryConsumeLazyMarker(string pattern, ref int index, ref int minCount, ref int maxCount)
        {
            if (index < pattern.Length && pattern[index] == '?')
            {
                index++;
                maxCount = minCount;
            }
            return true;
        }

        /// <summary>
        /// Parse a character class ([...] or [^...]) into a code-point
        /// predicate. Ranges, literal characters, shorthand classes and the
        /// usual control escapes are honored; an unclosed class or an
        /// unsupported escape fails the parse.
        /// </summary>
        private static bool TryParseCharacterClass(string pattern, ref int index, out Func<uint, bool> predicate)
        {
            predicate = static _ => false;

            int i = index + 1; // skip '['
            bool negated = i < pattern.Length && pattern[i] == '^';
            if (negated)
            {
                i++;
            }

            // ']' as the first item is a literal ']' (PCRE semantics).
            bool first = true;
            var ranges = new List<(uint lo, uint hi)>();
            var shorthands = new List<Func<uint, bool>>();

            while (i < pattern.Length && (pattern[i] != ']' || first))
            {
                first = false;

                // Shorthand classes (\d, \w, \s, ...) are valid class items.
                if (pattern[i] == '\\' && i + 1 < pattern.Length
                    && pattern[i + 1] is 'd' or 'D' or 'w' or 'W' or 's' or 'S' or 'h' or 'H')
                {
                    if (!TryGetShorthandPredicate(pattern[i + 1], out var shorthandPredicate))
                    {
                        return false;
                    }
                    shorthands.Add(shorthandPredicate);
                    i += 2;
                    continue;
                }

                if (!TryParseClassItem(pattern, ref i, out uint lo))
                {
                    return false;
                }

                // Range: item '-' item ('-' as the last item is a literal).
                uint hi = lo;
                if (i < pattern.Length && pattern[i] == '-' && i + 1 < pattern.Length && pattern[i + 1] != ']')
                {
                    i++;
                    if (!TryParseClassItem(pattern, ref i, out hi) || hi < lo)
                    {
                        return false;
                    }
                }

                ranges.Add((lo, hi));
            }

            if (i >= pattern.Length || pattern[i] != ']')
            {
                return false; // unterminated class
            }

            index = i + 1;
            var localRanges = ranges.ToArray();
            var localShorthands = shorthands.ToArray();

            if (negated)
            {
                predicate = cp => !IsCodePointInClass(cp, localRanges, localShorthands);
            }
            else
            {
                predicate = cp => IsCodePointInClass(cp, localRanges, localShorthands);
            }

            return true;
        }

        /// <summary>
        /// Whether a code point is covered by the ranges and shorthand
        /// classes of a character class.
        /// </summary>
        private static bool IsCodePointInClass(uint cp, (uint lo, uint hi)[] ranges, Func<uint, bool>[] shorthands)
        {
            foreach (var (lo, hi) in ranges)
            {
                if (cp >= lo && cp <= hi)
                {
                    return true;
                }
            }

            foreach (var shorthand in shorthands)
            {
                if (shorthand(cp))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Parse a single character-class item (one code point, honoring
        /// escape sequences) starting at <paramref name="index"/>.
        /// </summary>
        private static bool TryParseClassItem(string pattern, ref int index, out uint value)
        {
            value = 0;
            if (index >= pattern.Length)
            {
                return false;
            }

            if (pattern[index] == '\\')
            {
                if (!TryDecodeSingleEscape(pattern, index, out value, out int consumed))
                {
                    return false;
                }

                index += consumed;
                return true;
            }

            var (cp, bytes) = DecodePatternCodepoint(pattern, index);
            if (bytes == 0)
            {
                return false;
            }

            value = cp;
            index += bytes;
            return true;
        }

        /// <summary>
        /// Parse an escape-sequence atom outside a character class into a
        /// code-point predicate (control escapes, shorthand classes, hex
        /// escapes and escaped literals).
        /// </summary>
        private static bool TryParseEscapeAtom(string pattern, ref int index, out Func<uint, bool> predicate)
        {
            predicate = static _ => false;
            if (index + 1 >= pattern.Length)
            {
                return false;
            }

            char c = pattern[index + 1];
            if (c is 'p' or 'P')
            {
                // \p{Name} / \P{Name} property escape.
                int braceStart = index + 2;
                if (braceStart >= pattern.Length || pattern[braceStart] != '{')
                {
                    return false;
                }

                int close = pattern.IndexOf('}', braceStart + 1);
                if (close < 0)
                {
                    return false;
                }

                var name = pattern.Substring(braceStart + 1, close - braceStart - 1);
                bool negated = c == 'P';
                if (!UnicodePropertyValidator.IsValidPropertyName(name))
                {
                    return false;
                }

                index = close + 1;
                predicate = cp => UnicodePropertyMatcher.MatchesProperty((int)cp, name) != negated;
                return true;
            }

            if (TryGetShorthandPredicate(c, out var shorthand))
            {
                index += 2;
                predicate = shorthand;
                return true;
            }

            if (!TryDecodeSingleEscape(pattern, index, out uint value, out int consumed))
            {
                return false;
            }

            index += consumed;
            predicate = cp => cp == value;
            return true;
        }

        /// <summary>
        /// Decode a single-character escape sequence starting at the
        /// backslash at <paramref name="index"/> into its code point.
        /// </summary>
        private static bool TryDecodeSingleEscape(string pattern, int index, out uint value, out int consumed)
        {
            value = 0;
            consumed = 0;
            if (index >= pattern.Length || pattern[index] != '\\' || index + 1 >= pattern.Length)
            {
                return false;
            }

            char c = pattern[index + 1];
            switch (c)
            {
                case 'n': value = '\n'; consumed = 2; return true;
                case 'r': value = '\r'; consumed = 2; return true;
                case 't': value = '\t'; consumed = 2; return true;
                case 'f': value = '\f'; consumed = 2; return true;
                case 'v': value = '\v'; consumed = 2; return true;
                case 'a': value = '\a'; consumed = 2; return true;
                case 'e': value = '\u001B'; consumed = 2; return true;
                case '0': value = '\0'; consumed = 2; return true;
                case 'x':
                {
                    if (index + 3 < pattern.Length
                        && TryParseHexByte(pattern.Substring(index + 2, 2), out value))
                    {
                        consumed = 4;
                        return true;
                    }
                    // \x{HHHH} (PCRE) - one to four hex digits.
                    int braceStart = index + 2;
                    if (braceStart < pattern.Length && pattern[braceStart] == '{')
                    {
                        int close = pattern.IndexOf('}', braceStart + 1);
                        if (close > braceStart + 1 && close - braceStart - 1 <= 4)
                        {
                            if (TryParseHexNumber(pattern.Substring(braceStart + 1, close - braceStart - 1), out value))
                            {
                                consumed = close - index + 1;
                                return true;
                            }
                        }
                    }
                    return false;
                }
                default:
                    // Escaped literal (includes \\, \], \-, \^, \/ ...).
                    var (cp, bytes) = DecodePatternCodepoint(pattern, index + 1);
                    if (bytes == 0)
                    {
                        return false;
                    }
                    value = cp;
                    consumed = bytes + 1;
                    return true;
            }
        }

        /// <summary>
        /// Resolve a shorthand character class letter (d, D, w, W, s, S,
        /// h, H) into its code-point predicate.
        /// </summary>
        private static bool TryGetShorthandPredicate(char c, out Func<uint, bool> predicate)
        {
            switch (c)
            {
                case 'd':
                    predicate = static cp => cp is >= '0' and <= '9';
                    return true;
                case 'D':
                    predicate = static cp => cp is < '0' or > '9';
                    return true;
                case 'w':
                    predicate = static cp => cp is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '_';
                    return true;
                case 'W':
                    predicate = static cp => cp is not ((>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '_');
                    return true;
                case 's':
                    predicate = static cp => cp is ' ' or '\t' or '\r' or '\n' or '\f' or '\v';
                    return true;
                case 'S':
                    predicate = static cp => cp is not (' ' or '\t' or '\r' or '\n' or '\f' or '\v');
                    return true;
                case 'h':
                    predicate = static cp => cp is ' ' or '\t';
                    return true;
                case 'H':
                    predicate = static cp => cp is not (' ' or '\t');
                    return true;
                default:
                    predicate = static _ => false;
                    return false;
            }
        }

        /// <summary>Parse exactly two hex digits into a code point.</summary>
        private static bool TryParseHexByte(string text, out uint value)
        {
            value = 0;
            return text.Length == 2 && TryParseHexNumber(text, out value);
        }

        /// <summary>Parse one to four hex digits into a code point.</summary>
        private static bool TryParseHexNumber(string text, out uint value)
        {
            value = 0;
            if (text.Length is < 1 or > 4)
            {
                return false;
            }

            foreach (var c in text)
            {
                int digit = c switch
                {
                    >= '0' and <= '9' => c - '0',
                    >= 'a' and <= 'f' => c - 'a' + 10,
                    >= 'A' and <= 'F' => c - 'A' + 10,
                    _ => -1
                };
                if (digit < 0)
                {
                    return false;
                }
                value = (value << 4) | (uint)digit;
            }

            return true;
        }

        /// <summary>
        /// Determines whether the pattern body contains a regular expression
        /// metacharacter that this simplified matcher cannot handle as plain
        /// literal text.
        /// </summary>
        /// <param name="pattern">The pattern body.</param>
        /// <returns><see langword="true"/> if the pattern contains a metacharacter; otherwise <see langword="false"/>.</returns>
        private static bool ContainsRegexMetacharacter(string pattern)
        {
            const string Metacharacters = @"\()[]{}*+?.|^$";
            for (int i = 0; i < pattern.Length; i++)
            {
                if (Metacharacters.IndexOf(pattern[i]) >= 0)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Preprocess a regex pattern body: remove <c>(?#...)</c> comments,
        /// extract bare inline modifier groups (<c>(?i)</c>, <c>(?im)</c>,
        /// <c>(?-i)</c>, ...), strip atomic grouping markers (<c>(?&gt;...)</c>)
        /// and possessive quantifier markers (<c>++</c>, <c>*+</c>, <c>?+</c>),
        /// and apply extended-mode (<c>(?x)</c>) whitespace and <c>#</c>
        /// line-comment stripping.
        /// </summary>
        /// <param name="pattern">The pattern body (without the surrounding slashes).</param>
        /// <returns>The preprocessed pattern and whether case-insensitive matching is enabled.</returns>
        /// <remarks>
        /// Inline modifiers are applied globally regardless of position in the
        /// pattern; scoped modifier groups (<c>(?i:...)</c>) are not supported
        /// and are left untouched. Of the recognized flags, only <c>i</c> and
        /// <c>x</c> have an observable effect on this simplified matcher;
        /// <c>m</c>, <c>s</c>, <c>J</c> and <c>U</c> are accepted as no-ops.
        /// Atomic groups and possessive quantifiers are unwrapped to their
        /// greedy equivalents because the step lexer is a forward-parsing
        /// matcher that never backtracks: once input has been consumed by a
        /// token it is never given back, which is exactly the semantic
        /// guarantee atomic grouping provides in a backtracking engine.
        /// Extended-mode whitespace stripping skips escape sequences, quoted
        /// literal sequences (<c>\Q...\E</c>) and character classes, matching
        /// PCRE2 semantics.
        /// </remarks>
        private (string core, bool ignoreCase) PreprocessRegexPattern(string pattern)
        {
            if (pattern.Length == 0)
            {
                return (pattern, false);
            }

            bool ignoreCase = false;
            bool extended = false;
            bool modified = false;
            bool inQuotedLiteral = false;
            bool inCharClass = false;
            bool lastEmittedWasQuantifier = false;
            var parenIsAtomic = new Stack<bool>();
            var sb = new StringBuilder(pattern.Length);
            int i = 0;

            while (i < pattern.Length)
            {
                char c = pattern[i];

                // Escape sequence: copy verbatim (also toggles \Q quoting)
                if (c == '\\' && i + 1 < pattern.Length && !inQuotedLiteral && !inCharClass)
                {
                    if (pattern[i + 1] == 'Q')
                    {
                        inQuotedLiteral = true;
                    }
                    else if (pattern[i + 1] == 'E')
                    {
                        inQuotedLiteral = false;
                    }
                    sb.Append(c).Append(pattern[i + 1]);
                    lastEmittedWasQuantifier = false;
                    i += 2;
                    continue;
                }

                if (inQuotedLiteral)
                {
                    if (c == '\\' && i + 1 < pattern.Length && pattern[i + 1] == 'E')
                    {
                        inQuotedLiteral = false;
                        sb.Append("\\E");
                        lastEmittedWasQuantifier = false;
                        i += 2;
                        continue;
                    }
                    sb.Append(c);
                    lastEmittedWasQuantifier = false;
                    i++;
                    continue;
                }

                if (inCharClass)
                {
                    if (c == ']')
                    {
                        inCharClass = false;
                    }
                    sb.Append(c);
                    lastEmittedWasQuantifier = false;
                    i++;
                    continue;
                }

                // Comment group (?#...) - removed entirely
                if (c == '(' && i + 2 < pattern.Length && pattern[i + 1] == '?' && pattern[i + 2] == '#')
                {
                    int close = pattern.IndexOf(')', i + 3);
                    i = close < 0 ? pattern.Length : close + 1;
                    modified = true;
                    continue;
                }

                // Atomic group (?>...) - the opening marker and its matching
                // closing parenthesis are removed while the contents are
                // kept. The step lexer never backtracks, so every match is
                // already atomic; unwrapping preserves the matched text.
                if (c == '(' && i + 2 < pattern.Length && pattern[i + 1] == '?' && pattern[i + 2] == '>')
                {
                    parenIsAtomic.Push(true);
                    i += 3;
                    modified = true;
                    continue;
                }

                // Inline modifier group (?flags) or (?on-off)
                if (c == '(' && i + 1 < pattern.Length && pattern[i + 1] == '?')
                {
                    int close = pattern.IndexOf(')', i + 2);
                    if (close > i + 2 && TryReadModifierFlags(pattern.AsSpan(i + 2, close - (i + 2)), out var onFlags, out var offFlags))
                    {
                        ApplyModifierFlags(onFlags, ref ignoreCase, ref extended, true);
                        ApplyModifierFlags(offFlags, ref ignoreCase, ref extended, false);
                        i = close + 1;
                        modified = true;
                        continue;
                    }
                }

                // Any other opening parenthesis is tracked so that the
                // matching closing parenthesis of an atomic group can be
                // dropped even when groups are nested.
                if (c == '(')
                {
                    parenIsAtomic.Push(false);
                    sb.Append(c);
                    lastEmittedWasQuantifier = false;
                    i++;
                    continue;
                }

                // Closing parenthesis: dropped when it closes an atomic group.
                if (c == ')')
                {
                    if (parenIsAtomic.Count > 0 && parenIsAtomic.Pop())
                    {
                        i++;
                        continue;
                    }

                    sb.Append(c);
                    lastEmittedWasQuantifier = false;
                    i++;
                    continue;
                }

                // Possessive quantifier marker: a '+' immediately following
                // a quantifier ('*', '+', '?') is the possessive form. The
                // step lexer never backtracks, so possessive repetition is
                // equivalent to greedy repetition and the marker is dropped.
                if (c == '+' && lastEmittedWasQuantifier)
                {
                    i++;
                    modified = true;
                    continue;
                }

                if (c == '[')
                {
                    inCharClass = true;
                    sb.Append(c);
                    lastEmittedWasQuantifier = false;
                    i++;
                    continue;
                }

                // Extended mode: ignore unescaped whitespace and # line comments
                if (extended && char.IsWhiteSpace(c))
                {
                    i++;
                    modified = true;
                    continue;
                }
                if (extended && c == '#')
                {
                    int newline = pattern.IndexOf('\n', i);
                    i = newline < 0 ? pattern.Length : newline + 1;
                    modified = true;
                    continue;
                }

                sb.Append(c);
                lastEmittedWasQuantifier = c == '*' || c == '+' || c == '?';
                i++;
            }

            return modified ? (sb.ToString(), ignoreCase) : (pattern, ignoreCase);
        }

        /// <summary>
        /// Try to read the flags of a bare inline modifier group. The content
        /// must consist of the flag letters <c>imsxJUX</c> and at most one
        /// <c>-</c> separator, with at least one flag letter.
        /// </summary>
        /// <param name="content">The group content between <c>(?</c> and <c>)</c>.</param>
        /// <param name="onFlags">Receives the flags enabled by the group.</param>
        /// <param name="offFlags">Receives the flags disabled by the group.</param>
        /// <returns>True when the content is a valid bare modifier group.</returns>
        private static bool TryReadModifierFlags(ReadOnlySpan<char> content, out string onFlags, out string offFlags)
        {
            onFlags = string.Empty;
            offFlags = string.Empty;

            if (content.Length == 0)
            {
                return false;
            }

            int separator = content.IndexOf('-');
            if (separator >= 0)
            {
                onFlags = new string(content.Slice(0, separator));
                offFlags = new string(content.Slice(separator + 1));
                if (content.Slice(separator + 1).Contains('-'))
                {
                    return false;
                }
            }
            else
            {
                onFlags = new string(content);
            }

            var flags = onFlags + offFlags;
            if (flags.Length == 0)
            {
                return false;
            }

            foreach (var f in flags)
            {
                if ("imsxJUX".IndexOf(f) < 0)
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Apply inline modifier flags to the matcher state. Only <c>i</c>
        /// (case-insensitive matching) and <c>x</c> (extended mode) have an
        /// observable effect; the remaining recognized flags are no-ops.
        /// </summary>
        private static void ApplyModifierFlags(string flags, ref bool ignoreCase, ref bool extended, bool enable)
        {
            foreach (var f in flags)
            {
                switch (f)
                {
                    case 'i':
                        ignoreCase = enable;
                        break;
                    case 'x':
                        extended = enable;
                        break;
                }
            }
        }

        /// <summary>
        /// Match a quoted literal sequence pattern (<c>\Q...\E</c>) with an
        /// optional trailing quantifier. The text between <c>\Q</c> and
        /// <c>\E</c> is matched literally; an unterminated <c>\Q</c> runs to
        /// the end of the pattern, matching PCRE2 semantics.
        /// </summary>
        /// <param name="pattern">The preprocessed pattern body starting with <c>\Q</c>.</param>
        /// <param name="input">The remaining UTF-8 input at the current lexer position.</param>
        /// <param name="ignoreCase">Whether to match the literal case-insensitively.</param>
        /// <returns>The greedy match result, or failure for an unsupported trailing quantifier.</returns>
        private (bool success, int length, string text) TryMatchQuotedLiteral(string pattern, ReadOnlySpan<byte> input, bool ignoreCase)
        {
            int end = pattern.IndexOf("\\E", 2, StringComparison.Ordinal);

            string literalText;
            string quantifier;
            if (end < 0)
            {
                literalText = pattern.Substring(2);
                quantifier = string.Empty;
            }
            else
            {
                literalText = pattern.Substring(2, end - 2);
                quantifier = pattern.Substring(end + 2);
            }

            if (literalText.Length == 0)
            {
                return (false, 0, string.Empty);
            }

            if (!TryParseQuantifier(quantifier, out int minCount, out int maxCount))
            {
                return (false, 0, string.Empty);
            }

            // Greedily repeat the literal match over the input.
            int position = 0;
            int count = 0;
            while (count < maxCount)
            {
                var match = TryMatchLiteral(literalText, input.Slice(position), ignoreCase);
                if (!match.success)
                {
                    break;
                }
                position += match.length;
                count++;
            }

            if (count < minCount || position == 0)
            {
                return (false, 0, string.Empty);
            }

            var text = Encoding.UTF8.GetString(input.Slice(0, position));
            return (true, position, text);
        }

        /// <summary>
        /// Match a Unicode property escape pattern (<c>\p{Name}</c> or
        /// <c>\P{Name}</c>) with an optional trailing quantifier
        /// (<c>+</c>, <c>*</c>, <c>?</c>, <c>{n}</c>, <c>{n,}</c>, <c>{n,m}</c>)
        /// against the input. Matching is greedy over whole code points.
        /// </summary>
        /// <param name="pattern">The pattern body (without the surrounding slashes).</param>
        /// <param name="input">The remaining UTF-8 input at the current lexer position.</param>
        /// <returns>The match result, or failure for unknown property names or unsupported trailing syntax.</returns>
        /// <remarks>
        /// Unknown property names are rejected (no match) so that rule
        /// validation surfaces them instead of silently producing tokens.
        /// Zero-length matches (e.g. <c>\p{L}*</c> at a non-matching position)
        /// are reported as failures because the lexer advances by the match
        /// length and could not make progress otherwise.
        /// </remarks>
        private (bool success, int length, string text) MatchUnicodePropertyPattern(string pattern, ReadOnlySpan<byte> input)
        {
            bool negated = pattern[1] == 'P';

            // Find the closing brace of \p{...} / \P{...}
            int closeIndex = pattern.IndexOf('}', 3);
            if (closeIndex < 0)
            {
                return (false, 0, string.Empty);
            }

            var propertyName = pattern.Substring(3, closeIndex - 3);
            if (!UnicodePropertyValidator.IsValidPropertyName(propertyName))
            {
                return (false, 0, string.Empty);
            }

            // Parse the optional quantifier after the closing brace
            var quantifier = pattern.Substring(closeIndex + 1);
            if (!TryParseQuantifier(quantifier, out int minCount, out int maxCount))
            {
                return (false, 0, string.Empty);
            }

            return MatchUnicodeProperty(propertyName, negated, minCount, maxCount, input);
        }

        /// <summary>
        /// Parse a regex quantifier suffix into minimum and maximum repetition counts.
        /// </summary>
        /// <param name="quantifier">The quantifier text (may be empty).</param>
        /// <param name="minCount">Receives the minimum repetition count.</param>
        /// <param name="maxCount">Receives the maximum repetition count.</param>
        /// <returns>True if the quantifier is empty or one of the supported forms.</returns>
        private bool TryParseQuantifier(string quantifier, out int minCount, out int maxCount)
        {
            minCount = 1;
            maxCount = 1;

            switch (quantifier)
            {
                case "":
                    return true;
                case "+":
                    minCount = 1;
                    maxCount = int.MaxValue;
                    return true;
                case "*":
                    minCount = 0;
                    maxCount = int.MaxValue;
                    return true;
                case "?":
                    minCount = 0;
                    maxCount = 1;
                    return true;
            }

            // {n}, {n,} or {n,m}
            if (quantifier.StartsWith("{") && quantifier.EndsWith("}") && quantifier.Length >= 3)
            {
                var body = quantifier.Substring(1, quantifier.Length - 2);
                var commaIndex = body.IndexOf(',');

                var minText = commaIndex < 0 ? body : body.Substring(0, commaIndex);
                var maxText = commaIndex < 0 ? body : body.Substring(commaIndex + 1);

                if (minText.Length == 0 || !int.TryParse(minText, out minCount) || minCount < 0)
                {
                    return false;
                }

                if (commaIndex < 0)
                {
                    maxCount = minCount;
                    return true;
                }

                if (maxText.Length == 0)
                {
                    maxCount = int.MaxValue;
                    return true;
                }

                if (!int.TryParse(maxText, out maxCount) || maxCount < minCount)
                {
                    return false;
                }

                return true;
            }

            return false;
        }

        /// <summary>
        /// Greedily match a run of code points satisfying (or, when negated,
        /// not satisfying) a Unicode property, between the given minimum and
        /// maximum repetition counts.
        /// </summary>
        /// <param name="propertyName">The canonical or loose Unicode property name.</param>
        /// <param name="negated">True for <c>\P{...}</c> (inverted) matching.</param>
        /// <param name="minCount">Minimum number of matching code points.</param>
        /// <param name="maxCount">Maximum number of matching code points.</param>
        /// <param name="input">The remaining UTF-8 input at the current lexer position.</param>
        /// <returns>The match result; zero-length matches are reported as failures.</returns>
        private (bool success, int length, string text) MatchUnicodeProperty(string propertyName, bool negated,
            int minCount, int maxCount, ReadOnlySpan<byte> input)
        {
            int position = 0;
            int count = 0;

            while (position < input.Length && count < maxCount)
            {
                var (codepoint, bytesConsumed) = UTF8Utils.GetNextCodepoint(input, position);
                if (bytesConsumed == 0)
                {
                    break;
                }

                bool matches = UnicodePropertyMatcher.MatchesProperty((int)codepoint, propertyName);
                if (matches == negated)
                {
                    break;
                }

                position += bytesConsumed;
                count++;
            }

            if (count < minCount || position == 0)
            {
                return (false, 0, string.Empty);
            }

            var text = Encoding.UTF8.GetString(input.Slice(0, position));
            return (true, position, text);
        }

        /// <summary>
        /// Match literal string (case-sensitive)
        /// </summary>
        private (bool success, int length, string text) TryMatchLiteral(string literal, ReadOnlySpan<byte> input)
        {
            return TryMatchLiteral(literal, input, ignoreCase: false);
        }

        /// <summary>
        /// Match literal string, optionally case-insensitively using
        /// ordinal Unicode case folding.
        /// </summary>
        /// <param name="literal">The literal text to match.</param>
        /// <param name="input">The remaining UTF-8 input at the current lexer position.</param>
        /// <param name="ignoreCase">Whether to compare case-insensitively.</param>
        /// <returns>The match result with the byte length of the matched input.</returns>
        /// <remarks>
        /// Case-insensitive matches can differ in byte length from the
        /// literal (e.g. the Kelvin sign vs a lowercase k), so the returned
        /// length is derived from the matched input prefix, not the literal.
        /// Only a bounded prefix of the input is decoded for the comparison,
        /// keeping the cost proportional to the literal length.
        /// </remarks>
        private (bool success, int length, string text) TryMatchLiteral(string literal, ReadOnlySpan<byte> input, bool ignoreCase)
        {
            var literalBytes = Encoding.UTF8.GetBytes(literal);

            if (!ignoreCase)
            {
                if (input.Length >= literalBytes.Length && input.StartsWith(literalBytes))
                {
                    return (true, literalBytes.Length, literal);
                }
                return (false, 0, string.Empty);
            }

            // Worst case each literal char needs 4 input bytes (surrogate
            // pairs); decoding a bounded prefix keeps this allocation-free
            // with respect to input length.
            var maxBytes = Math.Min(input.Length, literal.Length * 4);
            var prefix = Encoding.UTF8.GetString(input.Slice(0, maxBytes));

            if (prefix.Length < literal.Length)
            {
                return (false, 0, string.Empty);
            }

            if (!prefix.AsSpan(0, literal.Length).Equals(literal, StringComparison.OrdinalIgnoreCase))
            {
                return (false, 0, string.Empty);
            }

            var matchedText = prefix.Substring(0, literal.Length);
            var matchedBytes = Encoding.UTF8.GetByteCount(matchedText);
            return (true, matchedBytes, matchedText);
        }

        /// <summary>
        /// Match digits pattern
        /// </summary>
        private (bool success, int length, string text) MatchDigits(ReadOnlySpan<byte> input)
        {
            int length = 0;
            while (length < input.Length && input[length] >= '0' && input[length] <= '9')
            {
                length++;
            }
            
            if (length > 0)
            {
                var text = Encoding.UTF8.GetString(input.Slice(0, length));
                return (true, length, text);
            }
            return (false, 0, string.Empty);
        }

        /// <summary>
        /// Match identifier pattern
        /// </summary>
        private (bool success, int length, string text) MatchIdentifier(ReadOnlySpan<byte> input)
        {
            if (input.Length == 0) return (false, 0, string.Empty);
            
            int length = 0;
            var first = input[0];
            
            // First character must be letter
            if (!((first >= 'a' && first <= 'z') || (first >= 'A' && first <= 'Z')))
                return (false, 0, string.Empty);
            
            length = 1;
            
            // Subsequent characters can be letters or digits
            while (length < input.Length)
            {
                var ch = input[length];
                if (!((ch >= 'a' && ch <= 'z') || (ch >= 'A' && ch <= 'Z') || (ch >= '0' && ch <= '9')))
                    break;
                length++;
            }
            
            var text = Encoding.UTF8.GetString(input.Slice(0, length));
            return (true, length, text);
        }

        /// <summary>
        /// Match whitespace pattern
        /// </summary>
        private (bool success, int length, string text) MatchWhitespace(ReadOnlySpan<byte> input)
        {
            int length = 0;
            while (length < input.Length)
            {
                var ch = input[length];
                if (!(ch == ' ' || ch == '\t' || ch == '\r' || ch == '\n'))
                    break;
                length++;
            }
            
            if (length > 0)
            {
                var text = Encoding.UTF8.GetString(input.Slice(0, length));
                return (true, length, text);
            }
            return (false, 0, string.Empty);
        }

        /// <summary>
        /// Matches the standard metavariable pattern
        /// <c>$[A-Z_][A-Z0-9_]*</c> (issue #65): a literal dollar sign
        /// followed by an uppercase identifier.
        /// </summary>
        /// <param name="input">The remaining input.</param>
        /// <returns>The match result with the number of bytes consumed.</returns>
        private (bool success, int length, string text) MatchMetavariable(ReadOnlySpan<byte> input)
        {
            if (input.Length < 2 || input[0] != (byte)'$')
            {
                return (false, 0, string.Empty);
            }

            if (!(input[1] == (byte)'_' || (input[1] >= (byte)'A' && input[1] <= (byte)'Z')))
            {
                return (false, 0, string.Empty);
            }

            int length = 2;
            while (length < input.Length)
            {
                var ch = input[length];
                if (!(ch == (byte)'_' || (ch >= (byte)'A' && ch <= (byte)'Z') || (ch >= (byte)'0' && ch <= (byte)'9')))
                    break;
                length++;
            }

            var text = Encoding.UTF8.GetString(input.Slice(0, length));
            return (true, length, text);
        }
    }
}