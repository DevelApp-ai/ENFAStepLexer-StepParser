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
            
            // Default to literal match
            return TryMatchLiteral(pattern, input);
        }

        /// <summary>
        /// Regex matching for the supported pattern set: the common literal
        /// character-class patterns, Unicode property escapes
        /// (<c>\p{Name}</c>/<c>\P{Name}</c>) with an optional quantifier,
        /// quoted literal sequences (<c>\Q...\E</c>) and plain literal text,
        /// with inline modifiers (<c>(?i)</c>, <c>(?x)</c>, ...) and
        /// <c>(?#...)</c> comments handled during preprocessing.
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

            return (false, 0, string.Empty);
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
        /// <c>(?-i)</c>, ...) and apply extended-mode (<c>(?x)</c>) whitespace
        /// and <c>#</c> line-comment stripping.
        /// </summary>
        /// <param name="pattern">The pattern body (without the surrounding slashes).</param>
        /// <returns>The preprocessed pattern and whether case-insensitive matching is enabled.</returns>
        /// <remarks>
        /// Inline modifiers are applied globally regardless of position in the
        /// pattern; scoped modifier groups (<c>(?i:...)</c>) are not supported
        /// and are left untouched. Of the recognized flags, only <c>i</c> and
        /// <c>x</c> have an observable effect on this simplified matcher;
        /// <c>m</c>, <c>s</c>, <c>J</c> and <c>U</c> are accepted as no-ops.
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
                    i += 2;
                    continue;
                }

                if (inQuotedLiteral)
                {
                    if (c == '\\' && i + 1 < pattern.Length && pattern[i + 1] == 'E')
                    {
                        inQuotedLiteral = false;
                        sb.Append("\\E");
                        i += 2;
                        continue;
                    }
                    sb.Append(c);
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

                if (c == '[')
                {
                    inCharClass = true;
                    sb.Append(c);
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
    }
}