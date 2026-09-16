using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using DevelApp.StepLexer;
using CognitiveGraph.Accessors;

namespace DevelApp.StepParser
{

    /// <summary>
    /// Grammar definition loaded from file
    /// </summary>
    public class GrammarDefinition
    {
        /// <summary>Gets or sets the name of the grammar definition.</summary>
        public string Name { get; set; } = string.Empty;
        
        /// <summary>Gets or sets the token splitter strategy (default: "Space").</summary>
        public string TokenSplitter { get; set; } = "Space";
        
        /// <summary>Gets or sets the list of token rules for lexical analysis.</summary>
        public List<TokenRule> TokenRules { get; set; } = new();
        
        /// <summary>Gets or sets the list of production rules for parsing.</summary>
        public List<ProductionRule> ProductionRules { get; set; } = new();
        
        /// <summary>Gets or sets the precedence values for operators and productions.</summary>
        public Dictionary<string, int> Precedence { get; set; } = new();
        
        /// <summary>Gets or sets the associativity rules (left, right, none) for operators.</summary>
        public Dictionary<string, string> Associativity { get; set; } = new();
        
        /// <summary>Gets or sets the list of available parsing contexts.</summary>
        public List<string> Contexts { get; set; } = new();
        
        /// <summary>Gets or sets the semantic actions triggered during parsing.</summary>
        public Dictionary<string, Action<GraphNodeRef, List<GraphNodeRef>, CognitiveGraph.Builder.CognitiveGraphBuilder>> SemanticActions { get; set; } = new();
        
        /// <summary>Gets or sets the list of imported grammar files.</summary>
        public List<string> Imports { get; set; } = new();
        
        /// <summary>Gets or sets whether this grammar can be inherited by other grammars.</summary>
        public bool IsInheritable { get; set; }
        
        /// <summary>Gets or sets the format type of the grammar (ANTLR, Bison, etc.).</summary>
        public string FormatType { get; set; } = string.Empty;
    }
}