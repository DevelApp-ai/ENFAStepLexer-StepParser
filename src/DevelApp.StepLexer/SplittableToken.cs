using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace DevelApp.StepLexer
{

    /// <summary>
    /// Represents a token that can be split into multiple alternatives during ambiguity resolution
    /// Used for both regex pattern parsing and source code tokenization
    /// </summary>
    public class SplittableToken
    {
        /// <summary>
        /// Gets the text content of the token as a zero-copy string view
        /// </summary>
        public ZeroCopyStringView Text { get; }
        
        /// <summary>
        /// Gets or sets the type of the token
        /// </summary>
        public TokenType Type { get; set; }
        
        /// <summary>
        /// Gets the position of the token in the input
        /// </summary>
        public int Position { get; }
        
        /// <summary>
        /// Gets or sets the list of alternative tokens when ambiguity is detected
        /// </summary>
        public List<SplittableToken>? Alternatives { get; set; }
        
        /// <summary>
        /// Initializes a new instance of the SplittableToken class
        /// </summary>
        /// <param name="text">The text content of the token</param>
        /// <param name="type">The type of the token</param>
        /// <param name="position">The position of the token in the input</param>
        public SplittableToken(ZeroCopyStringView text, TokenType type, int position)
        {
            Text = text;
            Type = type;
            Position = position;
        }
        
        /// <summary>
        /// Split this token into multiple alternatives when ambiguity is detected
        /// </summary>
        /// <param name="alternatives">Array of alternative token text and type pairs</param>
        public void Split(params (ZeroCopyStringView text, TokenType type)[] alternatives)
        {
            Alternatives ??= new List<SplittableToken>();
            
            foreach (var (text, type) in alternatives)
            {
                Alternatives.Add(new SplittableToken(text, type, Position));
            }
        }
        
        /// <summary>
        /// Gets a value indicating whether this token has alternatives
        /// </summary>
        public bool HasAlternatives => Alternatives != null && Alternatives.Count > 0;
    }
}