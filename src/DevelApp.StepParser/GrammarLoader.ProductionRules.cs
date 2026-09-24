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
                    var productionRules = ParseProductionRule(fullRule);
                    foreach (var productionRule in productionRules)
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

            // Continue collecting lines until we find a complete rule or hit
            // another rule. The bounds check on the array is part of the loop
            // condition itself so a rule with unbalanced braces at the end of
            // the file can never run past the last line, and braces are only
            // counted outside quoted literals/regexes: a quoted literal such
            // as "{" must not make an action look unterminated (issue #80).
            while (startIndex + 1 < lines.Length &&
                   ((!lines[startIndex + 1].Trim().StartsWith("<") &&
                     !ruleText.Contains("=>")) ||
                    CountUnquotedChar(ruleText, '{') > CountUnquotedChar(ruleText, '}')))
            {
                startIndex++;
                ruleText += " " + lines[startIndex].Trim();
            }

            return ruleText;
        }

        /// <summary>
        /// Count occurrences of a character outside single- or double-quoted
        /// regions (used for brace balancing, where quoted literals must not
        /// participate).
        /// </summary>
        private static int CountUnquotedChar(string text, char counted)
        {
            int count = 0;
            char quote = '\0';
            foreach (var c in text)
            {
                if (quote != '\0')
                {
                    if (c == quote)
                    {
                        quote = '\0';
                    }
                }
                else if (c == '\'' || c == '"')
                {
                    quote = c;
                }
                else if (c == counted)
                {
                    count++;
                }
            }

            return count;
        }

        /// <summary>
        /// Parse individual production rule, expanding top-level alternatives
        /// (separated by |) into one ProductionRule per alternative. All
        /// alternatives share the rule name, context, precedence, and semantic
        /// action of the original rule text.
        /// </summary>
        private List<ProductionRule> ParseProductionRule(string ruleText)
        {
            var rules = new List<ProductionRule>();
            try
            {
                // Pattern: <rule_name (context)> ::= rhs | rhs => { action }
                var match = Regex.Match(ruleText, @"<([^>]+)>\s*::=\s*(.+?)(?:\s*=>\s*\{([^}]*)\})?$", RegexOptions.Singleline);
                if (!match.Success) return rules;

                var nameWithContext = match.Groups[1].Value.Trim();
                var rhsText = match.Groups[2].Value.Trim();
                var action = match.Groups[3].Success ? match.Groups[3].Value.Trim() : "";

                var context = ExtractContext(nameWithContext);
                var cleanName = RemoveContext(nameWithContext);

                Action<GraphNodeRef, List<GraphNodeRef>, CognitiveGraph.Builder.CognitiveGraphBuilder>? semanticAction = null;
                if (!string.IsNullOrEmpty(action))
                {
                    semanticAction = CreateSemanticAction(action, cleanName);
                }

                // Handle alternatives (|): create one rule per alternative
                foreach (var alternative in SplitAlternatives(rhsText))
                {
                    var rhs = ParseRightHandSide(alternative);

                    // Skip empty alternatives (e.g. ε / epsilon productions).
                    // The GLR step loop has no lookahead gating, so a rule with
                    // an empty right-hand side would be applicable on every
                    // step and grow each path's stack instead of collapsing it.
                    if (rhs.Count == 0)
                    {
                        continue;
                    }

                    var rule = new ProductionRule(cleanName, rhs, context.context)
                    {
                        Precedence = context.priority,
                        SemanticAction = semanticAction
                    };

                    rules.Add(rule);
                }

                return rules;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error parsing production rule: {ruleText}. Error: {ex.Message}");
                return rules;
            }
        }

        /// <summary>
        /// Split a right-hand side on top-level alternative separators (|),
        /// ignoring separators inside quoted terminals ('...' or "...").
        /// </summary>
        private static List<string> SplitAlternatives(string rhsText)
        {
            var alternatives = new List<string>();
            var current = new System.Text.StringBuilder();
            char quote = '\0';

            foreach (var c in rhsText)
            {
                if (quote != '\0')
                {
                    current.Append(c);
                    if (c == quote)
                    {
                        quote = '\0';
                    }
                }
                else if (c == '"' || c == '\'')
                {
                    quote = c;
                    current.Append(c);
                }
                else if (c == '|')
                {
                    alternatives.Add(current.ToString().Trim());
                    current.Clear();
                }
                else
                {
                    current.Append(c);
                }
            }

            alternatives.Add(current.ToString().Trim());
            return alternatives;
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