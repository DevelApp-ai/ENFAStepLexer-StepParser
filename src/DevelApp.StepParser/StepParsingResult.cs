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
    /// Complete step-parsing result with CognitiveGraph integration
    /// </summary>
    public class StepParsingResult
    {
        /// <summary>Gets or sets whether the parsing operation was successful.</summary>
        public bool Success { get; set; }
        
        /// <summary>Gets or sets the primary CognitiveGraph result from parsing.</summary>
        public CognitiveGraph.CognitiveGraph? CognitiveGraph { get; set; }
        
        /// <summary>Gets or sets the list of ambiguous parse results when multiple interpretations are possible.</summary>
        public List<CognitiveGraph.CognitiveGraph> AmbiguousParses { get; set; } = new();
        
        /// <summary>Gets or sets the tokens generated during parsing.</summary>
        public List<StepToken> Tokens { get; set; } = new();
        
        /// <summary>Gets or sets the list of errors encountered during parsing.</summary>
        public List<string> Errors { get; set; } = new();

        /// <summary>Gets or sets the structured diagnostics (errors, warnings, information) produced during parsing.</summary>
        public List<ParseDiagnostic> Diagnostics { get; set; } = new();
        
        /// <summary>Gets or sets the time taken to complete the parsing operation.</summary>
        public TimeSpan ParseTime { get; set; }
        
        /// <summary>Gets or sets the number of parse paths explored during ambiguity resolution.</summary>
        public int PathCount { get; set; }
        
        /// <summary>Gets or sets the parsing context with scope and semantic information.</summary>
        public ParseContext Context { get; set; } = new ParseContext();
    }
}