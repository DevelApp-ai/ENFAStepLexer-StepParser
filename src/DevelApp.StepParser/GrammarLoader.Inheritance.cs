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
        private readonly Dictionary<string, string> _registeredBaseGrammars = new();

        /// <summary>
        /// Register a base grammar that other grammars can inherit from via
        /// <c>Inherits:</c>. The grammar content is parsed without
        /// inheritance processing, so registered base grammars form the roots
        /// of the inheritance hierarchy.
        /// </summary>
        /// <param name="name">The name used in <c>Inherits:</c> declarations.</param>
        /// <param name="content">The grammar file content.</param>
        /// <exception cref="ENFA_GrammarBuild_Exception">Thrown when the grammar content cannot be parsed.</exception>
        public void RegisterBaseGrammar(string name, string content)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new ENFA_GrammarBuild_Exception("Base grammar name must not be empty.");
            }

            // Parse without inheritance processing so the registry can contain
            // the roots of the hierarchy. Parsing eagerly also validates the
            // content at registration time rather than at first use.
            var grammar = ParseGrammarContent(content, $"{name}.grammar", processInheritance: false);
            grammar.Name = name;
            _registeredBaseGrammars[name] = content;
        }

        /// <summary>
        /// Process grammar inheritance: resolve each import (transitively),
        /// merge base grammar content into the derived grammar and detect
        /// inheritance cycles.
        /// </summary>
        /// <param name="grammar">The grammar whose imports should be processed.</param>
        /// <exception cref="ENFA_GrammarBuild_Exception">Thrown when an imported grammar is not inheritable or an inheritance cycle is detected.</exception>
        private void ProcessInheritance(GrammarDefinition grammar)
        {
            ProcessInheritanceCore(grammar, new HashSet<string>());
        }

        /// <summary>
        /// Recursive core of inheritance processing with cycle detection.
        /// </summary>
        /// <param name="grammar">The grammar currently being processed.</param>
        /// <param name="visiting">Names of grammars on the current inheritance path.</param>
        private void ProcessInheritanceCore(GrammarDefinition grammar, HashSet<string> visiting)
        {
            if (grammar.Imports.Count == 0)
            {
                return;
            }

            var key = InheritanceKey(grammar);
            if (!visiting.Add(key))
            {
                throw new ENFA_GrammarBuild_Exception(
                    $"{DiagnosticCodes.GrammarInheritanceCycle}: Grammar inheritance cycle detected involving grammar '{grammar.Name}'. Check the Inherits: declarations of all involved grammars.");
            }

            try
            {
                foreach (var import in grammar.Imports.ToList())
                {
                    var baseGrammar = LoadBaseGrammar(import);
                    if (baseGrammar != null)
                    {
                        // Process the base grammar's own imports first so that
                        // inheritance is transitive (grand-base content is
                        // merged into the base before the base is merged in).
                        ProcessInheritanceCore(baseGrammar, visiting);
                        MergeGrammars(grammar, baseGrammar);
                    }
                }

                // Imports are intentionally preserved on the grammar after
                // processing (merging is idempotent) so that the inheritance
                // declarations remain inspectable.
            }
            finally
            {
                visiting.Remove(key);
            }
        }

        /// <summary>
        /// Build a unique key for cycle detection: grammars with a name are
        /// tracked by name, anonymous grammars by object identity.
        /// </summary>
        /// <param name="grammar">The grammar to key.</param>
        /// <returns>A unique key for the inheritance path set.</returns>
        private static string InheritanceKey(GrammarDefinition grammar)
        {
            return string.IsNullOrEmpty(grammar.Name)
                ? $"obj:{System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(grammar)}"
                : $"name:{grammar.Name}";
        }

        /// <summary>
        /// Load base grammar for inheritance. Resolution order: grammars
        /// registered via <see cref="RegisterBaseGrammar"/>, then built-in
        /// default grammars, then an empty grammar.
        /// </summary>
        /// <param name="baseName">The name from the <c>Inherits:</c> declaration.</param>
        /// <returns>The base grammar definition, or an empty grammar when the name is unknown.</returns>
        /// <exception cref="ENFA_GrammarBuild_Exception">Thrown when the resolved grammar is marked <c>Inheritable: false</c>.</exception>
        private GrammarDefinition? LoadBaseGrammar(string baseName)
        {
            if (_registeredBaseGrammars.TryGetValue(baseName, out var content))
            {
                var registered = ParseGrammarContent(content, $"{baseName}.grammar", processInheritance: false);
                registered.Name = baseName;
                if (!registered.IsInheritable)
                {
                    throw new ENFA_GrammarBuild_Exception(
                        $"{DiagnosticCodes.GrammarNotInheritable}: Grammar '{baseName}' is marked Inheritable: false and cannot be used in an Inherits: declaration.");
                }

                return registered;
            }

            var builtIn = CreateDefaultBaseGrammar(baseName);
            if (builtIn != null)
            {
                builtIn.IsInheritable = true;
            }

            return builtIn;
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
        /// Merge base grammar into derived grammar. Base-only token rules and
        /// production rules are inherited; derived rules with the same name
        /// override the base rules.
        /// </summary>
        /// <param name="derived">The grammar inheriting the base content.</param>
        /// <param name="baseGrammar">The grammar being inherited from.</param>
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

            // Merge production rules (base-only rules are inherited first,
            // derived rules with the same name override the base rule)
            var mergedProductions = new Dictionary<string, ProductionRule>();

            foreach (var rule in baseGrammar.ProductionRules)
            {
                mergedProductions[rule.Name] = rule;
            }

            foreach (var rule in derived.ProductionRules)
            {
                mergedProductions[rule.Name] = rule; // Override base rules
            }

            derived.ProductionRules = mergedProductions.Values.ToList();

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

            // Merge semantic actions (derived actions win)
            foreach (var kvp in baseGrammar.SemanticActions)
            {
                if (!derived.SemanticActions.ContainsKey(kvp.Key))
                {
                    derived.SemanticActions[kvp.Key] = kvp.Value;
                }
            }

            // Merge contexts (union, derived order preserved)
            foreach (var context in baseGrammar.Contexts)
            {
                if (!derived.Contexts.Contains(context))
                {
                    derived.Contexts.Add(context);
                }
            }
        }
    }
}
