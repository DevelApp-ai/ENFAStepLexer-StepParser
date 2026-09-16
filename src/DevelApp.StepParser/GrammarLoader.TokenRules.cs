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
        /// Parse token rules from grammar content
        /// </summary>
        private void ParseTokenRules(string[] lines, GrammarDefinition grammar)
        {
            foreach (var line in lines)
            {
                var trimmed = line.Trim();
                if (IsTokenRule(trimmed))
                {
                    var tokenRule = ParseTokenRule(trimmed);
                    if (tokenRule != null)
                    {
                        grammar.TokenRules.Add(tokenRule);
                    }
                }
            }
        }

        /// <summary>
        /// Check if line defines a token rule
        /// </summary>
        private bool IsTokenRule(string line)
        {
            // Token rules start with < and contain > followed by ::= 
            // and have patterns like /regex/, 'literal', or "string" on the right side
            if (!line.StartsWith("<") || !line.Contains("::=")) return false;
            
            var colonIndex = line.IndexOf("::=");
            if (colonIndex == -1) return false;
            
            var rightSide = line.Substring(colonIndex + 3).Trim();
            
            // Token rules have patterns, not references to other rules
            return rightSide.StartsWith("/") || rightSide.StartsWith("'") || rightSide.StartsWith("\"");
        }

        /// <summary>
        /// Parse individual token rule
        /// </summary>
        private TokenRule? ParseTokenRule(string line)
        {
            try
            {
                // Pattern: <TOKEN_NAME> ::= pattern => { action }
                // Fixed regex to properly capture the full pattern
                var match = Regex.Match(line, @"<([^>]+)>\s*::=\s*(.+?)(?:\s*=>\s*\{([^}]*)\})?$");
                if (!match.Success) return null;

                var name = match.Groups[1].Value.Trim();
                var pattern = match.Groups[2].Value.Trim();
                var action = match.Groups[3].Success ? match.Groups[3].Value.Trim() : "";

                var context = ExtractContext(name);
                var cleanName = RemoveContext(name);

                var rule = new TokenRule(cleanName, pattern, context.context)
                {
                    Priority = context.priority,
                    IsSkippable = action.Contains("skip") || action.Contains("/* skip")
                };

                if (!string.IsNullOrEmpty(action))
                {
                    rule.Action = CreateTokenAction(action);
                }

                return rule;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error parsing token rule: {line}. Error: {ex.Message}");
                return null;
            }
        }
    }
}