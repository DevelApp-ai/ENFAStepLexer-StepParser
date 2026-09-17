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
    /// Parser path for GLR-style multi-path parsing with CognitiveGraph integration
    /// </summary>
    public class ParserPath
    {
        /// <summary>Gets or sets the unique identifier for this parse path.</summary>
        public int PathId { get; set; }
        
        /// <summary>Gets or sets the parse stack containing graph node references.</summary>
        public Stack<GraphNodeRef> ParseStack { get; set; } = new();
        
        /// <summary>Gets or sets the current position in the token stream.</summary>
        public int TokenPosition { get; set; }
        
        /// <summary>Gets or sets the current parser state.</summary>
        public string CurrentState { get; set; } = string.Empty;
        
        /// <summary>Gets or sets whether this parse path is still valid.</summary>
        public bool IsValid { get; set; } = true;
        
        /// <summary>Gets or sets the list of production rules currently being processed.</summary>
        public List<ProductionRule> ActiveProductions { get; set; } = new();
        
        /// <summary>Gets or sets the state dictionary for parser context information.</summary>
        public Dictionary<string, object> State { get; set; } = new();
        
        /// <summary>Gets or sets the quality score for this parse path.</summary>
        public float Score { get; set; } = 1.0f; // Path quality score
        
        /// <summary>Gets or sets the list of node offsets for ambiguity handling.</summary>
        public List<uint> NodeOffsets { get; set; } = new(); // Track node offsets for ambiguity handling

        /// <summary>
        /// Initializes a new instance of the ParserPath class.
        /// </summary>
        /// <param name="pathId">The unique identifier for this parse path</param>
        public ParserPath(int pathId)
        {
            PathId = pathId;
        }

        /// <summary>
        /// Creates a deep copy of this parser path with a new path ID.
        /// </summary>
        /// <param name="newPathId">The unique identifier for the cloned path</param>
        /// <returns>A new ParserPath instance with copied state</returns>
        public ParserPath Clone(int newPathId)
        {
            // Stack<T> enumerates top to bottom, so ToArray yields the stack
            // newest-first. Push the elements back in reverse order to restore
            // the original stack layout, using a single correctly sized copy.
            var stackArray = ParseStack.ToArray();
            var clonedStack = new Stack<GraphNodeRef>(stackArray.Length);
            for (int i = stackArray.Length - 1; i >= 0; i--)
            {
                clonedStack.Push(stackArray[i]);
            }

            return new ParserPath(newPathId)
            {
                ParseStack = clonedStack,
                TokenPosition = TokenPosition,
                CurrentState = CurrentState,
                IsValid = IsValid,
                ActiveProductions = new List<ProductionRule>(ActiveProductions),
                State = new Dictionary<string, object>(State),
                Score = Score,
                NodeOffsets = new List<uint>(NodeOffsets)
            };
        }
    }
}