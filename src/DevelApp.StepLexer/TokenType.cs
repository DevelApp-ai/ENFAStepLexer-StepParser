using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace DevelApp.StepLexer
{

    /// <summary>
    /// Enumeration of token types used in both regex pattern parsing and source code tokenization
    /// </summary>
    public enum TokenType
    {
        // Regex pattern tokens
        /// <summary>
        /// Literal character token in regex patterns
        /// </summary>
        Literal,
        
        /// <summary>
        /// Escape sequence token (e.g., \n, \t, \\)
        /// </summary>
        EscapeSequence,
        
        /// <summary>
        /// Character class token (e.g., [a-z], [^0-9])
        /// </summary>
        CharacterClass,
        
        /// <summary>
        /// Group start token (opening parenthesis)
        /// </summary>
        GroupStart,
        
        /// <summary>
        /// Group end token (closing parenthesis)
        /// </summary>
        GroupEnd,
        
        /// <summary>
        /// Special group token (e.g., (?:), (?=), (?!))
        /// </summary>
        SpecialGroup,
        
        /// <summary>
        /// Quantifier token (e.g., *, +, ?, {n,m})
        /// </summary>
        Quantifier,
        
        /// <summary>
        /// Lazy quantifier token (e.g., *?, +?, ??)
        /// </summary>
        LazyQuantifier,
        
        /// <summary>
        /// Alternation token (pipe symbol |)
        /// </summary>
        Alternation,
        
        /// <summary>
        /// Start anchor token (caret ^)
        /// </summary>
        StartAnchor,
        
        /// <summary>
        /// End anchor token (dollar sign $)
        /// </summary>
        EndAnchor,
        
        /// <summary>
        /// Any character token (dot .)
        /// </summary>
        AnyChar,
        
        /// <summary>
        /// Hexadecimal escape token (e.g., \x41)
        /// </summary>
        HexEscape,
        
        /// <summary>
        /// Unicode escape token (e.g., \u0041, \U00000041)
        /// </summary>
        UnicodeEscape,
        
        /// <summary>
        /// Unicode property token (e.g., \p{L}, \P{N})
        /// </summary>
        UnicodeProperty,
        
        /// <summary>
        /// Inline modifier token (e.g., (?i), (?m), (?s))
        /// </summary>
        InlineModifier,
        
        /// <summary>
        /// Literal text token in \Q...\E construct
        /// </summary>
        LiteralText,
        
        /// <summary>
        /// Comment token in (?#...) construct
        /// </summary>
        RegexComment,
        
        // Source code tokens
        /// <summary>
        /// Identifier token in source code
        /// </summary>
        Identifier,
        
        /// <summary>
        /// Numeric literal token
        /// </summary>
        Number,
        
        /// <summary>
        /// String literal token
        /// </summary>
        String,
        
        /// <summary>
        /// Keyword token (language-specific reserved words)
        /// </summary>
        Keyword,
        
        /// <summary>
        /// Operator token (arithmetic, logical, assignment operators)
        /// </summary>
        Operator,
        
        /// <summary>
        /// Whitespace token (spaces, tabs, line breaks)
        /// </summary>
        Whitespace,
        
        /// <summary>
        /// Comment token (single-line and multi-line comments)
        /// </summary>
        Comment,
        
        /// <summary>
        /// Punctuation token (semicolons, commas, brackets)
        /// </summary>
        Punctuation
    }
}