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
    /// Parse context for semantic actions and rule evaluation
    /// </summary>
    public class ParseContext
    {
        /// <summary>Gets or sets the hierarchical context stack for scope management.</summary>
        public IContextStack ContextStack { get; set; } = new ContextStack();
        
        /// <summary>Gets or sets the scope-aware symbol table for identifier resolution.</summary>
        public IScopeAwareSymbolTable SymbolTable { get; set; } = new ScopeAwareSymbolTable();
        
        /// <summary>Gets or sets the current location in the source code being parsed.</summary>
        public ICodeLocation CurrentLocation { get; set; } = new CodeLocation();
        
        /// <summary>Gets or sets the list of tokens being parsed.</summary>
        public List<StepToken> Tokens { get; set; } = new();
        
        /// <summary>Gets or sets the current index in the token stream.</summary>
        public int CurrentTokenIndex { get; set; }
        
        /// <summary>Gets or sets the variables dictionary for context-specific data.</summary>
        public Dictionary<string, object> Variables { get; set; } = new();
        
        /// <summary>Gets or sets the list of currently active parsing contexts.</summary>
        public List<string> ActiveContexts { get; set; } = new();

        /// <summary>Gets the current token being processed, or null if at end of stream.</summary>
        public StepToken? CurrentToken => CurrentTokenIndex < Tokens.Count ? Tokens[CurrentTokenIndex] : null;
        
        /// <summary>
        /// Gets a look-ahead token at the specified offset from the current position.
        /// </summary>
        /// <param name="offset">The number of tokens to look ahead (default: 1)</param>
        /// <returns>The token at the look-ahead position, or null if beyond end of stream</returns>
        public StepToken? LookAhead(int offset = 1) => (CurrentTokenIndex + offset) < Tokens.Count ? Tokens[CurrentTokenIndex + offset] : null;
    }
}