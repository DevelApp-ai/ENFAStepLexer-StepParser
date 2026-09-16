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

            // Configure lexer with token rules
            foreach (var tokenRule in _currentGrammar.TokenRules)
            {
                _lexer.AddRule(tokenRule);
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