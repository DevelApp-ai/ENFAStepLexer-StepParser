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
    /// Result of a single parser step
    /// </summary>
    public class ParserStepResult
    {
        /// <summary>Gets or sets the list of reductions performed during this step.</summary>
        public List<string> Reductions { get; set; } = new();
        
        /// <summary>Gets or sets the list of context changes that occurred during this step.</summary>
        public List<string> ContextChanges { get; set; } = new();
        
        /// <summary>Gets or sets the number of active parse paths after this step.</summary>
        public int ActivePathCount { get; set; }
        
        /// <summary>Gets or sets the current position in the token stream after this step.</summary>
        public int CurrentPosition { get; set; }
        
        /// <summary>Gets or sets whether the parsing is complete after this step.</summary>
        public bool IsComplete { get; set; }
        
        /// <summary>Gets or sets the list of CognitiveGraphs representing parse results.</summary>
        public List<CognitiveGraph.CognitiveGraph> CognitiveGraphs { get; set; } = new();
    }
}