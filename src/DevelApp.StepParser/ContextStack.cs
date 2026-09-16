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
    /// Hierarchical context stack implementation
    /// </summary>
    public class ContextStack : IContextStack
    {
        private readonly Stack<(string context, string? name)> _stack = new();

        /// <summary>Push a new context onto the stack.</summary>
        public void Push(string context, string? name = null)
        {
            _stack.Push((context, name));
        }

        /// <summary>Pop the current context from the stack.</summary>
        public void Pop()
        {
            if (_stack.Count > 0)
                _stack.Pop();
        }

        /// <summary>Get the current context.</summary>
        public string Current()
        {
            return _stack.Count > 0 ? _stack.Peek().context : "";
        }

        /// <summary>Check if a context is in scope.</summary>
        public bool InScope(string context)
        {
            return _stack.Any(item => item.context == context);
        }

        /// <summary>Get the current depth of the stack.</summary>
        public int Depth()
        {
            return _stack.Count;
        }

        /// <summary>Get the full path from root to current context.</summary>
        public string[] GetPath()
        {
            var path = _stack.Reverse().Select(item => item.context).ToArray();
            return path;
        }

        /// <summary>Check if the stack contains the specified context.</summary>
        public bool Contains(string context)
        {
            return InScope(context);
        }
    }
}