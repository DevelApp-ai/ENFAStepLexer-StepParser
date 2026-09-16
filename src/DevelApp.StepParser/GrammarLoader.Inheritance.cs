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
        /// Process grammar inheritance
        /// </summary>
        private void ProcessInheritance(GrammarDefinition grammar)
        {
            foreach (var import in grammar.Imports)
            {
                var baseGrammar = LoadBaseGrammar(import);
                if (baseGrammar != null)
                {
                    MergeGrammars(grammar, baseGrammar);
                }
            }
        }

        /// <summary>
        /// Load base grammar for inheritance
        /// </summary>
        private GrammarDefinition? LoadBaseGrammar(string baseName)
        {
            // In a full implementation, this would resolve base grammar files
            // For now, return a simplified base grammar
            return CreateDefaultBaseGrammar(baseName);
        }

        /// <summary>
        /// Create default base grammars for common parser types
        /// </summary>
        private GrammarDefinition? CreateDefaultBaseGrammar(string baseName)
        {
            var baseGrammar = new GrammarDefinition { Name = baseName };

            switch (baseName.ToLower())
            {
                case "antlr4_base":
                    // Add common ANTLR v4 patterns
                    baseGrammar.TokenRules.Add(new TokenRule("WS", "/[ \\t\\r\\n]+/", "", 0) { IsSkippable = true });
                    baseGrammar.TokenRules.Add(new TokenRule("IDENTIFIER", "/[a-zA-Z][a-zA-Z0-9]*/"));
                    baseGrammar.TokenRules.Add(new TokenRule("NUMBER", "/[0-9]+/"));
                    break;

                case "bison_base":
                    // Add common Bison patterns
                    baseGrammar.Precedence["+"] = 1;
                    baseGrammar.Precedence["-"] = 1;
                    baseGrammar.Precedence["*"] = 2;
                    baseGrammar.Precedence["/"] = 2;
                    baseGrammar.Associativity["+"] = "left";
                    baseGrammar.Associativity["-"] = "left";
                    baseGrammar.Associativity["*"] = "left";
                    baseGrammar.Associativity["/"] = "left";
                    break;
            }

            return baseGrammar;
        }

        /// <summary>
        /// Merge base grammar into derived grammar
        /// </summary>
        private void MergeGrammars(GrammarDefinition derived, GrammarDefinition baseGrammar)
        {
            // Merge token rules (base rules first, then derived overrides)
            var mergedTokens = new Dictionary<string, TokenRule>();
            
            foreach (var rule in baseGrammar.TokenRules)
            {
                mergedTokens[rule.Name] = rule;
            }
            
            foreach (var rule in derived.TokenRules)
            {
                mergedTokens[rule.Name] = rule; // Override base rules
            }
            
            derived.TokenRules = mergedTokens.Values.ToList();

            // Merge precedence rules
            foreach (var kvp in baseGrammar.Precedence)
            {
                if (!derived.Precedence.ContainsKey(kvp.Key))
                {
                    derived.Precedence[kvp.Key] = kvp.Value;
                }
            }

            // Merge associativity rules
            foreach (var kvp in baseGrammar.Associativity)
            {
                if (!derived.Associativity.ContainsKey(kvp.Key))
                {
                    derived.Associativity[kvp.Key] = kvp.Value;
                }
            }
        }
    }
}