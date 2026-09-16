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
        /// Simple regex matching (placeholder - would use proper regex in real implementation)
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
                    return (false, 0, string.Empty);
            }
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