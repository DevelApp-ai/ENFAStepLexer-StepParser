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
        /// Parse production rules from grammar content
        /// </summary>
        private void ParseProductionRules(string[] lines, GrammarDefinition grammar)
        {
            for (int i = 0; i < lines.Length; i++)
            {
                var line = lines[i].Trim();
                if (IsProductionRule(line))
                {
                    var fullRule = CollectMultiLineRule(lines, ref i);
                    var productionRule = ParseProductionRule(fullRule);
                    if (productionRule != null)
                    {
                        grammar.ProductionRules.Add(productionRule);
                    }
                }
            }
        }

        /// <summary>
        /// Check if line defines a production rule
        /// </summary>
        private bool IsProductionRule(string line)
        {
            // Production rules start with < and contain ::= but are not token rules
            return line.StartsWith("<") && line.Contains("::=") && !IsTokenRule(line);
        }

        /// <summary>
        /// Collect multi-line rule definition
        /// </summary>
        private string CollectMultiLineRule(string[] lines, ref int startIndex)
        {
            var ruleText = lines[startIndex];
            
            // Continue collecting lines until we find a complete rule or hit another rule
            while (startIndex + 1 < lines.Length && 
                   !lines[startIndex + 1].Trim().StartsWith("<") &&
                   !ruleText.Contains("=>") ||
                   (ruleText.Count(c => c == '{') > ruleText.Count(c => c == '}')))
            {
                startIndex++;
                ruleText += " " + lines[startIndex].Trim();
            }

            return ruleText;
        }

        /// <summary>
        /// Parse individual production rule
        /// </summary>
        private ProductionRule? ParseProductionRule(string ruleText)
        {
            try
            {
                // Pattern: <rule_name (context)> ::= rhs | rhs => { action }
                var match = Regex.Match(ruleText, @"<([^>]+)>\s*::=\s*(.+?)(?:\s*=>\s*\{([^}]*)\})?$", RegexOptions.Singleline);
                if (!match.Success) return null;

                var nameWithContext = match.Groups[1].Value.Trim();
                var rhsText = match.Groups[2].Value.Trim();
                var action = match.Groups[3].Success ? match.Groups[3].Value.Trim() : "";

                var context = ExtractContext(nameWithContext);
                var cleanName = RemoveContext(nameWithContext);

                // Handle alternatives (|)
                var alternatives = rhsText.Split('|');
                var firstAlternative = alternatives[0].Trim();
                
                // Parse RHS symbols
                var rhs = ParseRightHandSide(firstAlternative);
                
                var rule = new ProductionRule(cleanName, rhs, context.context)
                {
                    Precedence = context.priority
                };

                if (!string.IsNullOrEmpty(action))
                {
                    rule.SemanticAction = CreateSemanticAction(action, cleanName);
                }

                return rule;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error parsing production rule: {ruleText}. Error: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Parse right-hand side of production rule
        /// </summary>
        private List<string> ParseRightHandSide(string rhs)
        {
            var symbols = new List<string>();
            var matches = Regex.Matches(rhs, @"<([^>]+)>|""([^""]+)""|'([^']+)'|([a-zA-Z_][a-zA-Z0-9_]*)");
            
            foreach (Match match in matches)
            {
                if (match.Groups[1].Success) // <non-terminal>
                {
                    symbols.Add(match.Groups[1].Value);
                }
                else if (match.Groups[2].Success) // "terminal"
                {
                    symbols.Add($"\"{match.Groups[2].Value}\"");
                }
                else if (match.Groups[3].Success) // 'terminal'
                {
                    symbols.Add($"'{match.Groups[3].Value}'");
                }
                else if (match.Groups[4].Success) // TERMINAL
                {
                    symbols.Add(match.Groups[4].Value);
                }
            }

            return symbols;
        }

        /// <summary>
        /// Extract context information from rule name
        /// </summary>
        private (string context, int priority) ExtractContext(string nameWithContext)
        {
            var match = Regex.Match(nameWithContext, @"([^(]+)(?:\(([^)]+)\))?");
            if (!match.Success) return ("", 0);

            var name = match.Groups[1].Value.Trim();
            var context = match.Groups[2].Success ? match.Groups[2].Value.Trim() : "";
            
            // Extract priority if specified
            var priorityMatch = Regex.Match(context, @"priority:(\d+)");
            var priority = priorityMatch.Success ? int.Parse(priorityMatch.Groups[1].Value) : 0;
            
            // Remove priority specification from context
            context = Regex.Replace(context, @",?\s*priority:\d+", "").Trim();

            return (context, priority);
        }

        /// <summary>
        /// Remove context modifiers from name
        /// </summary>
        private string RemoveContext(string nameWithContext)
        {
            var parenIndex = nameWithContext.IndexOf('(');
            return parenIndex > 0 ? nameWithContext.Substring(0, parenIndex).Trim() : nameWithContext.Trim();
        }
    }
}