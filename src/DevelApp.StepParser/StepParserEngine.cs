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
    /// Main step-parser engine coordinating lexer, parser, and semantic analysis
    /// Implements the GrammarForge architecture with location-based targeting and surgical operations
    /// </summary>
    public partial class StepParserEngine : IDisposable
    {
        private readonly DevelApp.StepLexer.StepLexer _lexer = new();
        private readonly StepParser _parser;
        private readonly GrammarLoader _grammarLoader = new();
        private readonly Dictionary<string, RefactoringOperation> _refactoringOps = new();
        private readonly Dictionary<string, ISemanticActionHandler> _actionHandlers = new();
        private readonly SchemaVersion _schemaVersion;
        private GrammarDefinition? _currentGrammar;
        private CognitiveGraph.CognitiveGraph? _lastParsedGraph;
        private string _lastSourceText = string.Empty;
        private Dictionary<string, List<int>> _lineOffsetMaps = new();

        /// <summary>
        /// Initializes a new instance of StepParserEngine with default action handlers
        /// </summary>
        /// <param name="schemaVersion">The CognitiveGraph schema version to use (default: V1 for backward compatibility)</param>
        public StepParserEngine(SchemaVersion schemaVersion = SchemaVersion.V1)
        {
            _schemaVersion = schemaVersion;
            _parser = new StepParser(schemaVersion);
            
            // Register default semantic action handlers
            RegisterActionHandler(new DefaultSemanticActionHandler());
            RegisterActionHandler(new RoslynSemanticActionHandler());
        }

        /// <summary>
        /// Current loaded grammar
        /// </summary>
        public GrammarDefinition? CurrentGrammar => _currentGrammar;

        /// <summary>
        /// Human-readable ML-assist status for diagnostics (issue #58 guard
        /// rails): which ML features are active and with which model versions.
        /// Everything is disabled by default and can be force-disabled at
        /// runtime via <see cref="MlAssistOptions"/> or the
        /// <c>DEVELAPP_STEPML_DISABLE_ALL</c> environment variable.
        /// </summary>
        public string MlAssistStatus => MlAssistOptions.Describe();

        /// <summary>
        /// Learned GLR path-pruning prototype (issue #75). Installed on the
        /// parser during <see cref="Parse"/> only while
        /// <see cref="MlAssistFeature.LearnedPathPruning"/> is enabled
        /// (default off — full GLR ships by default). Assign the trained
        /// artifact (e.g. <see cref="LearnedPathPruner.Default"/>) before
        /// enabling the feature.
        /// </summary>
        public LearnedPathPruner? PathPruner { get; set; }
        /// Learned token-rule prioritizer prototype (issue #74). Installed
        /// on the lexer during <see cref="Parse"/> only while
        /// <see cref="MlAssistFeature.LearnedRulePrioritization"/> is
        /// enabled, and it may only influence the ORDER in which token
        /// rules are evaluated — never which rules match. Assign the
        /// trained artifact (e.g. <see cref="LearnedRulePrioritizer.Default"/>)
        /// before enabling the feature.
        /// </summary>
        public ILearnedRulePrioritizer? RulePrioritizer { get; set; }


        /// <summary>
        /// Current parse context
        /// </summary>
        public ParseContext Context => _parser.Context;

        /// <summary>
        /// Register a custom semantic action handler
        /// </summary>
        /// <param name="handler">The handler to register</param>
        public void RegisterActionHandler(ISemanticActionHandler handler)
        {
            _actionHandlers[handler.Name] = handler;
        }

        /// <summary>
        /// Get a registered action handler by name, or return the default handler
        /// </summary>
        /// <param name="name">The handler name, or null for default</param>
        /// <returns>The action handler</returns>
        public ISemanticActionHandler GetActionHandler(string? name = null)
        {
            if (string.IsNullOrEmpty(name) || !_actionHandlers.ContainsKey(name))
                return _actionHandlers["default"];
            
            return _actionHandlers[name];
        }

        /// <summary>
        /// Load grammar from file
        /// </summary>
        public void LoadGrammar(string grammarFile)
        {
            _currentGrammar = _grammarLoader.LoadGrammar(grammarFile);
            ConfigureLexerAndParser();
            RegisterDefaultRefactoringOperations();
        }

        /// <summary>
        /// Load grammar from content string
        /// </summary>
        public void LoadGrammarFromContent(string grammarContent, string fileName = "inline")
        {
            _currentGrammar = _grammarLoader.ParseGrammarContent(grammarContent, fileName);
            ConfigureLexerAndParser();
            RegisterDefaultRefactoringOperations();
        }

        /// <summary>
        /// Configure lexer and parser with loaded grammar
        /// </summary>
        private void ConfigureLexerAndParser()
        {
            if (_currentGrammar == null) return;

            // Reset any previously configured rules so reconfiguring (e.g.
            // applying an overlay to an already-loaded grammar) never
            // duplicates them.
            _lexer.ClearRules();
            _parser.ClearRules();

            // Whitespace rules that no production references are ignorable
            // whitespace. The lexer still emits their tokens (tooling such
            // as RealTimeParserSession expects them), but the GLR parse must
            // skip them or they kill every parse path (issue #80). Grammars
            // that reference whitespace in a production right-hand side (for
            // example <opt-whitespace>) keep it as a significant token.
            var referencedSymbols = new HashSet<string>(
                _currentGrammar.ProductionRules.SelectMany(r => r.RightHandSide), StringComparer.Ordinal);
            _parser.IgnorableTokenTypes = new HashSet<string>(
                _currentGrammar.TokenRules
                    .Where(r => !referencedSymbols.Contains(r.Name)
                        && MatchesWhitespacePattern(r.Pattern))
                    .Select(static r => r.Name),
                StringComparer.Ordinal);

            // Configure lexer with token rules
            foreach (var tokenRule in _currentGrammar.TokenRules)
            {
                _lexer.AddRule(tokenRule);
            }

            MaterializeImplicitLiteralTokens(_currentGrammar);

            // Ensure whitespace can always be consumed: grammars with no rule
            // covering whitespace otherwise fail on the first space or
            // newline with LX1001 (issue #80). The rule is added with the
            // lowest priority and skips, so it never produces tokens. It is
            // only added when no existing rule (skippable or not) already
            // matches whitespace, so grammars that treat whitespace as a
            // significant token keep their own rules and do not fork the
            // lexer on every space.
            if (!_currentGrammar.TokenRules.Any(static r => MatchesWhitespacePattern(r.Pattern)))
            {
                _lexer.AddRule(new TokenRule("__default_ws", "/[ \\t\\r\\n]+/", priority: int.MinValue + 1)
                {
                    IsSkippable = true
                });
            }

            // Ensure '#' comment lines in example corpora can always be
            // consumed (issue #83): most corpus Examples.txt files start
            // with '#' comment lines and many grammars have no token rule
            // matching '#', so the lexer died with LX1001 at byte 0. This
            // lowest-priority skip rule (a '#' to end-of-line comment,
            // analogous to __default_ws) is added only when no existing
            // token rule can match a '#' anywhere in its pattern, so
            // grammars that treat '#' as a significant token (makefile
            // directives, markdown ATX headings, ...) keep their own rules.
            if (!_currentGrammar.TokenRules.Any(static r => r.Pattern.Contains('#')))
            {
                _lexer.AddRule(new TokenRule("__default_comment", "/#[^\\r\\n]*/", priority: int.MinValue)
                {
                    IsSkippable = true
                });
            }

            // Configure parser with production rules
            foreach (var productionRule in _currentGrammar.ProductionRules)
            {
                // Apply precedence and associativity
                if (_currentGrammar.Precedence.ContainsKey(productionRule.Name))
                {
                    productionRule.Precedence = _currentGrammar.Precedence[productionRule.Name];
                }
                
                _parser.AddRule(productionRule);
            }
        }

        /// <summary>
        /// Materialize implicit literal token rules for quoted terminals
        /// referenced in production right-hand sides (issue #80).
        /// Quoted terminals such as <c>'image'</c> in
        /// <c>&lt;image&gt; ::= 'image' ':' &lt;text&gt;</c> only exist as
        /// parser-side symbols; the lexer previously had no rule that could
        /// emit a matching token. This method synthesizes one token rule per
        /// distinct quoted terminal, named exactly like the parser symbol so
        /// the GLR token-type comparison matches.
        /// </summary>
        private void MaterializeImplicitLiteralTokens(GrammarDefinition grammar)
        {
            var knownTokenNames = new HashSet<string>(
                grammar.TokenRules.Select(r => r.Name), StringComparer.Ordinal);

            // Map from literal text to an existing token rule that matches
            // exactly that literal (e.g. <PLUS> ::= '+' for the literal '+').
            // Production symbols are rewritten to such a rule's name instead
            // of synthesizing a duplicate lexer rule, which would fork the
            // lexer on every occurrence of the literal (issue #80).
            var literalToRuleName = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var tokenRule in grammar.TokenRules)
            {
                var core = GetQuotedCore(tokenRule.Pattern);
                if (core != null && string.IsNullOrEmpty(tokenRule.Context))
                {
                    literalToRuleName.TryAdd(core, tokenRule.Name);
                }
            }

            foreach (var productionRule in grammar.ProductionRules)
            {
                for (int i = 0; i < productionRule.RightHandSide.Count; i++)
                {
                    var symbol = productionRule.RightHandSide[i];
                    if (!IsQuotedTerminalSymbol(symbol))
                    {
                        continue;
                    }

                    var symbolCore = GetQuotedCore(symbol);
                    if (symbolCore != null && literalToRuleName.TryGetValue(symbolCore, out var existingName))
                    {
                        // Reuse the grammar-defined token rule for this literal.
                        productionRule.RightHandSide[i] = existingName;
                        continue;
                    }

                    if (knownTokenNames.Add(symbol))
                    {
                        _lexer.AddRule(new TokenRule(symbol, symbol));
                    }
                }
            }

            // Materialize regex terminals: production symbols of the form
            // /.../ (written directly in CEBNF right-hand sides, issue #83)
            // are not token-rule names, so the parser could never reduce
            // them. Register each distinct regex as a lexer rule whose name
            // is the full regex text; dedup via knownTokenNames so a shared
            // regex yields one rule, not one per occurrence.
            foreach (var productionRule in grammar.ProductionRules)
            {
                for (int i = 0; i < productionRule.RightHandSide.Count; i++)
                {
                    var symbol = productionRule.RightHandSide[i];
                    if (symbol.Length < 3 || symbol[0] != '/' || symbol[^1] != '/')
                    {
                        continue;
                    }

                    if (knownTokenNames.Add(symbol))
                    {
                        _lexer.AddRule(new TokenRule(symbol, symbol));
                    }
                }
            }
        }

        /// <summary>
        /// Return the inner text of a complete quoted literal
        /// (<c>'lit'</c> / <c>"lit"</c>), or <see langword="null"/> when the
        /// value is not a complete quoted literal.
        /// </summary>
        private static string? GetQuotedCore(string value)
        {
            return value.Length > 2 && ((value[0] == '\'' && value[^1] == '\'')
                || (value[0] == '"' && value[^1] == '"'))
                ? value[1..^1]
                : null;
        }

        /// <summary>
        /// Check whether a production right-hand side symbol is a quoted
        /// terminal (<c>'lit'</c> or <c>"lit"</c>).
        /// </summary>
        private static bool IsQuotedTerminalSymbol(string symbol)
        {
            return symbol.Length >= 3
                && ((symbol[0] == '\'' && symbol[^1] == '\'')
                    || (symbol[0] == '"' && symbol[^1] == '"'));
        }

        /// <summary>
        /// Check whether a lexer pattern can match only whitespace: a
        /// whitespace regex (whose character classes, escapes and quantifiers
        /// cover only whitespace) or a quoted literal consisting solely of
        /// whitespace characters.
        /// </summary>
        private static bool MatchesWhitespacePattern(string pattern)
        {
            string core;
            if (pattern.Length > 2 && pattern[0] == '/' && pattern[^1] == '/')
            {
                core = pattern[1..^1];
            }
            else if (pattern.Length > 2 && ((pattern[0] == '"' && pattern[^1] == '"')
                || (pattern[0] == '\'' && pattern[^1] == '\'')))
            {
                core = pattern[1..^1];
            }
            else
            {
                core = pattern;
            }

            if (core.Length == 0)
            {
                return false;
            }

            // A negated character class can match non-whitespace.
            if (core.Contains("[^"))
            {
                return false;
            }

            // The pattern must contain at least one atom that can match
            // whitespace: a whitespace escape or a literal whitespace
            // character. Without this check, patterns made purely of regex
            // structure (for example the quoted literal '+' for <PLUS>)
            // reduce to the empty string below and would be misclassified
            // as whitespace, silently dropping their tokens (issue #80).
            bool sawWhitespaceAtom = core.Any(char.IsWhiteSpace)
                || core.Contains("\\t") || core.Contains("\\r") || core.Contains("\\n")
                || core.Contains("\\f") || core.Contains("\\v") || core.Contains("\\s");
            if (!sawWhitespaceAtom)
            {
                return false;
            }

            // Regex escape sequences that denote whitespace. Note that in
            // the pattern text these are two characters (backslash + letter),
            // so the literal text "\t" is not itself a whitespace character.
            var reduced = core
                .Replace("\\t", string.Empty)
                .Replace("\\r", string.Empty)
                .Replace("\\n", string.Empty)
                .Replace("\\f", string.Empty)
                .Replace("\\v", string.Empty)
                .Replace("\\s", string.Empty)
                .Replace("\\ ", string.Empty);

            // Regex structural characters: character classes, quantifiers,
            // groups, alternation, ranges and anchors. Anything else left in
            // the pattern must be whitespace for the pattern to match only
            // whitespace.
            foreach (var structural in new[] { '[', ']', '+', '*', '?', '(', ')', '{', '}', ',', '|', '^', '-', '\\' })
            {
                reduced = reduced.Replace(structural.ToString(), string.Empty);
            }

            return reduced.All(char.IsWhiteSpace);
        }

        /// <summary>
        /// Execute projection match triggered code for semantic rules
        /// </summary>
        public void ExecuteProjection(string ruleName, string context, ICodeLocation location)
        {
            var projection = _grammarLoader.GetProjection(ruleName, context);
            if (projection?.ExecuteAction != null)
            {
                // Would need to get SymbolNode from location - simplified for now
                // projection.ExecuteAction(node, _parser.Context);
            }
        }

        /// <summary>
        /// Switch grammar context (for multi-language support)
        /// </summary>
        public void SwitchGrammarContext(string newContext)
        {
            _parser.Context.ContextStack.Push(newContext);
        }

        /// <summary>
        /// Get memory usage statistics (zero-copy architecture benefit)
        /// </summary>
        public (long bytesAllocated, int activeObjects) GetMemoryStats()
        {
            // In a full implementation, this would track actual memory usage
            // For demonstration, return estimated values
            var tokenCount = _parser.Context.Tokens.Count;
            var pathCount = _parser.ActivePaths.Count;
            
            return (tokenCount * 64 + pathCount * 128, tokenCount + pathCount);
        }

        /// <summary>
        /// Dispose resources
        /// </summary>
        public void Dispose()
        {
            _parser?.Dispose();
            // _lexer does not implement IDisposable currently
        }
    }
}