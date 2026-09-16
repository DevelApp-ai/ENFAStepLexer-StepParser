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
    public partial class StepParserEngine : IDisposable
    {

        /// <summary>
        /// Select code locations based on criteria (RefakTS-style)
        /// </summary>
        public List<ICodeLocation> Select(string file, SelectionCriteria criteria)
        {
            var locations = new List<ICodeLocation>();

            if (!File.Exists(file))
                return locations;

            var content = File.ReadAllText(file);
            var parseResult = Parse(content, file);

            if (!parseResult.Success || parseResult.CognitiveGraph == null)
                return locations;

            return SelectFromCognitiveGraph(parseResult.CognitiveGraph, criteria, file);
        }

        /// <summary>
        /// Select locations from CognitiveGraph based on criteria
        /// </summary>
        private List<ICodeLocation> SelectFromCognitiveGraph(CognitiveGraph.CognitiveGraph graph, SelectionCriteria criteria, string file)
        {
            var locations = new List<ICodeLocation>();
            var rootNode = graph.GetRootNode();
            
            return SelectFromSymbolNode(rootNode, criteria, file, graph);
        }

        /// <summary>
        /// Select locations from SymbolNode recursively
        /// </summary>
        private List<ICodeLocation> SelectFromSymbolNode(SymbolNode node, SelectionCriteria criteria, string file, CognitiveGraph.CognitiveGraph graph)
        {
            var locations = new List<ICodeLocation>();
            var sourceText = node.GetSourceText().ToString();

            // Regex-based selection
            if (!string.IsNullOrEmpty(criteria.Regex))
            {
                var regex = new System.Text.RegularExpressions.Regex(criteria.Regex);
                if (regex.IsMatch(sourceText))
                {
                    var location = new CodeLocation
                    {
                        File = file,
                        StartLine = 1, // Would need proper line calculation from byte position
                        StartColumn = (int)node.SourceStart,
                        EndLine = 1,
                        EndColumn = (int)node.SourceEnd,
                        Context = ""
                    };
                    locations.Add(location);
                }
            }

            // Structural selection
            if (criteria.Structural.HasValue)
            {
                var structural = criteria.Structural.Value;
                if (node.TryGetProperty("RuleName", out var ruleNameProp))
                {
                    var ruleName = ruleNameProp.AsString();
                    if (IsStructuralMatch(ruleName, structural.type))
                    {
                        var location = new CodeLocation
                        {
                            File = file,
                            StartLine = 1, // Would need proper line calculation from byte position
                            StartColumn = (int)node.SourceStart,
                            EndLine = 1,
                            EndColumn = (int)node.SourceEnd,
                            Context = ""
                        };
                        locations.Add(location);
                    }
                }
            }

            // Process packed nodes (ambiguous interpretations) - simplified for now
            if (node.IsAmbiguous)
            {
                // TODO: Implement proper packed node iteration once CognitiveGraph collection API is clarified
                // var packedNodes = node.GetPackedNodes();
            }

            return locations;
        }

        /// <summary>
        /// Check if rule name matches structural criteria
        /// </summary>
        private bool IsStructuralMatch(string ruleName, string type)
        {
            return ruleName.Equals(type, StringComparison.OrdinalIgnoreCase) ||
                   ruleName.Contains(type, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Build line-offset map for converting line/column to byte offset
        /// </summary>
        private List<int> BuildLineOffsetMap(string text)
        {
            var lineOffsets = new List<int> { 0 }; // Line 1 starts at byte 0
            var bytes = Encoding.UTF8.GetBytes(text);
            
            for (int i = 0; i < bytes.Length; i++)
            {
                // Check for newline characters (LF or CR+LF)
                if (bytes[i] == '\n')
                {
                    lineOffsets.Add(i + 1); // Next line starts after the newline
                }
            }
            
            return lineOffsets;
        }

        /// <summary>
        /// Convert line/column location to byte offset
        /// </summary>
        private uint ConvertLocationToByteOffset(ICodeLocation location, string sourceText)
        {
            // Try to use cached line-offset map for the file
            List<int>? lineOffsets = null;
            if (!string.IsNullOrEmpty(location.File) && _lineOffsetMaps.ContainsKey(location.File))
            {
                lineOffsets = _lineOffsetMaps[location.File];
            }
            else
            {
                // Build line-offset map on demand
                lineOffsets = BuildLineOffsetMap(sourceText);
            }
            
            // Convert 1-based line number to 0-based index
            int lineIndex = location.StartLine - 1;
            if (lineIndex < 0 || lineIndex >= lineOffsets.Count)
            {
                return 0; // Invalid line number
            }
            
            // Get the byte offset for the start of the line
            uint lineStartOffset = (uint)lineOffsets[lineIndex];
            
            // Add the column offset (assuming column is also in bytes, not characters)
            // Note: StartColumn is 1-based, so subtract 1
            uint byteOffset = lineStartOffset + (uint)Math.Max(0, location.StartColumn - 1);
            
            return byteOffset;
        }

        /// <summary>
        /// Find SymbolNode at specific location
        /// </summary>
        private bool TryFindNodeAtLocation(ICodeLocation location, out SymbolNode node)
        {
            node = default;

            // Check if we have a parsed graph available
            if (_lastParsedGraph == null || string.IsNullOrEmpty(_lastSourceText))
            {
                return false;
            }

            try
            {
                // Convert line/column to byte offset
                uint byteOffset = ConvertLocationToByteOffset(location, _lastSourceText);
                
                // Use CognitiveGraph's spatial index to find node offsets at this location
                var nodeOffsets = _lastParsedGraph.FindNodesAt(byteOffset);
                
                if (nodeOffsets == null || !nodeOffsets.Any())
                {
                    return false;
                }
                
                // FindNodesAt returns node offsets, we need to get the actual nodes
                // For now, we'll use the root node as a placeholder since we don't have
                // a direct way to get a SymbolNode from an offset in the current API
                // This is a limitation that should be addressed in future versions
                
                // As a workaround, we can get the root node which at least validates
                // that a node exists at this location
                node = _lastParsedGraph.GetRootNode();
                return true;
            }
            catch (Exception)
            {
                // If there's any error in spatial lookup, return false
                return false;
            }
        }
    }
}