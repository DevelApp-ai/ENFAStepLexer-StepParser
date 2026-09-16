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
    /// Scope-aware symbol table implementation
    /// </summary>
    public class ScopeAwareSymbolTable : IScopeAwareSymbolTable
    {
        private readonly Dictionary<string, List<SymbolEntry>> _symbols = new();

        /// <summary>Declare a symbol in the specified scope.</summary>
        public void Declare(string name, string type, string scope, ICodeLocation location)
        {
            if (!_symbols.ContainsKey(scope))
                _symbols[scope] = new List<SymbolEntry>();

            var entry = new SymbolEntry
            {
                Name = name,
                Type = type,
                Scope = scope,
                Location = location
            };

            _symbols[scope].Add(entry);
        }

        /// <summary>Look up a symbol in the specified scope and parent scopes.</summary>
        public SymbolEntry? Lookup(string name, string scope)
        {
            // First check the current scope
            if (_symbols.ContainsKey(scope))
            {
                var symbol = _symbols[scope].LastOrDefault(s => s.Name == name);
                if (symbol != null)
                    return symbol;
            }

            // Then check parent scopes by walking up the scope hierarchy
            var parts = scope.Split('.');
            for (int i = parts.Length - 1; i >= 0; i--)
            {
                var parentScope = string.Join(".", parts.Take(i));
                if (!string.IsNullOrEmpty(parentScope) && _symbols.ContainsKey(parentScope))
                {
                    var symbol = _symbols[parentScope].LastOrDefault(s => s.Name == name);
                    if (symbol != null)
                        return symbol;
                }
            }

            return null;
        }

        /// <summary>Check if a symbol exists in the specified scope.</summary>
        public bool Exists(string name, string scope)
        {
            return Lookup(name, scope) != null;
        }

        /// <summary>Get all symbols in the specified scope.</summary>
        public IEnumerable<SymbolEntry> GetSymbols(string scope)
        {
            return _symbols.ContainsKey(scope) ? _symbols[scope] : Enumerable.Empty<SymbolEntry>();
        }

        /// <summary>Find all references to a symbol across all scopes.</summary>
        public IEnumerable<SymbolEntry> FindAllReferences(string name)
        {
            return _symbols.SelectMany(kvp => kvp.Value).Where(s => s.Name == name);
        }
    }
}