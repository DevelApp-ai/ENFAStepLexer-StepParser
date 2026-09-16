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
    /// Node reference in the CognitiveGraph for parser paths
    /// </summary>
    public struct GraphNodeRef
    {
        /// <summary>Gets or sets the offset of the node in the graph.</summary>
        public uint NodeOffset { get; set; }
        
        /// <summary>Gets or sets the symbol identifier for this node.</summary>
        public ushort SymbolId { get; set; }
        
        /// <summary>Gets or sets the type of the node.</summary>
        public ushort NodeType { get; set; }
        
        /// <summary>Gets or sets the name of the production rule this node represents.</summary>
        public string RuleName { get; set; }
        
        /// <summary>Gets or sets the value associated with this node.</summary>
        public string Value { get; set; }
        
        /// <summary>Gets or sets the location in the source code for this node.</summary>
        public ICodeLocation Location { get; set; }

        /// <summary>
        /// Initializes a new instance of the GraphNodeRef struct.
        /// </summary>
        /// <param name="nodeOffset">The offset of the node in the graph</param>
        /// <param name="symbolId">The symbol identifier for this node</param>
        /// <param name="nodeType">The type of the node</param>
        /// <param name="ruleName">The name of the production rule this node represents</param>
        /// <param name="value">The value associated with this node</param>
        /// <param name="location">The location in the source code for this node</param>
        public GraphNodeRef(uint nodeOffset, ushort symbolId, ushort nodeType, string ruleName, string value, ICodeLocation location)
        {
            NodeOffset = nodeOffset;
            SymbolId = symbolId;
            NodeType = nodeType;
            RuleName = ruleName;
            Value = value;
            Location = location;
        }
    }
}