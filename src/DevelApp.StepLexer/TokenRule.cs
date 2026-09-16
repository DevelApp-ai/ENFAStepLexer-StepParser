using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace DevelApp.StepLexer
{

    /// <summary>
    /// Grammar rule for tokenization
    /// </summary>
    public class TokenRule
    {
        /// <summary>
        /// Gets or sets the name of the token rule
        /// </summary>
        public string Name { get; set; } = string.Empty;
        
        /// <summary>
        /// Gets or sets the pattern to match for this rule
        /// </summary>
        public string Pattern { get; set; } = string.Empty;
        
        /// <summary>
        /// Gets or sets the context in which this rule applies
        /// </summary>
        public string Context { get; set; } = string.Empty;
        
        /// <summary>
        /// Gets or sets the priority of this rule (higher values have higher priority)
        /// </summary>
        public int Priority { get; set; } = 0;
        
        /// <summary>
        /// Gets or sets a value indicating whether this rule is a fragment (used by other rules)
        /// </summary>
        public bool IsFragment { get; set; }
        
        /// <summary>
        /// Gets or sets a value indicating whether tokens matched by this rule should be skipped
        /// </summary>
        public bool IsSkippable { get; set; }
        
        /// <summary>
        /// Gets or sets the action to execute when this rule matches
        /// </summary>
        public Action<StepToken>? Action { get; set; }

        /// <summary>
        /// Initializes a new instance of the TokenRule class
        /// </summary>
        /// <param name="name">The name of the token rule</param>
        /// <param name="pattern">The pattern to match for this rule</param>
        /// <param name="context">The context in which this rule applies</param>
        /// <param name="priority">The priority of this rule</param>
        public TokenRule(string name, string pattern, string context = "", int priority = 0)
        {
            Name = name;
            Pattern = pattern;
            Context = context;
            Priority = priority;
        }
    }
}