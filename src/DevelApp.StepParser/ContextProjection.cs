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
    /// Context-sensitive projection for semantic rules
    /// </summary>
    public class ContextProjection
    {
        /// <summary>Gets or sets the name of the production rule this projection applies to.</summary>
        public string RuleName { get; set; } = string.Empty;
        
        /// <summary>Gets or sets the context in which this projection is active.</summary>
        public string Context { get; set; } = string.Empty;
        
        /// <summary>Gets or sets the projection pattern that triggers this rule.</summary>
        public string ProjectionPattern { get; set; } = string.Empty;
        
        /// <summary>Gets or sets the code triggered when this projection matches.</summary>
        public string TriggeredCode { get; set; } = string.Empty;
        
        /// <summary>Gets or sets the parameters for the projection.</summary>
        public List<string> Parameters { get; set; } = new();
        
        /// <summary>Gets or sets the condition function that determines if the projection matches.</summary>
        public Func<ParseContext, bool>? MatchCondition { get; set; }
        
        /// <summary>Gets or sets the action to execute when the projection is triggered.</summary>
        public Action<ICodeLocation, ParseContext>? ExecuteAction { get; set; }

        /// <summary>
        /// Initializes a new instance of the ContextProjection class.
        /// </summary>
        /// <param name="ruleName">The name of the production rule</param>
        /// <param name="context">The context in which this projection is active</param>
        /// <param name="pattern">The projection pattern that triggers this rule</param>
        /// <param name="code">The code triggered when this projection matches</param>
        public ContextProjection(string ruleName, string context, string pattern, string code)
        {
            RuleName = ruleName;
            Context = context;
            ProjectionPattern = pattern;
            TriggeredCode = code;
        }
    }
}