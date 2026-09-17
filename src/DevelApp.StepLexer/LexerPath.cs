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
        /// Cached rolling fingerprint over the token type sequence, used by
        /// path merging. <c>-1</c> marks a cold cache; the pair
        /// (<see cref="_fingerprint"/>, <see cref="_fingerprintCount"/>) is
        /// advanced incrementally for append-only token lists.
        /// </summary>
        private long _fingerprint;

        /// <summary>
        /// Number of tokens folded into <see cref="_fingerprint"/>, or
        /// <c>-1</c> when the cache is cold.
        /// </summary>
        private int _fingerprintCount = -1;

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
        /// Computes an order-sensitive rolling fingerprint over the token
        /// type sequence of this path. The result is cached and advanced
        /// incrementally while tokens are only appended, so calling this
        /// after each token is O(1) amortized instead of O(tokens so far).
        /// </summary>
        /// <returns>A fingerprint of the sequence of token types on this path.</returns>
        /// <remarks>
        /// Fingerprints are used to bucket paths for merging; callers must
        /// still verify true sequence equality when fingerprints collide.
        /// Non-append-only modifications of <see cref="Tokens"/> invalidate
        /// the cache only in the shrinking case, which is not produced by
        /// the lexer itself.
        /// </remarks>
        internal long GetTokenFingerprint()
        {
            var tokens = Tokens;
            if (_fingerprintCount < 0 || tokens.Count < _fingerprintCount)
            {
                _fingerprint = 0;
                _fingerprintCount = 0;
            }

            for (int i = _fingerprintCount; i < tokens.Count; i++)
            {
                var type = tokens[i].Type;
                _fingerprint = _fingerprint * 31 + (type?.GetHashCode() ?? 0);
            }

            _fingerprintCount = tokens.Count;
            return _fingerprint;
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