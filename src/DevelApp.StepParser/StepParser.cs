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
        // Symbol-keyed rule indexes (issue #157 follow-up, NL-morphology
        // scale): the pending-reduction and shift planning loops previously
        // scanned the full grammar linearly for every active path at every
        // round of every token step. On word-list grammars with thousands of
        // rules that scan dominated parse time. Both loops only ever select
        // rules whose right-hand side mentions a specific symbol (the stack
        // top for reductions, the token type for shifts), so the candidate
        // sets are precomputed here in AddRule order to keep the iteration
        // order (and therefore parse determinism) identical to the linear
        // scan.
        private readonly Dictionary<string, List<ProductionRule>> _rulesByRhsLastSymbol = new(StringComparer.Ordinal);
        private readonly Dictionary<string, List<ProductionRule>> _rulesByRhsSymbol = new(StringComparer.Ordinal);
        // Second-level reduction index (issue #157 follow-up, NL-morphology
        // scale): the vast majority of reduction candidates failing the
        // (state, rule) applicability test fail on the second stack symbol,
        // so rules with a right-hand side of two or more symbols are also
        // indexed by their final symbol pair. Single-symbol right-hand sides
        // cannot fail deeper than the top symbol, so they live in a separate
        // literal-top index. Merged candidate lists per observed
        // (top, second) pair are built lazily in rule-index order, which is
        // the same order the previous per-symbol list (and before that the
        // full grammar scan) iterated them, so parse determinism is kept.
        private readonly Dictionary<string, List<ProductionRule>> _rulesByRhsSingleSymbol = new(StringComparer.Ordinal);
        private readonly Dictionary<(string Last, string Second), List<ProductionRule>> _rulesByRhsLastTwoSymbols = new();
        private readonly Dictionary<(string Last, string Second), List<ProductionRule>> _deepReductionCandidateCache = new();
        private static readonly List<ProductionRule> EmptyRuleList = new();
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
        /// Memo of the graph node produced by each (path state, rule)
        /// reduction derivation, so a re-derived reduction reuses the node
        /// written by its first derivation instead of writing a duplicate
        /// into the append-only graph builder. Keyed by
        /// <c>{token position}:{parser state}:{stack signature}:{rule index}</c>
        /// and reset with the node-write counter (issue #157, part 4).
        /// </summary>
        private readonly Dictionary<string, GraphNodeRef> _reductionNodeMemo = new();

        /// <summary>
        /// Memo of the terminal node each shifted token writes, keyed by
        /// token instance: identical writes from every path that shifts the
        /// same token collapse to one node (issue #157, part 4). Reset with
        /// the node-write counter.
        /// </summary>
        private readonly Dictionary<StepToken, GraphNodeRef> _shiftNodeMemo = new();

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
        /// Discards the shift and reduction node memos. The memos reference
        /// node offsets that are only meaningful while the derivation state
        /// of the current parse is consistent; a cache-resumed incremental
        /// parse (issue #163) restores paths and re-lexes tokens under new
        /// absolute positions, so memoized nodes from the earlier parse can
        /// carry spans inconsistent with the resumed parse's children
        /// (observed as symbol-node span inversions). The session calls
        /// this before every resumed parse; cold parses reset via
        /// <see cref="Initialize(List{StepToken}, string)"/> (issue #157,
        /// part 4).
        /// </summary>
        internal void ResetGraphNodeMemos()
        {
            _reductionNodeMemo.Clear();
            _shiftNodeMemo.Clear();
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
            IndexRuleForSymbolLookups(rule);

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
            _rulesByRhsLastSymbol.Clear();
            _rulesByRhsSymbol.Clear();
            _rulesByRhsSingleSymbol.Clear();
            _rulesByRhsLastTwoSymbols.Clear();
            _deepReductionCandidateCache.Clear();
            _activePaths.Clear();
            _ignorableTokenTypes.Clear();
            _rootRuleNames = null;
        }

        /// <summary>
        /// Adds a rule to the symbol-keyed candidate indexes used by the
        /// pending-reduction and shift planning loops (issue #157
        /// follow-up, NL-morphology scale): reductions can only apply to a
        /// rule whose right-hand side ends with the stack top symbol, and a
        /// token can only be shifted into a rule whose right-hand side
        /// mentions its type. Rules with an empty right-hand side cannot be
        /// matched by either loop (both require at least one RHS symbol),
        /// so they are not indexed; behavior is unchanged for them.
        /// </summary>
        /// <param name="rule">The rule to index.</param>
        private void IndexRuleForSymbolLookups(ProductionRule rule)
        {
            if (rule.RightHandSide.Count == 0)
            {
                return;
            }

            if (!_rulesByRhsLastSymbol.TryGetValue(rule.RightHandSide[^1], out var byLast))
            {
                byLast = new List<ProductionRule>();
                _rulesByRhsLastSymbol[rule.RightHandSide[^1]] = byLast;
            }
            byLast.Add(rule);

            // Second-level index: single-symbol right-hand sides match on
            // the literal top; multi-symbol rules are indexed by their
            // final symbol pair so deep stacks consult only the rules whose
            // second-last symbol also matches.
            if (rule.RightHandSide.Count == 1)
            {
                if (!_rulesByRhsSingleSymbol.TryGetValue(rule.RightHandSide[0], out var singles))
                {
                    singles = new List<ProductionRule>();
                    _rulesByRhsSingleSymbol[rule.RightHandSide[0]] = singles;
                }
                singles.Add(rule);
            }
            else
            {
                var pairKey = (rule.RightHandSide[^1], rule.RightHandSide[^2]);
                if (!_rulesByRhsLastTwoSymbols.TryGetValue(pairKey, out var byPair))
                {
                    byPair = new List<ProductionRule>();
                    _rulesByRhsLastTwoSymbols[pairKey] = byPair;
                }
                byPair.Add(rule);
            }

            // Each rule must appear at most once per symbol in the candidate
            // list (the linear scan it replaces evaluated each rule once,
            // regardless of how often its right-hand side repeats a symbol);
            // a per-rule distinct-symbol pass guarantees that even when the
            // same rule is added twice (duplicate AddRule calls keep their
            // second entry, matching the linear scan's behavior). The
            // run-length check below handles the common case without any
            // allocation; only interleaved repeats fall back to the set.
            if (rule.RightHandSide.Distinct().Count() == rule.RightHandSide.Count)
            {
                foreach (var symbol in rule.RightHandSide)
                {
                    if (!_rulesByRhsSymbol.TryGetValue(symbol, out var bySymbol))
                    {
                        bySymbol = new List<ProductionRule>();
                        _rulesByRhsSymbol[symbol] = bySymbol;
                        bySymbol.Add(rule);
                    }
                    else if (bySymbol[^1] != rule)
                    {
                        bySymbol.Add(rule);
                    }
                }
            }
            else
            {
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var symbol in rule.RightHandSide)
                {
                    if (!seen.Add(symbol))
                    {
                        continue;
                    }
                    if (!_rulesByRhsSymbol.TryGetValue(symbol, out var bySymbol))
                    {
                        bySymbol = new List<ProductionRule>();
                        _rulesByRhsSymbol[symbol] = bySymbol;
                        bySymbol.Add(rule);
                    }
                    else if (bySymbol[^1] != rule)
                    {
                        bySymbol.Add(rule);
                    }
                }
            }
        }

        /// <summary>
        /// Single-symbol right-hand-side rules whose symbol matches the
        /// stack top, in grammar order: for a stack of depth one, the only
        /// candidates a reduction can apply to (multi-symbol rules fail the
        /// stack depth check, so skipping them keeps behavior identical).
        /// </summary>
        /// <param name="topSymbol">The stack top symbol.</param>
        /// <returns>The rule candidates, or an empty list for unknown symbols.</returns>
        private List<ProductionRule> GetReductionCandidates(string topSymbol)
        {
            return _rulesByRhsSingleSymbol.TryGetValue(topSymbol, out var rules) ? rules : EmptyRuleList;
        }

        /// <summary>
        /// Rules whose right-hand side can reduce a stack whose top two
        /// symbols are the given pair, in grammar order: the merged union of
        /// single-symbol rules ending with the top symbol and multi-symbol
        /// rules ending with the pair, kept in rule-index order (matching
        /// the previous per-symbol list's order, and the full grammar scan
        /// before it) so parse determinism is preserved. The merged list is
        /// built lazily on the first observation of a pair and cached.
        /// </summary>
        /// <param name="topSymbol">The stack top symbol.</param>
        /// <param name="secondSymbol">The second symbol on the stack.</param>
        /// <returns>The rule candidates, or an empty list for unknown symbols.</returns>
        private List<ProductionRule> GetDeepReductionCandidates(string topSymbol, string secondSymbol)
        {
            var cacheKey = (topSymbol, secondSymbol);
            if (_deepReductionCandidateCache.TryGetValue(cacheKey, out var cached))
            {
                return cached;
            }

            var candidates = new List<ProductionRule>();
            _deepReductionCandidateCache[cacheKey] = candidates;
            if (_rulesByRhsSingleSymbol.TryGetValue(topSymbol, out var singles))
            {
                candidates.AddRange(singles);
            }

            if (_rulesByRhsLastTwoSymbols.TryGetValue(cacheKey, out var pairs))
            {
                candidates.AddRange(pairs);
            }

            // Restore grammar order: both groups are disjoint (single-symbol
            // vs multi-symbol right-hand sides), so a stable merge by rule
            // index reproduces exactly the iteration order of the previous
            // per-symbol candidate list.
            if (candidates.Count > 1)
            {
                candidates.Sort((a, b) => _ruleIndices[a].CompareTo(_ruleIndices[b]));
            }

            return candidates;
        }

        /// <summary>
        /// Rules whose right-hand side mentions the given token type, in
        /// grammar order: the only candidates a shift of that token type can
        /// continue.
        /// </summary>
        /// <param name="tokenType">The token type being shifted.</param>
        /// <returns>The rule candidates, or an empty list for unknown token types.</returns>
        private List<ProductionRule> GetShiftCandidates(string tokenType)
        {
            return _rulesByRhsSymbol.TryGetValue(tokenType, out var rules) ? rules : EmptyRuleList;
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
            _reductionNodeMemo.Clear();
            _shiftNodeMemo.Clear();
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
            _reductionNodeMemo.Clear();
            _shiftNodeMemo.Clear();
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