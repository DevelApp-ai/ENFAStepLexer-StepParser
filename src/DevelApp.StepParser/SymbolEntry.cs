using System;
using System.Collections.Generic;
using System.Linq;
using DevelApp.StepLexer;
using CognitiveGraph;
using CognitiveGraph.Builder;
using CognitiveGraph.Accessors;
using CognitiveGraph.Schema;

namespace DevelApp.StepParser
{

    /// <summary>
    /// Symbol table entry
    /// </summary>
    public class SymbolEntry
    {
        /// <summary>Gets or sets the name of the symbol.</summary>
        public string Name { get; set; } = string.Empty;
        
        /// <summary>Gets or sets the type of the symbol.</summary>
        public string Type { get; set; } = string.Empty;
        
        /// <summary>Gets or sets the scope where the symbol is declared.</summary>
        public string Scope { get; set; } = string.Empty;
        
        /// <summary>Gets or sets the location where the symbol is declared.</summary>
        public ICodeLocation Location { get; set; } = new CodeLocation();
        
        /// <summary>Gets or sets the value associated with the symbol.</summary>
        public string Value { get; set; } = string.Empty;
        
        /// <summary>Gets or sets whether this symbol can be inlined.</summary>
        public bool CanInline { get; set; } = false;
    }
}