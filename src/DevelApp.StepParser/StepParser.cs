using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
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
        private CognitiveGraphBuilder _graphBuilder;
        private readonly SchemaVersion _schemaVersion;
        private int _nextPathId = 0;
        private ushort _nextSymbolId = 1;

        /// <summary>
        /// Graph-builder node writes (symbol nodes + packed nodes) since the
        /// last full <see cref="Initialize(List{StepToken}, string)"/>. A cold start replaces the
        /// builder, so this resets to 0 there; every cache-resumed parse
        /// (see <see cref="RestorePaths"/>) keeps appending to the same
        /// builder and accumulates here. The consumer driving resumed
        /// parses (RealTimeParserSession) compares this counter against
        /// the current parse's own writes to decide when the retained
        /// content is stale-dominated and a cold start must reclaim it
        /// (issue #163: the append-only builder grew O(edits x document)
        /// and the doubling in the builder's EnsureCapacity OOM-killed
        /// long-lived sessions).
        /// </summary>
        private long _graphNodeWrites;

        /// <summary>
        /// The UTF-8 source bytes backing the current parse (issue #87):
        /// the single source of record handed to the graph builder
        /// zero-copy. The string Initialize overload encodes once at the
        /// boundary; no UTF-16 form is retained.
        /// </summary>
        private ReadOnlyMemory<byte> _sourceUtf8;

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
        /// Graph-builder node writes (symbol nodes plus packed nodes)
        /// since the last full <see cref="Initialize(List{StepToken}, string)"/>. A cold-start
        /// parse resets this to 0 (the builder is replaced); each
        /// cache-resumed parse appends to the same builder, so the
        /// counter measures how much stale graph content the builder
        /// accumulates across a resumed-parse history (issue #163).
        /// </summary>
        internal long GraphNodeWriteCount => _graphNodeWrites;

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
        /// Deterministic active-path budget applied after each token step (and
        /// in the end-of-input finalize phase). Default 10 (the historical
        /// full-GLR cap). When the budget prunes, the single highest-scoring
        /// path is always kept (survivor-preserving), so a genuine parse is not
        /// evicted by cheap resting prefixes (issue #157, part 3: the hard cap
        /// pruned the surviving path on e.g. Norwegian_Nynorsk). Settable for
        /// experiments (budget growth); the learned pruner's safety valve keeps
        /// using the default of 10 regardless.
        /// </summary>
        public int PathBudget { get; set; } = 10;

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
        /// Initialize parser with tokens and the source text. The text is
        /// encoded to UTF-8 once here (issue #87 boundary conversion) so
        /// the graph builder receives bytes without any further
        /// conversion.
        /// </summary>
        public void Initialize(List<StepToken> tokens, string sourceText = "")
        {
            // Issue #158: a full re-initialization is a new top-level parse.
            // Drop carry-over state (declared symbols, context stack,
            // semantic-action variables) accumulated by earlier parses of
            // this long-lived parser instance first.
            _context.ResetParseState();

            // Issue #158: the graph builder is append-only, so a long-lived
            // parser re-initialized per parse grows the heap linearly
            // (measured 2026-11-19: 30-45 MB retained and doubling per-parse
            // wall time over 8 sequenced 1000-line parses). The previous
            // parse's node offsets are garbage the moment a new full token
            // stream arrives: dispose and recreate the builder. Completed
            // graphs are self-contained Build buffers, so results already
            // returned to consumers stay valid.
            _graphBuilder.Dispose();
            _graphBuilder = new CognitiveGraphBuilder(new GraphBuilderOptions { Schema = _schemaVersion });
            _nextSymbolId = 1;
            _graphNodeWrites = 0;
            _context.Tokens = tokens;
            _context.CurrentTokenIndex = 0;
            _sourceUtf8 = Encoding.UTF8.GetBytes(sourceText);
            _activePaths.Clear();
            _activePaths.Add(new ParserPath(_nextPathId++));
        }

        /// <summary>
        /// Initialize parser with tokens and the UTF-8 source bytes
        /// (issue #87): the bytes are the single source of record and are
        /// handed to the graph builder zero-copy; no UTF-16 form is
        /// materialized.
        /// </summary>
        /// <param name="tokens">The tokens produced by the lexer.</param>
        /// <param name="sourceUtf8">The UTF-8 encoded source bytes.</param>
        public void Initialize(List<StepToken> tokens, ReadOnlyMemory<byte> sourceUtf8)
        {
            // Issue #158: see the string overload — fresh top-level parse.
            _context.ResetParseState();

            // Issue #158: see the string overload — fresh top-level parse
            // gets a fresh graph builder and fresh symbol ids.
            _graphBuilder.Dispose();
            _graphBuilder = new CognitiveGraphBuilder(new GraphBuilderOptions { Schema = _schemaVersion });
            _nextSymbolId = 1;
            _graphNodeWrites = 0;
            _context.Tokens = tokens;
            _context.CurrentTokenIndex = 0;
            _sourceUtf8 = sourceUtf8;
            _activePaths.Clear();
            _activePaths.Add(new ParserPath(_nextPathId++));
        }

        /// <summary>
        /// Capture a snapshot of the active parse paths for the path cache
        /// (issue #101, Selective Path Invalidation). The returned clones
        /// share the immutable stack spines of the live paths, so
        /// capturing is O(paths), not O(stack depth), and later stepping
        /// never mutates the returned state.
        /// </summary>
        /// <returns>
        /// Clones of the currently active paths, or an empty list when no
        /// path is active.
        /// </returns>
        public IReadOnlyList<ParserPath> CaptureActivePaths()
        {
            var snapshot = new List<ParserPath>(_activePaths.Count);
            foreach (var path in _activePaths)
            {
                snapshot.Add(path.Clone(path.PathId));
            }

            return snapshot;
        }

        /// <summary>
        /// Restore a previously captured path snapshot as the live parser
        /// state and continue stepping at the given token boundary
        /// (issue #101, Selective Path Invalidation).
        /// </summary>
        /// <param name="paths">
        /// Clones to install as the active paths; they are cloned once
        /// more so the source snapshot stays pristine.
        /// </param>
        /// <param name="tokens">
        /// The (new) token stream the parse continues over; tokens
        /// <c>[0, boundary)</c> must be the unchanged prefix the snapshot
        /// consumed.
        /// </param>
        /// <param name="sourceUtf8">
        /// The UTF-8 bytes of the new source text (issue #87: the bytes
        /// are the single source of record, handed on zero-copy).
        /// </param>
        /// <param name="boundary">
        /// Token boundary to resume from: token indexes below it are
        /// considered consumed by the restored paths.
        /// </param>
        public void RestorePaths(
            IReadOnlyList<ParserPath> paths,
            List<StepToken> tokens,
            ReadOnlyMemory<byte> sourceUtf8,
            int boundary)
        {
            if (paths == null)
            {
                throw new ArgumentNullException(nameof(paths));
            }

            if (tokens == null)
            {
                throw new ArgumentNullException(nameof(tokens));
            }

            if (boundary < 0 || boundary > tokens.Count)
            {
                throw new ArgumentOutOfRangeException(nameof(boundary));
            }

            _context.Tokens = tokens;
            _context.CurrentTokenIndex = boundary;
            _sourceUtf8 = sourceUtf8;

            _activePaths.Clear();
            foreach (var path in paths)
            {
                _activePaths.Add(path.Clone(path.PathId));
            }

            // The token index drives stepping; the per-path TokenPosition
            // values carried by the restored clones already line up with
            // the unchanged prefix [0, boundary).
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

                // Issue #93: label capture for the #75 training corpus —
                // mark the path ids that ultimately succeeded, including
                // every ancestor id the surviving paths were cloned from
                // (ParserPath.SurvivalIds). The capture (if attached by the
                // ML trace harness) turns every recorded (path, step)
                // observation into a labeled sample.
                PathLabelCapture.Current?.MarkSuccessful(
                    _activePaths
                        .Where(p => p.IsValid && p.StackDepth == 1)
                        .SelectMany(p => p.SurvivalIds));

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

            // Issue #93: label capture for the #75 path-pruner training
            // corpus. Observation-only and inert unless the ML trace
            // harness attached a capture on this thread: while the live
            // path count is above the deterministic budget (the regime the
            // learned pruner is consulted in), every active path's feature
            // vector is recorded for the current step, keyed by path id.
            // Success is attributed at end of input via the surviving
            // paths' ancestry chains (ParserPath.SurvivalIds): with
            // per-branch-minted ids alone no recorded id ever survived to
            // end of input, which left every label doomed and the success
            // class unobservable. Recording never changes paths, ordering
            // or scores.
            var labelCapture = PathLabelCapture.Current;
            if (labelCapture != null)
            {
                if (_activePaths.Count > 10)
                {
                    var (captureBestScore, captureBestPosition) =
                        LearnedPathPruner.ReferencePoints(_activePaths);
                    if (captureBestScore != float.MinValue)
                    {
                        foreach (var capturePath in _activePaths)
                        {
                            labelCapture.Record(
                                capturePath.PathId,
                                LearnedPathPruner.ExtractFeatures(
                                    capturePath, captureBestScore, captureBestPosition,
                                    _context.Tokens.Count));
                        }
                    }
                }

                labelCapture.NextStep();
            }

            // Prune low-quality paths if too many exist (survivor-preserving:
            // the highest-scoring path is never evicted, issue #157 part 3).
            PruneToPathBudget(_activePaths);

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
        /// The flattened token sequence of the winning derivation: the
        /// tokens the primary successful parse path shifted, in source
        /// order (issue #112). Unlike the per-path token union exposed by
        /// the lexer (see upstream issue #109), this sequence contains each
        /// source span at most once — sibling interpretations of ambiguous
        /// lexes and tokens skipped as ignorable are excluded, because no
        /// derivation consumed them.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The primary path is the first successful path (valid, stack
        /// collapsed to a single entry), matching the interpretation the
        /// ambiguous CognitiveGraph packs first and
        /// <see cref="StepParserEngine"/> selects as its best parse. When
        /// several interpretations complete,
        /// <see cref="StepParsingResult.AmbiguousParses"/> remains the
        /// accessor for the alternatives.
        /// </para>
        /// <para>
        /// When the input cannot be derived, or when the parse never
        /// completed, the sequence is empty. Spans matched by skippable
        /// token rules produce no tokens at all (they are never emitted),
        /// so the sequence tiles the input exactly whenever the grammar
        /// emits a token for every consumed span.
        /// </para>
        /// </remarks>
        /// <returns>The winning derivation's tokens, in shift (source) order.</returns>
        public IReadOnlyList<StepToken> GetResolvedTokens()
        {
            var successfulPaths = _activePaths
                .Where(p => p.IsValid && p.StackDepth == 1)
                .ToList();

            if (successfulPaths.Count == 0)
            {
                return Array.Empty<StepToken>();
            }

            return successfulPaths[0].GetConsumedTokens();
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