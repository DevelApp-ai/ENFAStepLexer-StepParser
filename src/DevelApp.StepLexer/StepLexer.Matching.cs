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
            path.Position += length;

            if (!rule.IsSkippable)
            {
                var location = new CodeLocation(_fileName, line, column, line, column + length, path.CurrentContext);
                var token = new StepToken(rule.Name, matchText, location, path.CurrentContext);
                
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
        /// character-class patterns plus Unicode property escapes
        /// (<c>\p{Name}</c>/<c>\P{Name}</c>) with an optional quantifier.
        /// </summary>
        private (bool success, int length, string text) TryMatchRegex(string pattern, ReadOnlySpan<byte> input)
        {
            // Placeholder implementation for common patterns
            switch (pattern)
            {
                case "[0-9]+":
                    return MatchDigits(input);
                case "[a-zA-Z][a-zA-Z0-9]*":
                    return MatchIdentifier(input);
                case "[ \\t\\r\\n]+":
                    return MatchWhitespace(input);
                default:
                    // Unicode property escape: \p{Name} or \P{Name} with optional quantifier
                    if (pattern.StartsWith("\\p{") || pattern.StartsWith("\\P{"))
                    {
                        return MatchUnicodePropertyPattern(pattern, input);
                    }
                    return (false, 0, string.Empty);
            }
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
        /// Match literal string
        /// </summary>
        private (bool success, int length, string text) TryMatchLiteral(string literal, ReadOnlySpan<byte> input)
        {
            var literalBytes = Encoding.UTF8.GetBytes(literal);
            if (input.Length >= literalBytes.Length && input.StartsWith(literalBytes))
            {
                return (true, literalBytes.Length, literal);
            }
            return (false, 0, string.Empty);
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