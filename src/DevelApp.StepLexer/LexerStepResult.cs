using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace DevelApp.StepLexer
{

    /// <summary>
    /// Result of a single lexer step
    /// </summary>
    public class LexerStepResult
    {
        /// <summary>
        /// Gets or sets the list of new tokens created during this step
        /// </summary>
        public List<StepToken> NewTokens { get; set; } = new();
        
        /// <summary>
        /// Gets or sets the list of context changes that occurred during this step
        /// </summary>
        public List<string> ContextChanges { get; set; } = new();
        
        /// <summary>
        /// Gets or sets the number of active parsing paths
        /// </summary>
        public int ActivePathCount { get; set; }
        
        /// <summary>
        /// Gets or sets a value indicating whether parsing is complete
        /// </summary>
        public bool IsComplete { get; set; }
    }
}