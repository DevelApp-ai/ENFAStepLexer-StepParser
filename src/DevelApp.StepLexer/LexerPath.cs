using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace DevelApp.StepLexer
{

    /// <summary>
    /// Lexer path for handling multiple tokenization possibilities
    /// </summary>
    public class LexerPath
    {
        /// <summary>
        /// Gets or sets the unique identifier for this path
        /// </summary>
        public int PathId { get; set; }
        
        /// <summary>
        /// Gets or sets the current position in the input stream
        /// </summary>
        public int Position { get; set; }
        
        /// <summary>
        /// Gets or sets the list of tokens found on this path
        /// </summary>
        public List<StepToken> Tokens { get; set; } = new();
        
        /// <summary>
        /// Gets or sets the current parsing context
        /// </summary>
        public string CurrentContext { get; set; } = string.Empty;
        
        /// <summary>
        /// Gets or sets a value indicating whether this path is still valid
        /// </summary>
        public bool IsValid { get; set; } = true;
        
        /// <summary>
        /// Gets or sets the state dictionary for context-specific data
        /// </summary>
        public Dictionary<string, object> State { get; set; } = new();

        /// <summary>
        /// Initializes a new instance of the LexerPath class
        /// </summary>
        /// <param name="pathId">The unique identifier for this path</param>
        /// <param name="position">The starting position in the input stream</param>
        public LexerPath(int pathId, int position = 0)
        {
            PathId = pathId;
            Position = position;
        }

        /// <summary>
        /// Creates a clone of this path with a new path ID
        /// </summary>
        /// <param name="newPathId">The path ID for the cloned path</param>
        /// <returns>A new LexerPath instance that is a copy of this path</returns>
        public LexerPath Clone(int newPathId)
        {
            return new LexerPath(newPathId, Position)
            {
                Tokens = new List<StepToken>(Tokens),
                CurrentContext = CurrentContext,
                IsValid = IsValid,
                State = new Dictionary<string, object>(State)
            };
        }
    }
}