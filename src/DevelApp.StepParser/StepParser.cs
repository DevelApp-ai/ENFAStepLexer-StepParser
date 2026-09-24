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
        /// Learned GLR path-pruning prototype (issue #75). When set AND the
        /// <see cref="MlAssistFeature.LearnedPathPruning"/> gate is enabled,
        /// high-confidence doomed paths are pruned before the deterministic
        /// path budget prune. Default off: the full-GLR behavior is what
        /// ships unless a model artifact is assigned and the feature enabled
        /// via <see cref="MlAssistOptions"/>.
        /// </summary>
        public LearnedPathPruner? PathPruner { get; set; }

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
            _rootRuleNames = null; // invalidate lazy root-rule cache

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
            _ignorableTokenTypes.Clear();
            _rootRuleNames = null;
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
        /// Token types (e.g. unreferenced whitespace rules) the parser should
        /// silently skip. The lexer still emits these tokens so tooling
        /// consumers (RealTimeParserSession, diagnostics) keep seeing the
        /// full token stream; only the GLR parse skips them (issue #80).
        /// </summary>
        public HashSet<string> IgnorableTokenTypes
        {
            get => _ignorableTokenTypes;
            set => _ignorableTokenTypes = value ?? new HashSet<string>(StringComparer.Ordinal);
        }

        private HashSet<string> _ignorableTokenTypes = new(StringComparer.Ordinal);

        /// <summary>
        /// Lazy cache of root rule names: production rules whose left-hand
        /// side is not referenced by any other rule's right-hand side. Root
        /// rules must only reduce at end of input; reducing them mid-parse
        /// lets the root symbol masquerade as a shiftable operand and spawns
        /// nonsense stacks (issue #80).
        /// </summary>
        private HashSet<string>? _rootRuleNames;

        private HashSet<string> GetRootRuleNames()
        {
            if (_rootRuleNames == null)
            {
                var referenced = new HashSet<string>(
                    _grammar.SelectMany(r => r.RightHandSide), StringComparer.Ordinal);
                _rootRuleNames = new HashSet<string>(
                    _grammar.Where(r => !referenced.Contains(r.Name)).Select(r => r.Name),
                    StringComparer.Ordinal);
            }
            return _rootRuleNames;
        }

        /// <summary>
        /// Parse next token and return results
        /// </summary>
        public ParserStepResult Step()
        {
            var result = new ParserStepResult();

            // Skip tokens whose type is ignorable for the parse (e.g.
            // whitespace rules no production references). They stay in the
            // token stream for tooling, but must not kill parse paths.
            while (_context.CurrentToken != null
                && _ignorableTokenTypes.Contains(_context.CurrentToken.Type))
            {
                _context.CurrentTokenIndex++;
            }

            if (_context.CurrentToken == null)
            {
                // End of input: finish pending reductions so a parse that
                // consumed the whole token stream can still collapse to a
                // single stack entry (issue #80).
                FinalizeEndOfInputReductions();
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

            // Issue #75: learned pruning of high-confidence doomed paths
            // above the path budget, consulted only while the ML-assist gate
            // is enabled. The pruner has its own confidence threshold and
            // never acts at or below the deterministic budget (full-GLR
            // fallback otherwise).
            var pruner = PathPruner;
            if (pruner != null && MlAssistOptions.IsEnabled(MlAssistFeature.LearnedPathPruning))
            {
                pruner.PruneDoomedPaths(_activePaths, _context.Tokens.Count);
            }

            // Prune low-quality paths if too many exist
            if (_activePaths.Count > 10)
            {
                _activePaths.Sort(ComparePathsForPruning);
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