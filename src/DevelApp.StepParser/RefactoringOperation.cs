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
    /// Refactoring operation definition
    /// </summary>
    public class RefactoringOperation
    {
        /// <summary>Gets or sets the name of the refactoring operation.</summary>
        public string Name { get; set; } = string.Empty;
        
        /// <summary>Gets or sets the applicable contexts where this operation can be used.</summary>
        public string[] ApplicableContexts { get; set; } = Array.Empty<string>();
        
        /// <summary>Gets or sets the preconditions function that determines if the operation can be applied.</summary>
        public Func<ParseContext, bool>? Preconditions { get; set; }
        
        /// <summary>Gets or sets the execution function that performs the refactoring operation.</summary>
        public Func<ICodeLocation, ParseContext, RefactoringResult>? Execute { get; set; }
        
        /// <summary>Gets or sets the description of what the refactoring operation does.</summary>
        public string Description { get; set; } = string.Empty;
    }
}