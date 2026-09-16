using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using DevelApp.StepLexer;
using CognitiveGraph;
using CognitiveGraph.Accessors;
using CognitiveGraph.Schema;

namespace DevelApp.StepParser
{

    /// <summary>
    /// Individual code change for surgical operations
    /// </summary>
    public class CodeChange
    {
        /// <summary>Gets or sets the location where the code change is applied.</summary>
        public ICodeLocation Location { get; set; } = new CodeLocation();
        
        /// <summary>Gets or sets the original text before the change.</summary>
        public string OriginalText { get; set; } = string.Empty;
        
        /// <summary>Gets or sets the new text after the change.</summary>
        public string NewText { get; set; } = string.Empty;
        
        /// <summary>Gets or sets the type of change (insert, delete, replace).</summary>
        public string ChangeType { get; set; } = string.Empty; // insert, delete, replace
    }
}