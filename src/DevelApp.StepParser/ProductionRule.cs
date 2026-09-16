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
    /// Grammar production rule for parsing
    /// </summary>
    public class ProductionRule
    {
        /// <summary>Gets or sets the name of the production rule.</summary>
        public string Name { get; set; } = string.Empty;
        
        /// <summary>Gets or sets the right-hand side symbols of the production rule.</summary>
        public List<string> RightHandSide { get; set; } = new();
        
        /// <summary>Gets or sets the context in which this rule applies.</summary>
        public string Context { get; set; } = string.Empty;
        
        /// <summary>Gets or sets the precedence level for this rule.</summary>
        public int Precedence { get; set; } = 0;
        
        /// <summary>Gets or sets the associativity of this rule (none, left, right).</summary>
        public string Associativity { get; set; } = "none"; // none, left, right
        
        /// <summary>Gets or sets the semantic action to execute when this rule is applied.</summary>
        public Action<GraphNodeRef, List<GraphNodeRef>, CognitiveGraphBuilder>? SemanticAction { get; set; }
        
        /// <summary>Gets or sets the precondition that must be satisfied for this rule to apply.</summary>
        public Func<ParseContext, bool>? Precondition { get; set; }

        /// <summary>
        /// Initializes a new instance of the ProductionRule class.
        /// </summary>
        /// <param name="name">The name of the production rule</param>
        /// <param name="rightHandSide">The right-hand side symbols of the production rule</param>
        /// <param name="context">The context in which this rule applies (optional)</param>
        public ProductionRule(string name, List<string> rightHandSide, string context = "")
        {
            Name = name;
            RightHandSide = rightHandSide;
            Context = context;
        }

        /// <summary>
        /// Returns a string representation of the production rule.
        /// </summary>
        /// <returns>A string in the format "Name ::= RightHandSide"</returns>
        public override string ToString()
        {
            return $"{Name} ::= {string.Join(" ", RightHandSide)}";
        }
    }
}