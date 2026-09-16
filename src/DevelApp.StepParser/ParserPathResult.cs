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
    /// Result of processing a single parser path
    /// </summary>
    public class ParserPathResult
    {
        /// <summary>Gets or sets the list of new parser paths created during processing.</summary>
        public List<ParserPath> NewPaths { get; set; } = new();
        
        /// <summary>Gets or sets the list of reductions performed on this path.</summary>
        public List<string> Reductions { get; set; } = new();
        
        /// <summary>Gets or sets the list of context changes that occurred on this path.</summary>
        public List<string> ContextChanges { get; set; } = new();
    }
}