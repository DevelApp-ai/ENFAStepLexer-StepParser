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
    /// Symbol table entry
    /// </summary>
    public class SymbolEntry
    {
        /// <summary>Gets or sets the name of the symbol.</summary>
        public string Name { get; set; } = string.Empty;
        
        /// <summary>Gets or sets the type of the symbol.</summary>
        public string Type { get; set; } = string.Empty;
        
        /// <summary>Gets or sets the scope where the symbol is declared.</summary>
        public string Scope { get; set; } = string.Empty;
        
        /// <summary>Gets or sets the location where the symbol is declared.</summary>
        public ICodeLocation Location { get; set; } = new CodeLocation();
        
        /// <summary>Gets or sets the value associated with the symbol.</summary>
        public string Value { get; set; } = string.Empty;
        
        /// <summary>Gets or sets whether this symbol can be inlined.</summary>
        public bool CanInline { get; set; } = false;

        /// <summary>
        /// Gets or sets the stable, project-scoped symbol identity for cross-file
        /// reference resolution (ENFAStepLexer-StepParser issue #95). Same-named
        /// symbols in different namespaces carry distinct refs; the same
        /// declaration seen from multiple files carries the same ref.
        /// </summary>
        public SymbolRef SymbolRef { get; set; }

        /// <summary>
        /// Gets or sets the exact identifier-level span of the declared name,
        /// distinct from the enclosing statement/declaration span. Consumers
        /// anchoring documentation must use this span, not the entry location.
        /// </summary>
        public ICodeLocation? IdentifierSpan { get; set; }
    }
}
