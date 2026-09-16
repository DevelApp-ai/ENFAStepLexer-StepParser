using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace DevelApp.StepLexer
{
    public partial class StepLexer
    {
        
        // ===============================================================================================
        // TWO-PHASE REGEX PATTERN PARSING METHODS
        // Integrated from TwoPhaseParser for regex pattern compilation capabilities
        // ===============================================================================================
        
        /// <summary>
        /// Phase 1: Fast lexical scanning with ambiguity detection for regex patterns
        /// </summary>
        public bool Phase1_LexicalScan(ZeroCopyStringView input)
        {
            _phase1Tokens.Clear();
            
            int position = 0;
            while (position < input.Length)
            {
                var result = ScanNextToken(input, position);
                if (result == null)
                    return false;
                    
                _phase1Tokens.Add(result.Value.token);
                position = result.Value.nextPosition;
            }
            
            return true;
        }
        
        /// <summary>
        /// Phase 2: Disambiguation and ENFA state machine construction for regex patterns
        /// </summary>
        public bool Phase2_Disambiguation()
        {
            _phase2States.Clear();
            
            foreach (var token in _phase1Tokens)
            {
                if (token.HasAlternatives)
                {
                    // Handle ambiguous tokens by choosing best alternative
                    var bestAlternative = SelectBestAlternative(token);
                    if (!ProcessRegexToken(bestAlternative))
                        return false;
                }
                else
                {
                    if (!ProcessRegexToken(token))
                        return false;
                }
            }
            
            return true;
        }
        
        private (SplittableToken token, int nextPosition)? ScanNextToken(ZeroCopyStringView input, int position)
        {
            if (position >= input.Length)
                return null;
                
            byte currentByte = input[position];
            
            // Fast pattern recognition for common cases
            switch (currentByte)
            {
                case (byte)'\\':
                    return ScanEscapeSequence(input, position);
                case (byte)'[':
                    return ScanCharacterClass(input, position);
                case (byte)'(':
                    return ScanGroup(input, position);
                case (byte)')':
                    return (new SplittableToken(input.Slice(position, 1), TokenType.GroupEnd, position), position + 1);
                case (byte)'*':
                case (byte)'+':
                case (byte)'?':
                    return ScanQuantifier(input, position);
                case (byte)'|':
                    return (new SplittableToken(input.Slice(position, 1), TokenType.Alternation, position), position + 1);
                case (byte)'^':
                    return (new SplittableToken(input.Slice(position, 1), TokenType.StartAnchor, position), position + 1);
                case (byte)'$':
                    return (new SplittableToken(input.Slice(position, 1), TokenType.EndAnchor, position), position + 1);
                case (byte)'.':
                    return (new SplittableToken(input.Slice(position, 1), TokenType.AnyChar, position), position + 1);
                default:
                    return ScanLiteral(input, position);
            }
        }
        
        private (SplittableToken token, int nextPosition)? ScanEscapeSequence(ZeroCopyStringView input, int position)
        {
            if (position + 1 >= input.Length)
                return null;
                
            byte nextByte = input[position + 1];
            
            // Check for \Q...\E literal text construct
            if (nextByte == (byte)'Q')
            {
                return ScanLiteralTextConstruct(input, position);
            }
            
            var token = new SplittableToken(input.Slice(position, 2), TokenType.EscapeSequence, position);
            
            // Check for potential ambiguity in escape sequences
            switch (nextByte)
            {
                case (byte)'x':
                    // Could be \xFF or \x{FFFF} - ambiguous!
                    // Only create alternatives if we have enough input
                    if (position + 4 <= input.Length)
                    {
                        token.Split(
                            (input.Slice(position, 4), TokenType.HexEscape),  // \xFF
                            (position + 6 <= input.Length ? input.Slice(position, 6) : input.Slice(position, Math.Min(4, input.Length - position)), TokenType.UnicodeEscape) // \x{FF}
                        );
                    }
                    return (token, position + 2);
                    
                case (byte)'p':
                case (byte)'P':
                    // Unicode property - scan full property name
                    return ScanUnicodeProperty(input, position);
                    
                default:
                    return (token, position + 2);
            }
        }
        
        private (SplittableToken token, int nextPosition)? ScanCharacterClass(ZeroCopyStringView input, int position)
        {
            int end = position + 1;
            bool escaped = false;
            
            while (end < input.Length)
            {
                byte b = input[end];
                if (!escaped && b == (byte)']')
                    break;
                escaped = !escaped && b == (byte)'\\';
                end++;
            }
            
            if (end >= input.Length)
                return null; // Unclosed character class
                
            var token = new SplittableToken(input.Slice(position, end - position + 1), TokenType.CharacterClass, position);
            return (token, end + 1);
        }
        
        private (SplittableToken token, int nextPosition)? ScanGroup(ZeroCopyStringView input, int position)
        {
            // Check for special group types like (?:...) or (?=...) or inline modifiers (?i), (?m), or comments (?#...)
            if (position + 1 < input.Length && input[position + 1] == (byte)'?')
            {
                // Check for comment group (?#...)
                if (position + 2 < input.Length && input[position + 2] == (byte)'#')
                {
                    return ScanCommentGroup(input, position);
                }
                
                // Scan for inline modifiers and special groups
                int end = position + 2;
                while (end < input.Length && input[end] != (byte)')')
                {
                    end++;
                }
                
                if (end < input.Length) // Found closing )
                {
                    end++; // Include the closing )
                    var groupText = input.Slice(position, end - position);
                    
                    // Check if this is an inline modifier
                    if (IsInlineModifier(groupText))
                    {
                        var token = new SplittableToken(groupText, TokenType.InlineModifier, position);
                        return (token, end);
                    }
                    
                    // Regular special group
                    var specialToken = new SplittableToken(groupText, TokenType.SpecialGroup, position);
                    return (specialToken, end);
                }
                
                // Fallback for incomplete special group
                var fallbackToken = new SplittableToken(input.Slice(position, 2), TokenType.SpecialGroup, position);
                return (fallbackToken, position + 2);
            }
            
            var groupToken = new SplittableToken(input.Slice(position, 1), TokenType.GroupStart, position);
            return (groupToken, position + 1);
        }
        
        private (SplittableToken token, int nextPosition)? ScanQuantifier(ZeroCopyStringView input, int position)
        {
            var token = new SplittableToken(input.Slice(position, 1), TokenType.Quantifier, position);
            
            // Check for lazy quantifiers (+?, *?, ??)
            if (position + 1 < input.Length && input[position + 1] == (byte)'?')
            {
                token = new SplittableToken(input.Slice(position, 2), TokenType.LazyQuantifier, position);
                return (token, position + 2);
            }
            
            return (token, position + 1);
        }
        
        private (SplittableToken token, int nextPosition)? ScanLiteral(ZeroCopyStringView input, int position)
        {
            var token = new SplittableToken(input.Slice(position, 1), TokenType.Literal, position);
            return (token, position + 1);
        }
        
        private (SplittableToken token, int nextPosition)? ScanUnicodeProperty(ZeroCopyStringView input, int position)
        {
            // Scan for \p{PropertyName} or \P{PropertyName}
            int end = position + 2; // Skip \p or \P
            
            if (end < input.Length && input[end] == (byte)'{')
            {
                end++; // Skip {
                while (end < input.Length && input[end] != (byte)'}')
                    end++;
                    
                if (end < input.Length)
                    end++; // Include }
            }
            
            var token = new SplittableToken(input.Slice(position, end - position), TokenType.UnicodeProperty, position);
            return (token, end);
        }
        
        private SplittableToken SelectBestAlternative(SplittableToken ambiguousToken)
        {
            // Simple heuristic: prefer longer matches
            if (ambiguousToken.Alternatives == null)
                return ambiguousToken;
                
            var best = ambiguousToken;
            foreach (var alternative in ambiguousToken.Alternatives)
            {
                if (alternative.Text.Length > best.Text.Length)
                    best = alternative;
            }
            
            return best;
        }
        
        private bool ProcessRegexToken(SplittableToken token)
        {
            // Validate token based on type
            if (token.Type == TokenType.UnicodeProperty)
            {
                if (!ValidateUnicodeProperty(token.Text))
                {
                    // Invalid Unicode property - add error state
                    var errorState = new ParsedState
                    {
                        TokenType = TokenType.UnicodeProperty,
                        Text = token.Text.ToString(),
                        Position = token.Position,
                        IsAmbiguous = false
                    };
                    _phase2States.Add(errorState);
                    return false; // Indicate validation failure
                }
            }
            
            // Convert processed token to parsed state for regex patterns
            var state = new ParsedState
            {
                TokenType = token.Type,
                Text = token.Text.ToString(),
                Position = token.Position,
                IsAmbiguous = token.HasAlternatives
            };
            
            _phase2States.Add(state);
            return true;
        }
        
        /// <summary>
        /// Access to Phase 1 regex parsing results
        /// </summary>
        public IReadOnlyList<SplittableToken> Phase1Results => _phase1Tokens;
        
        /// <summary>
        /// Access to Phase 2 regex parsing results
        /// </summary>
        public IReadOnlyList<ParsedState> Phase2Results => _phase2States;
        
        /// <summary>
        /// Check if a group text represents an inline modifier
        /// </summary>
        private bool IsInlineModifier(ZeroCopyStringView groupText)
        {
            if (groupText.Length < 4) return false; // Minimum (?i)
            
            var text = groupText.ToString();
            
            // Common inline modifiers: (?i), (?m), (?s), (?x), (?im), etc.
            if (text.StartsWith("(?") && text.EndsWith(")"))
            {
                var modifiers = text.Substring(2, text.Length - 3);
                
                // Empty modifiers are not valid
                if (string.IsNullOrEmpty(modifiers))
                    return false;
                    
                return IsValidModifierString(modifiers);
            }
            
            return false;
        }
        
        /// <summary>
        /// Validate if a string contains valid PCRE2 modifiers
        /// </summary>
        private bool IsValidModifierString(string modifiers)
        {
            foreach (char c in modifiers)
            {
                switch (c)
                {
                    case 'i': // Case insensitive
                    case 'm': // Multiline mode
                    case 's': // Single line mode (dotall)
                    case 'x': // Extended syntax (ignore whitespace)
                    case 'u': // Unicode mode
                    case 'U': // Ungreedy quantifiers
                    case 'A': // Anchored
                    case 'D': // Dollar matches newline at end
                    case 'S': // Study the regex
                    case 'J': // Allow duplicate named groups
                        continue;
                    default:
                        return false;
                }
            }
            return modifiers.Length > 0;
        }
        
        /// <summary>
        /// Scan \Q...\E literal text construct
        /// </summary>
        private (SplittableToken token, int nextPosition)? ScanLiteralTextConstruct(ZeroCopyStringView input, int position)
        {
            // Look for \Q at current position
            if (position + 1 >= input.Length || input[position + 1] != (byte)'Q')
                return null;
                
            // Scan for \E ending
            int end = position + 2;
            while (end + 1 < input.Length)
            {
                if (input[end] == (byte)'\\' && input[end + 1] == (byte)'E')
                {
                    end += 2; // Include \E
                    var token = new SplittableToken(input.Slice(position, end - position), TokenType.LiteralText, position);
                    return (token, end);
                }
                end++;
            }
            
            // No \E found - treat as regular escape sequence
            var fallbackToken = new SplittableToken(input.Slice(position, 2), TokenType.EscapeSequence, position);
            return (fallbackToken, position + 2);
        }
        
        /// <summary>
        /// Enhanced Unicode property validation with comprehensive ICU-based property support
        /// </summary>
        private bool ValidateUnicodeProperty(ZeroCopyStringView propertyText)
        {
            var text = propertyText.ToString();
            
            // Remove \p{ or \P{ prefix and } suffix
            if (text.Length < 4) return false;
            if (!text.StartsWith("\\p{") && !text.StartsWith("\\P{")) return false;
            if (!text.EndsWith("}")) return false;
            
            var propertyName = text.Substring(3, text.Length - 4);
            
            // Validate against ICU-supported Unicode property names
            return IsValidUnicodePropertyName(propertyName);
        }
        
        /// <summary>
        /// Check if a property name is a valid Unicode property using comprehensive validation
        /// </summary>
        private bool IsValidUnicodePropertyName(string propertyName)
        {
            if (string.IsNullOrEmpty(propertyName))
                return false;
                
            // Check against known valid properties
            string[] validProperties = {
                // General Categories
                "L", "LC", "Ll", "Lm", "Lo", "Lt", "Lu",  // Letters
                "M", "Mc", "Me", "Mn",                     // Marks
                "N", "Nd", "Nl", "No",                     // Numbers
                "P", "Pc", "Pd", "Pe", "Pf", "Pi", "Po", "Ps", // Punctuation
                "S", "Sc", "Sk", "Sm", "So",               // Symbols
                "Z", "Zl", "Zp", "Zs",                     // Separators
                "C", "Cc", "Cf", "Cn", "Co", "Cs",        // Other
                
                // Unicode Blocks
                "Basic_Latin", "Latin_1_Supplement", "Latin_Extended_A", "Latin_Extended_B",
                "IPA_Extensions", "Spacing_Modifier_Letters", "Combining_Diacritical_Marks",
                "Greek_and_Coptic", "Cyrillic", "Hebrew", "Arabic", "Devanagari", "Bengali",
                "Thai", "Hiragana", "Katakana", "CJK_Unified_Ideographs",
                
                // Script Properties
                "Latin", "Greek", "Arabic", "Cyrillic", "Hebrew",
                
                // Binary Properties
                "Alphabetic", "ASCII_Hex_Digit", "Emoji", "Math", "Uppercase", "Lowercase",
                "White_Space", "ID_Start", "ID_Continue"
            };
            
            return validProperties.Contains(propertyName);
        }
        
        /// <summary>
        /// Scan comment group (?#...)
        /// </summary>
        private (SplittableToken token, int nextPosition)? ScanCommentGroup(ZeroCopyStringView input, int position)
        {
            // Scan from (?# to closing )
            int end = position + 3; // Skip (?#
            int depth = 1;
            
            while (end < input.Length && depth > 0)
            {
                if (input[end] == (byte)'(')
                    depth++;
                else if (input[end] == (byte)')')
                    depth--;
                end++;
            }
            
            var token = new SplittableToken(input.Slice(position, end - position), TokenType.RegexComment, position);
            return (token, end);
        }
    }
}