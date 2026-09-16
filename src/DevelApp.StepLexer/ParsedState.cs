using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace DevelApp.StepLexer
{

    /// <summary>
    /// Represents a parsed state from Phase 2 processing
    /// </summary>
    public class ParsedState
    {
        /// <summary>
        /// Gets or sets the token type for this parsed state
        /// </summary>
        public TokenType TokenType { get; init; }
        
        /// <summary>
        /// Gets or sets the text content of the parsed state
        /// </summary>
        public string Text { get; init; } = string.Empty;
        
        /// <summary>
        /// Gets or sets the position of the parsed state in the input
        /// </summary>
        public int Position { get; init; }
        
        /// <summary>
        /// Gets or sets a value indicating whether this parsed state is ambiguous
        /// </summary>
        public bool IsAmbiguous { get; init; }
    }
}