using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace DevelApp.StepLexer
{

    /// <summary>
    /// Token produced by the step lexer with position information
    /// </summary>
    public class StepToken
    {
        /// <summary>
        /// Gets or sets the type of the token
        /// </summary>
        public string Type { get; set; } = string.Empty;
        
        /// <summary>
        /// Gets or sets the value/content of the token
        /// </summary>
        public string Value { get; set; } = string.Empty;
        
        /// <summary>
        /// Gets or sets the location of the token in the source code
        /// </summary>
        public ICodeLocation Location { get; set; } = new CodeLocation();
        
        /// <summary>
        /// Gets or sets the context in which the token was found
        /// </summary>
        public string Context { get; set; } = string.Empty;
        
        /// <summary>
        /// Gets or sets a value indicating whether this token can be split into alternatives
        /// </summary>
        public bool IsSplittable { get; set; }
        
        /// <summary>
        /// Gets or sets the list of split tokens when ambiguity is resolved
        /// </summary>
        public List<StepToken>? SplitTokens { get; set; }

        /// <summary>
        /// Initializes a new instance of the StepToken class
        /// </summary>
        public StepToken() { }

        /// <summary>
        /// Initializes a new instance of the StepToken class with specified parameters
        /// </summary>
        /// <param name="type">The type of the token</param>
        /// <param name="value">The value/content of the token</param>
        /// <param name="location">The location of the token in the source code</param>
        /// <param name="context">The context in which the token was found</param>
        public StepToken(string type, string value, ICodeLocation location, string context = "")
        {
            Type = type;
            Value = value;
            Location = location;
            Context = context;
        }

        /// <summary>
        /// Returns a string representation of the token
        /// </summary>
        /// <returns>A string in the format "Type:'Value' @Location"</returns>
        public override string ToString()
        {
            return $"{Type}:'{Value}' @{Location}";
        }
    }
}