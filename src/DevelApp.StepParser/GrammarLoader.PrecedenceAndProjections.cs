using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using DevelApp.StepLexer;
using CognitiveGraph.Accessors;

namespace DevelApp.StepParser
{
    public partial class GrammarLoader
    {

        /// <summary>
        /// Parse precedence and associativity rules
        /// </summary>
        private void ParsePrecedenceRules(string[] lines, GrammarDefinition grammar)
        {
            bool inPrecedenceBlock = false;
            
            foreach (var line in lines)
            {
                var trimmed = line.Trim();
                
                if (trimmed.StartsWith("Precedence:"))
                {
                    inPrecedenceBlock = true;
                    continue;
                }
                
                if (inPrecedenceBlock)
                {
                    if (trimmed.StartsWith("}") || string.IsNullOrEmpty(trimmed))
                    {
                        inPrecedenceBlock = false;
                        continue;
                    }
                    
                    ParsePrecedenceLevel(trimmed, grammar);
                }
            }
        }

        /// <summary>
        /// Parse individual precedence level
        /// </summary>
        private void ParsePrecedenceLevel(string line, GrammarDefinition grammar)
        {
            // Format: Level1: { operators: ["*", "/"], associativity: "left" }
            var match = Regex.Match(line, @"Level(\d+):\s*\{\s*operators:\s*\[([^\]]+)\],\s*associativity:\s*""([^""]+)""\s*\}");
            if (match.Success)
            {
                var level = int.Parse(match.Groups[1].Value);
                var operators = match.Groups[2].Value.Split(',')
                    .Select(op => op.Trim().Trim('"'))
                    .ToArray();
                var associativity = match.Groups[3].Value;

                foreach (var op in operators)
                {
                    grammar.Precedence[op] = level;
                    grammar.Associativity[op] = associativity;
                }
            }
        }

        /// <summary>
        /// Parse context-sensitive projections
        /// </summary>
        private void ParseContextProjections(string[] lines, GrammarDefinition grammar)
        {
            foreach (var line in lines)
            {
                var trimmed = line.Trim();
                if (IsContextProjection(trimmed))
                {
                    var projection = ParseContextProjection(trimmed);
                    if (projection != null)
                    {
                        _projections[projection.RuleName + ":" + projection.Context] = projection;
                    }
                }
            }
        }

        /// <summary>
        /// Check if line defines a context projection
        /// </summary>
        private bool IsContextProjection(string line)
        {
            // Context projections have specific patterns for triggered code
            return line.Contains("@context") || line.Contains("@projection");
        }

        /// <summary>
        /// Parse context projection definition
        /// </summary>
        private ContextProjection? ParseContextProjection(string line)
        {
            try
            {
                // Pattern: @context(context_name) @projection(pattern) rule_name => { code }
                var match = Regex.Match(line, @"@context\(([^)]+)\)\s*@projection\(([^)]+)\)\s*([a-zA-Z_][a-zA-Z0-9_]*)\s*=>\s*\{([^}]*)\}");
                if (!match.Success) return null;

                var context = match.Groups[1].Value.Trim();
                var pattern = match.Groups[2].Value.Trim();
                var ruleName = match.Groups[3].Value.Trim();
                var code = match.Groups[4].Value.Trim();

                var projection = new ContextProjection(ruleName, context, pattern, code);
                projection.ExecuteAction = CreateProjectionAction(code, ruleName, context);

                return projection;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error parsing context projection: {line}. Error: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Get context projection for rule and context
        /// </summary>
        public ContextProjection? GetProjection(string ruleName, string context)
        {
            return _projections.TryGetValue(ruleName + ":" + context, out var projection) ? projection : null;
        }
    }
}