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
        private readonly Dictionary<string, SymbolEntry> _entriesByRef = new();
        private string _project = "project";

        /// <summary>
        /// Gets or sets the project scope used to build stable symbol
        /// identities (ENFAStepLexer-StepParser issue #95). Set this once per
        /// analysis session before declaring symbols.
        /// </summary>
        public string Project
        {
            get => _project;
            set => _project = string.IsNullOrEmpty(value) ? "project" : value;
        }

        /// <summary>Declare a symbol in the specified scope.</summary>
        public void Declare(string name, string type, string scope, ICodeLocation location)
        {
            Declare(name, type, scope, location, null, null);
        }

        /// <summary>
        /// Declare a symbol in the specified scope with a stable symbol
        /// reference and an optional identifier-level span (issue #95).
        /// </summary>
        /// <param name="name">The declared symbol name.</param>
        /// <param name="type">The symbol type.</param>
        /// <param name="scope">The scope path (dotted).</param>
        /// <param name="location">The declaration location (statement/declaration span).</param>
        /// <param name="identifierSpan">The exact span of the declared identifier, if known.</param>
        /// <param name="qualifiedName">The resolved qualified name; defaults to scope + name.</param>
        public void Declare(string name, string type, string scope, ICodeLocation location, ICodeLocation? identifierSpan, string? qualifiedName = null)
        {
            if (!_symbols.ContainsKey(scope))
                _symbols[scope] = new List<SymbolEntry>();
            var fq = string.IsNullOrEmpty(scope) || scope == "global"
                ? (qualifiedName ?? name)
                : (qualifiedName ?? $"{scope}.{name}");
            var entry = new SymbolEntry
            {
                Name = name,
                Type = type,
                Scope = scope,
                Location = location,
                SymbolRef = new SymbolRef(_project, fq, name),
                IdentifierSpan = identifierSpan
            };
            // Issue #158: a re-declaration of the same stable symbol
            // identity (an incremental re-parse re-stepping a region it
            // already declared during an earlier step of the session)
            // updates the existing entry in place instead of appending a
            // duplicate. Without this, long-lived sessions grow the
            // per-scope lists (and lookups, which scan them) forever.
            if (_entriesByRef.TryGetValue(entry.SymbolRef.Id, out var existing))
            {
                existing.Type = entry.Type;
                existing.Location = entry.Location;
                existing.IdentifierSpan = entry.IdentifierSpan;
                return;
            }

            _symbols[scope].Add(entry);
            _entriesByRef[entry.SymbolRef.Id] = entry;
        }

        /// <summary>
        /// Clear every declared symbol while keeping the configured
        /// <see cref="Project"/> scope (issue #158: long-lived engines and
        /// editor sessions re-parse documents; entries retained from earlier
        /// parses made the heap grow per parse, lookups slower, and — under
        /// memory pressure — parse outcomes diverge from fresh-engine runs).
        /// </summary>
        public void Clear()
        {
            _symbols.Clear();
            _entriesByRef.Clear();
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

        /// <summary>
        /// Look up an entry by its stable symbol reference, across all scopes
        /// and files in this table (issue #95).
        /// </summary>
        public SymbolEntry? LookupRef(SymbolRef symbolRef)
        {
            if (symbolRef.IsEmpty)
            {
                return null;
            }

            return _entriesByRef.TryGetValue(symbolRef.Id, out var entry) ? entry : null;
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
