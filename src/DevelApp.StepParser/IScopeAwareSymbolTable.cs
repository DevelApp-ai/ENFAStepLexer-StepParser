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
    /// Interface for scope-aware symbol table management
    /// </summary>
    public interface IScopeAwareSymbolTable
    {
        /// <summary>Declare a symbol in the specified scope.</summary>
        void Declare(string name, string type, string scope, ICodeLocation location);
        
        /// <summary>Look up a symbol in the specified scope and parent scopes.</summary>
        SymbolEntry? Lookup(string name, string scope);
        
        /// <summary>Check if a symbol exists in the specified scope.</summary>
        bool Exists(string name, string scope);
        
        /// <summary>Get all symbols in the specified scope.</summary>
        IEnumerable<SymbolEntry> GetSymbols(string scope);
        
        /// <summary>Find all references to a symbol across all scopes.</summary>
        IEnumerable<SymbolEntry> FindAllReferences(string name);
    }
}