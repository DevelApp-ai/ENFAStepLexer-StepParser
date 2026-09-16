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
    /// Interface for hierarchical context stack management
    /// </summary>
    public interface IContextStack
    {
        /// <summary>Push a new context onto the stack.</summary>
        void Push(string context, string? name = null);
        
        /// <summary>Pop the current context from the stack.</summary>
        void Pop();
        
        /// <summary>Get the current context.</summary>
        string Current();
        
        /// <summary>Check if a context is in scope.</summary>
        bool InScope(string context);
        
        /// <summary>Get the current depth of the stack.</summary>
        int Depth();
        
        /// <summary>Get the full path from root to current context.</summary>
        string[] GetPath();
        
        /// <summary>Check if the stack contains the specified context.</summary>
        bool Contains(string context);
    }
}