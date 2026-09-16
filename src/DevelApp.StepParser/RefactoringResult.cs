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
    /// Result of a refactoring operation
    /// </summary>
    public class RefactoringResult
    {
        /// <summary>Gets or sets whether the refactoring operation was successful.</summary>
        public bool Success { get; set; }
        
        /// <summary>Gets or sets the message describing the result of the operation.</summary>
        public string Message { get; set; } = string.Empty;
        
        /// <summary>Gets or sets the list of code changes made during the refactoring.</summary>
        public List<CodeChange> Changes { get; set; } = new();
        
        /// <summary>Gets or sets the location of the modified node after refactoring.</summary>
        public ICodeLocation? ModifiedNodeLocation { get; set; }
    }
}