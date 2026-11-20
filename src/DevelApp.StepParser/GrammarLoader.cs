using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using DevelApp.StepLexer;
using CognitiveGraph.Accessors;

namespace DevelApp.StepParser
{

    /// <summary>
    /// Loader for grammar files with support for context-sensitive projections
    /// and projection match triggered code for semantic rules
    /// </summary>
    public partial class GrammarLoader
    {
        private readonly Dictionary<string, GrammarDefinition> _loadedGrammars = new();
        private readonly Dictionary<string, ContextProjection> _projections = new();

        /// <summary>
        /// Load grammar from file
        /// </summary>
        public GrammarDefinition LoadGrammar(string filePath)
        {
            if (_loadedGrammars.TryGetValue(filePath, out var cached))
                return cached;

            var content = File.ReadAllText(filePath);
            var grammar = ParseGrammarContent(content, filePath);
            
            _loadedGrammars[filePath] = grammar;
            return grammar;
        }

        /// <summary>
        /// Parse grammar content from string
        /// </summary>
        /// <param name="content">The grammar file content.</param>
        /// <param name="fileName">Optional file name used for error reporting.</param>
        /// <param name="processInheritance">Whether to resolve <c>Inherits:</c> declarations; pass <see langword="false"/> when parsing a registered base grammar.</param>
        /// <param name="throwOnUndefinedReferences">
        /// When <see langword="null"/> (default), undefined nonterminal references are recorded on
        /// <see cref="GrammarDefinition.UndefinedNonterminalReferences"/> and additionally throw when
        /// <c>GRAMMAR_VALIDATION_MODE=strict</c> is set (issue #157). Pass <see langword="true"/> to
        /// always throw, <see langword="false"/> to never throw.
        /// </param>
        public GrammarDefinition ParseGrammarContent(string content, string fileName = "", bool processInheritance = true, bool? throwOnUndefinedReferences = null)
        {
            var grammar = new GrammarDefinition();
            var lines = content.Split('\n', StringSplitOptions.RemoveEmptyEntries);

            ParseGrammarHeader(lines, grammar);
            ParseTokenRules(lines, grammar);
            ParseProductionRules(lines, grammar);
            ParsePrecedenceRules(lines, grammar);
            ParseContextProjections(lines, grammar);
            if (processInheritance)
            {
                ProcessInheritance(grammar);
            }

            // Issue #157 (part 2): surface nonterminals referenced from production
            // right-hand sides but never defined, instead of loading silently.
            var undefined = FindUndefinedNonterminalReferences(lines, grammar);
            grammar.UndefinedNonterminalReferences.AddRange(undefined);
            var strict = throwOnUndefinedReferences ??
                         string.Equals(Environment.GetEnvironmentVariable("GRAMMAR_VALIDATION_MODE"),
                             "strict", StringComparison.OrdinalIgnoreCase);
            if (undefined.Count > 0 && strict)
            {
                throw new ENFA_GrammarBuild_Exception(
                    $"{DiagnosticCodes.GrammarUndefinedNonterminal}: Grammar '{grammar.Name}' ({fileName}) references undefined nonterminals: {string.Join(", ", undefined)}");
            }

            return grammar;
        }

        /// <summary>
        /// Parse grammar header information
        /// </summary>
        private void ParseGrammarHeader(string[] lines, GrammarDefinition grammar)
        {
            foreach (var line in lines)
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith("Grammar:"))
                {
                    grammar.Name = trimmed.Substring(8).Trim();
                }
                else if (trimmed.StartsWith("TokenSplitter:"))
                {
                    grammar.TokenSplitter = trimmed.Substring(14).Trim();
                }
                else if (trimmed.StartsWith("Inherits:"))
                {
                    var inherits = trimmed.Substring(9).Trim();
                    grammar.Imports.AddRange(inherits.Split(',').Select(s => s.Trim()));
                }
                else if (trimmed.StartsWith("Inheritable:"))
                {
                    grammar.IsInheritable = bool.Parse(trimmed.Substring(12).Trim());
                }
                else if (trimmed.StartsWith("FormatType:"))
                {
                    grammar.FormatType = trimmed.Substring(11).Trim();
                }
            }
        }
    }
}