using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using DevelApp.StepLexer;
using CognitiveGraph;
using CognitiveGraph.Accessors;
using CognitiveGraph.Schema;

namespace DevelApp.StepParser
{

    /// <summary>
    /// Selection criteria for location-based targeting
    /// </summary>
    public class SelectionCriteria
    {
        /// <summary>Gets or sets the regex pattern for matching target locations.</summary>
        public string? Regex { get; set; }
        
        /// <summary>Gets or sets the range specification with start and end markers.</summary>
        public (string start, string end)? Range { get; set; }
        
        /// <summary>Gets or sets the structural selection with type and member inclusion options.</summary>
        public (string type, bool includeFields, bool includeMethods)? Structural { get; set; }
        
        /// <summary>Gets or sets the boundary specification for selection limits.</summary>
        public string? Boundaries { get; set; }
        
        /// <summary>Gets or sets the grammar-based selection criteria.</summary>
        public string? Grammar { get; set; }
    }
}