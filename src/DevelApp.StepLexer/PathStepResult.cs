using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace DevelApp.StepLexer
{

    /// <summary>
    /// Result of processing a single path
    /// </summary>
    public class PathStepResult
    {
        /// <summary>
        /// Gets or sets the list of tokens found on this path
        /// </summary>
        public List<StepToken> Tokens { get; set; } = new();
        
        /// <summary>
        /// Gets or sets the list of paths resulting from this processing step
        /// </summary>
        public List<LexerPath> Paths { get; set; } = new();
        
        /// <summary>
        /// Gets or sets the list of context changes that occurred on this path
        /// </summary>
        public List<string> ContextChanges { get; set; } = new();
    }
}