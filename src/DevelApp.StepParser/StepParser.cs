using System;
using System.Collections.Generic;
using System.Linq;
using DevelApp.StepLexer;
using CognitiveGraph;
using CognitiveGraph.Builder;
using CognitiveGraph.Accessors;
using CognitiveGraph.Schema;

namespace DevelApp.StepParser
{

    /// <summary>
    /// Step parser with GLR-style multi-path processing and CognitiveGraph integration
    /// </summary>
    public partial class StepParser : IDisposable
    {
        private readonly List<ProductionRule> _grammar = new();
        private readonly Dictionary<ProductionRule, int> _ruleIndices = new();
        private readonly List<ParserPath> _activePaths = new();
        private readonly ParseContext _context = new();
        private readonly CognitiveGraphBuilder _graphBuilder;
        private readonly SchemaVersion _schemaVersion;
        private int _nextPathId = 0;
        private ushort _nextSymbolId = 1;
        private string _sourceText = "";

        /// <summary>
        /// Initializes a new instance of StepParser with the specified schema version
        /// </summary>
        /// <param name="schemaVersion">The CognitiveGraph schema version to use (default: V1)</param>
        public StepParser(SchemaVersion schemaVersion = SchemaVersion.V1)
        {
            _schemaVersion = schemaVersion;
            _graphBuilder = new CognitiveGraphBuilder(new GraphBuilderOptions { Schema = schemaVersion });
        }

        /// <summary>
        /// Active parsing paths
        /// </summary>
        public IReadOnlyList<ParserPath> ActivePaths => _activePaths;

        /// <summary>
        /// Current parse context
        /// </summary>
        public ParseContext Context => _context;

        /// <summary>
        /// Add a production rule to the grammar
        /// </summary>
        public void AddRule(ProductionRule rule)
        {
            // Keep the first index for duplicate rule references, matching the
            // List.IndexOf semantics previously used for packed node rule ids.
            _ruleIndices.TryAdd(rule, _grammar.Count);
            _grammar.Add(rule);
        }

        /// <summary>
        /// Remove all production rules (and active paths), returning the
        /// parser to its pre-configured state so a new grammar can be loaded
        /// without duplicating rules.
        /// </summary>
        public void ClearRules()
        {
            _grammar.Clear();
            _ruleIndices.Clear();
            _activePaths.Clear();
        }

        /// <summary>
        /// Initialize parser with tokens
        /// </summary>
        public void Initialize(List<StepToken> tokens, string sourceText = "")
        {
            _context.Tokens = tokens;
            _context.CurrentTokenIndex = 0;
            _sourceText = sourceText;
            _activePaths.Clear();
            _activePaths.Add(new ParserPath(_nextPathId++));
        }

        /// <summary>
        /// Parse next token and return results
        /// </summary>
        public ParserStepResult Step()
        {
            var result = new ParserStepResult();

            if (_context.CurrentToken == null)
            {
                result.IsComplete = true;
                result.CognitiveGraphs = GenerateCompleteCognitiveGraphs();
                return result;
            }

            var newPaths = new List<ParserPath>();

            foreach (var path in _activePaths.Where(p => p.IsValid))
            {
                var pathResults = ProcessParserPath(path, _context.CurrentToken);
                newPaths.AddRange(pathResults.NewPaths);
                result.Reductions.AddRange(pathResults.Reductions);
                
                if (pathResults.ContextChanges.Any())
                {
                    result.ContextChanges.AddRange(pathResults.ContextChanges);
                }
            }

            // Update paths and merge identical ones
            _activePaths.Clear();
            _activePaths.AddRange(MergeParserPaths(newPaths));

            // Prune low-quality paths if too many exist
            if (_activePaths.Count > 10)
            {
                _activePaths.Sort((a, b) => b.Score.CompareTo(a.Score));
                _activePaths.RemoveRange(10, _activePaths.Count - 10);
            }

            _context.CurrentTokenIndex++;
            result.ActivePathCount = _activePaths.Count;
            result.CurrentPosition = _context.CurrentTokenIndex;

            return result;
        }

        /// <summary>
        /// Handle ambiguous parses by returning CognitiveGraph with packed nodes
        /// </summary>
        public List<CognitiveGraph.CognitiveGraph> HandleAmbiguity()
        {
            return GenerateCompleteCognitiveGraphs();
        }

        /// <summary>
        /// Dispose resources
        /// </summary>
        public void Dispose()
        {
            _graphBuilder?.Dispose();
        }
    }
}