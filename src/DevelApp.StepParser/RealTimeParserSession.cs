using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using DevelApp.StepLexer;

namespace DevelApp.StepParser
{
    /// <summary>
    /// An incremental, real-time parsing session for IDE and editor
    /// integration. Wraps a <see cref="StepParserEngine"/> and maintains a
    /// token list that is updated incrementally when edits are applied,
    /// reusing unchanged tail tokens where possible so that consumers can
    /// rely on token object identity for unchanged regions.
    /// </summary>
    /// <remarks>
    /// All byte offsets are offsets into the UTF-8 encoding of the source
    /// text, matching the offsets reported by
    /// <see cref="StepToken.StartPosition"/>.
    /// </remarks>
    public class RealTimeParserSession
    {
        private readonly StepParserEngine _engine;
        private readonly string _fileName;
        private readonly DevelApp.StepLexer.StepLexer _lexer = new();
        private string _source = string.Empty;
        private List<StepToken> _tokens = new();
        private bool _grammarLoaded;
        private LexingMode _lexingMode = LexingMode.Exploratory;
        private int _lastUnmatchedOffset = -1;
        private bool _lastLexConsumedAllInput = true;
        private readonly ParsePathCache _parseCache = new();
        private StepParsingResult? _lastParse;

        /// <summary>
        /// Safety window for the windowed re-lex (issue #144): this many
        /// consecutive freshly lexed tokens must each match an old tail
        /// token exactly - same type, same value bytes, same context and
        /// position aligned at the edit's byte delta - before the
        /// byte-identical suffix is spliced from the old tail. The window
        /// keeps the splice robust against accidental short matches right
        /// after the edit: eight consecutive aligned tokens at shifted
        /// positions reproduce the byte input and the effective lexer
        /// state of the original lex, so the continuation is determined
        /// and need not be lexed again.
        /// </summary>
        private const int RealignmentWindowTokens = 8;

        /// <summary>
        /// Retained-graph factor (issue #163): when the parse-node writes
        /// accumulated in the engine graph builder since the last cold
        /// start exceed this factor times the current parse's own node
        /// writes, the retained builder content is stale-dominated:
        /// everything older than the current parse's nodes and the
        /// freshly stored path-cache snapshots is garbage from earlier
        /// parses of earlier text that nothing will ever read again, but
        /// the append-only builder keeps it. A cache-resumed parse with a
        /// decimated resume boundary (e.g. block pastes near the document
        /// start re-stepping almost the whole suffix) appends
        /// O(document) nodes per edit, so a long editing session grew
        /// the builder O(edits x document) until the builder's capacity
        /// doubling OOM-killed the process. When the factor trips, the
        /// session drops the path cache so the next edit cold-starts the
        /// parse (a cold start replaces the builder and resets the
        /// counter), bounding a session's retained builder content to a
        /// small multiple of a single parse's graph.
        /// </summary>
        private const int RetainedGraphNodeFactor = 8;

        /// <summary>
        /// Initializes a new session backed by a grammar given as content.
        /// </summary>
        /// <param name="grammarContent">The grammar file content.</param>
        /// <param name="fileName">Optional file name used for diagnostics.</param>
        public RealTimeParserSession(string grammarContent, string fileName = "inline")
            : this(CreateEngine(grammarContent, fileName), fileName)
        {
        }

        /// <summary>
        /// Initializes a new session backed by an existing engine. The
        /// engine's grammar must be loaded before <see cref="Initialize"/>.
        /// </summary>
        /// <param name="engine">The engine providing grammar and full parses.</param>
        /// <param name="fileName">Optional file name used for diagnostics.</param>
        public RealTimeParserSession(StepParserEngine engine, string fileName = "inline")
        {
            _engine = engine ?? throw new ArgumentNullException(nameof(engine));
            _fileName = fileName;
        }

        private static StepParserEngine CreateEngine(string grammarContent, string fileName)
        {
            if (grammarContent is null)
            {
                throw new ArgumentNullException(nameof(grammarContent));
            }

            var engine = new StepParserEngine();
            engine.LoadGrammarFromContent(grammarContent, fileName);
            return engine;
        }

        /// <summary>
        /// Gets the current source text after all applied edits.
        /// </summary>
        public string Text => _source;

        /// <summary>
        /// Gets the current token list. Tokens are ordered by position.
        /// </summary>
        public IReadOnlyList<StepToken> Tokens => _tokens;

        /// <summary>
        /// Gets the current version of the token list; incremented on every
        /// successful <see cref="ApplyEdit"/> and reset by
        /// <see cref="Initialize"/>.
        /// </summary>
        public int Version { get; private set; }

        /// <summary>
        /// Gets or sets the lexing mode of the session's internal lexer
        /// (issue #142). The default is <see cref="LexingMode.Exploratory"/>,
        /// which forks a lexer path per matching rule and unions the tokens of
        /// all active paths; with overlapping rules (e.g. a keyword rule and
        /// an identifier rule both matching <c>public</c>) the token stream
        /// grows multiplicatively. Set to
        /// <see cref="LexingMode.GreedyLongestMatch"/> for deterministic,
        /// linear lexing: at each position only the longest match is kept
        /// (ties broken by rule priority, then rule order), so exactly one
        /// token is emitted per position. Can be changed at any time; it
        /// applies to <see cref="Initialize"/> and every
        /// <see cref="ApplyEdit"/> re-lex.
        /// </summary>
        public LexingMode LexingMode
        {
            get => _lexingMode;
            set
            {
                _lexingMode = value;
                _lexer.Mode = value;
            }
        }

        /// <summary>
        /// Number of tail token objects that survived the last
        /// <see cref="ApplyEdit"/> and were reused (kept by reference) in
        /// the merged token list. Instrumentation for the issue #76
        /// evaluation of learned token-reuse prediction (candidate approach
        /// 4): the baseline tail-reuse strategy already avoids re-lexing
        /// aligned suffixes, and this counter measures how much it
        /// actually achieves per edit.
        /// </summary>
        public int LastReusedTokenCount { get; private set; }

        /// <summary>
        /// Fraction of the old token tail that was reused by the last
        /// <see cref="ApplyEdit"/> (1.0 when there was no tail, i.e. edits
        /// at the end of text have nothing to reuse and are vacuously
        /// fully reused). 0 means every old tail token was re-lexed.
        /// </summary>
        public double LastReuseRatio { get; private set; } = 1.0;

        /// <summary>
        /// Number of tokens the last lex pass actually lexed (issue #144
        /// instrumentation). After <see cref="Initialize"/> this equals
        /// <see cref="Tokens"/>.Count; after an <see cref="ApplyEdit"/>
        /// with a successful realignment it is small - roughly the edit
        /// vicinity plus the safety window - because the byte-identical
        /// suffix is spliced from the previously lexed tail. Compare with
        /// <see cref="Tokens"/>.Count to see how much lexing an edit
        /// avoided.
        /// </summary>
        public int LastRelexedTokenCount { get; private set; }

        /// <summary>
        /// Gets or sets whether the session also maintains a parse-level
        /// view of the token stream using Selective Path Invalidation
        /// (issue #101). When enabled, <see cref="Initialize"/> performs a
        /// full parse that records parser path snapshots at token
        /// boundaries, and every <see cref="ApplyEdit"/> resumes the parse
        /// from the nearest snapshot below the edit instead of re-parsing
        /// from the start. When disabled (the default), the session keeps
        /// its token-level-only behavior.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The incremental re-parse resumes at the restored boundary and
        /// steps forward to the end of input: the byte positions baked into
        /// previously written parse-graph nodes are immutable, so the
        /// unchanged suffix must be re-stepped to produce nodes with the
        /// edit-correct positions. The win measured against a full re-parse
        /// is the skipped prefix work (see
        /// <see cref="LastParseStepCount"/>) plus the preserved path-state
        /// reuse, not a sub-linear total.
        /// </para>
        /// </remarks>
        public bool EnableIncrementalParsing { get; set; }

        /// <summary>
        /// Gets the result of the last parse pass when
        /// <see cref="EnableIncrementalParsing"/> is enabled, or null when
        /// no parse has run yet or incremental parsing is disabled.
        /// </summary>
        public StepParsingResult? LastParse => _lastParse;

        /// <summary>
        /// Gets the number of <see cref="StepParser"/> steps the last
        /// parse pass performed (issue #101 instrumentation). Compare
        /// with <see cref="Tokens"/>' Count for the full-pass bound: after
        /// a cache-resumed edit this stays proportional to the distance
        /// from the edit to the end of input instead of the whole
        /// document.
        /// </summary>
        public int LastParseStepCount { get; private set; }

        /// <summary>
        /// Gets the token boundary the last parse pass resumed from:
        /// 0 for a full (cold-start) parse and larger than 0 when the
        /// path cache supplied a resume point below the edit (a cache
        /// hit, see <see cref="LastParseUsedCache"/>).
        /// </summary>
        public int LastParseResumeBoundary { get; private set; }

        /// <summary>
        /// Gets whether the last parse pass resumed from a cached path
        /// snapshot instead of parsing from the start (issue #101
        /// instrumentation: the parse-level "cache hit" signal).
        /// </summary>
        public bool LastParseUsedCache { get; private set; }

        /// <summary>
        /// Gets the number of token boundaries for which the path cache
        /// currently holds parser state snapshots (issue #101
        /// instrumentation). Empty while incremental parsing is disabled
        /// or before the first parse.
        /// </summary>
        public int ParseCacheBoundaryCount => EnableIncrementalParsing ? _parseCache.Count : 0;

        /// <summary>
        /// Occurs after the token list has been updated by
        /// <see cref="ApplyEdit"/> or <see cref="Initialize"/>. Read
        /// <see cref="Version"/> and <see cref="Tokens"/> in the handler.
        /// </summary>
        public event EventHandler? Reparsed;

        /// <summary>
        /// Byte offset into <see cref="Text"/> of the first position where no
        /// token rule matched during the last lex pass, or -1 when the whole
        /// input was consumed. Issue #108: lexing used to stop silently at the
        /// first unmatched position, so <see cref="Tokens"/> covered only a
        /// prefix of the input with no error signal; this property exposes the
        /// stall point (the engine-level equivalent is the LX1001 diagnostic).
        /// </summary>
        public int LastUnmatchedOffset => _lastUnmatchedOffset;

        /// <summary>
        /// True when the last lex pass (<see cref="Initialize"/> or
        /// <see cref="ApplyEdit"/>) consumed the input to its very end.
        /// When false, check <see cref="LastUnmatchedOffset"/> for the first
        /// unmatched byte (issue #108).
        /// </summary>
        public bool LastLexConsumedAllInput => _lastLexConsumedAllInput;

        /// <summary>
        /// Initialize (or re-initialize) the session with full source text,
        /// performing a complete lex.
        /// </summary>
        /// <param name="source">The full source text.</param>
        public void Initialize(string source)
        {
            _source = source ?? throw new ArgumentNullException(nameof(source));
            EnsureGrammarLoaded();
            _tokens = LexRange(_source, 0);
            LastRelexedTokenCount = _tokens.Count;
            LastParseUsedCache = false;
            LastParseResumeBoundary = 0;
            if (EnableIncrementalParsing)
            {
                // Cold start for Selective Path Invalidation (issue
                // #101): a full parse whose per-boundary path snapshots
                // populate the cache the next edit resumes from.
                RunParse(resumeBoundary: 0, usedCache: false);
            }
            else
            {
                _parseCache.Clear();
                _lastParse = null;
            }

            Version = 0;
            Reparsed?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>
        /// Apply a text edit and incrementally re-lex the affected region.
        /// The region from the first token that ends at or after the edit
        /// offset is re-lexed; unchanged tail tokens that align with the
        /// re-lexed result at their shifted positions are reused so that
        /// consumers keep token object identity for unchanged text.
        /// </summary>
        /// <remarks>
        /// The re-lex is windowed (issue #144): once
        /// <see cref="RealignmentWindowTokens"/> consecutive freshly lexed
        /// tokens match old tail tokens exactly, the byte-identical suffix
        /// is spliced from the old tail instead of being lexed again, so
        /// the edit cost is O(edit) rather than O(document suffix). When no
        /// realignment is found - pathological edits, grammar with lexical
        /// contexts spanning the edit, or edits near the end of text - the
        /// suffix is re-lexed to the end and the cost remains O(suffix).
        /// </remarks>
        /// <param name="offset">Zero-based byte offset of the edit in the current source text.</param>
        /// <param name="deleteLength">Number of bytes to delete starting at <paramref name="offset"/>.</param>
        /// <param name="insertText">The text to insert at <paramref name="offset"/> (may be empty).</param>
        /// <exception cref="ArgumentOutOfRangeException">Thrown when the edit is out of bounds.</exception>
        public void ApplyEdit(int offset, int deleteLength, string insertText)
        {
            if (offset < 0 || offset > _source.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(offset));
            }

            if (deleteLength < 0 || offset + deleteLength > _source.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(deleteLength));
            }

            var oldSource = _source;
            var newSource = _source.Substring(0, offset) + (insertText ?? string.Empty) + _source.Substring(offset + deleteLength);
            var oldBytes = Encoding.UTF8.GetByteCount(oldSource);
            var newBytes = Encoding.UTF8.GetByteCount(newSource);
            var delta = newBytes - oldBytes;

            // Find the first token whose end is at or after the edit offset.
            // Using >= (not >) means a token ending exactly at the offset can
            // merge with inserted text (e.g. inserting '4' at offset 3 into
            // "123" must re-lex the NUMBER token to produce "1234").
            int firstAffected = _tokens.Count;
            for (int i = 0; i < _tokens.Count; i++)
            {
                if (_tokens[i].StartPosition + _tokens[i].Length >= offset)
                {
                    firstAffected = i;
                    break;
                }
            }

            int lexStart = firstAffected < _tokens.Count
                ? _tokens[firstAffected].StartPosition
                : (_tokens.Count > 0 ? _tokens[_tokens.Count - 1].StartPosition + _tokens[_tokens.Count - 1].Length : 0);
            lexStart = Math.Min(lexStart, Math.Min(offset, newBytes));

            _source = newSource;
            var oldTail = _tokens.Skip(firstAffected).ToList();
            var tracker = oldTail.Count > 0
                ? new RealignmentTracker(oldTail, lexStart, delta, offset + deleteLength, RealignmentWindowTokens)
                : null;

            var relexed = LexRange(newSource, lexStart, tracker);

            var merged = new List<StepToken>(relexed);
            if (tracker is { SpliceIndex: >= 0 } splice)
            {
                // Issue #144: the re-lexed stream realigned with the old
                // tail inside the safety window. The suffix beyond the
                // splice point is byte-identical to the old tail, so the
                // remaining tokens are reused by identity instead of being
                // lexed again.
                AppendShiftedTail(merged, oldTail, splice.SpliceIndex, delta, oldSource, newSource);
                CarryTailStallState(lexStart, offset + deleteLength, delta);
            }
            else
            {
                // No realignment within the re-lexed suffix: fall back to
                // end-aligned merging against the fully re-lexed stream.
                merged = MergeWithTail(relexed, oldTail, delta, oldSource, newSource, offset + deleteLength);
            }

            LastRelexedTokenCount = relexed.Count;

            // Issue #76 instrumentation: measure how much of the old tail
            // the deterministic alignment actually reused.
            LastReusedTokenCount = CountReusedTokens(merged, oldTail);
            LastReuseRatio = oldTail.Count == 0
                ? 1.0
                : (double)LastReusedTokenCount / oldTail.Count;

            _tokens = _tokens.Take(firstAffected).Concat(merged).ToList();

            if (EnableIncrementalParsing)
            {
                // Issue #101, Selective Path Invalidation: the tokens
                // below firstAffected are unchanged (reused by identity),
                // so phase 1 maps the edit to the first affected token
                // boundary, phase 2 invalidates cached snapshots at or
                // after it, and phases 3-4 resume the parse from the
                // nearest surviving snapshot and integrate the new
                // snapshots back into the cache.
                var impact = EditImpactAnalyzer.Analyze(firstAffected, offset, delta);
                _parseCache.InvalidateFrom(impact.FirstAffectedTokenBoundary);

                if (_lastParse != null
                    && _parseCache.TryGetNearestAtOrBelow(
                        impact.FirstAffectedTokenBoundary,
                        out int resumeBoundary,
                        out var snapshot))
                {
                    var parser = _engine.InternalParser;
                    var restored = ParsePathCache.RestoreClone(snapshot);
                    var sourceUtf8 = Encoding.UTF8.GetBytes(_source);
                    parser.RestorePaths(
                        restored, _tokens, new ReadOnlyMemory<byte>(sourceUtf8), resumeBoundary);
                    RunParse(resumeBoundary, usedCache: true);
                }
                else
                {
                    // Cache miss below the impact boundary (deleted
                    // history, a disabled-then-re-enabled session, or the
                    // very first edit after a cold start): cold-start
                    // full parse with checkpoint capture.
                    RunParse(resumeBoundary: 0, usedCache: false);
                }
            }

            Version++;
            Reparsed?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>
        /// Drive the engine's step parser over the session's current
        /// token stream to completion, capturing per-boundary path
        /// snapshots into the cache as the parse advances (issue #101).
        /// </summary>
        /// <param name="resumeBoundary">
        /// Token boundary the parse started from: 0 for a cold start or
        /// the restored boundary on a cache hit.
        /// </param>
        /// <param name="usedCache">
        /// Whether the parse was resumed from a cached snapshot.
        /// </param>
        private void RunParse(int resumeBoundary, bool usedCache)
        {
            var parser = _engine.InternalParser;

            long nodeWritesBefore = 0;
            if (!usedCache)
            {
                _parseCache.Clear();
                var sourceUtf8 = Encoding.UTF8.GetBytes(_source);
                parser.Initialize(_tokens, new ReadOnlyMemory<byte>(sourceUtf8));
            }
            else
            {
                // Issue #163: the cache-resumed parse keeps appending to
                // the same graph builder; measure where this parse's own
                // writes start so the retained-staleness check below can
                // compare fresh work against accumulated garbage.
                nodeWritesBefore = parser.GraphNodeWriteCount;
            }

            var result = new StepParsingResult
            {
                Tokens = new List<StepToken>(_tokens),
            };
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();

            // Mirrors StepParserEngine.Parse's step loop: same step
            // budget (ParseStepBudget, issue #158: previously the two entry
            // points ran slightly different budgets), same stall
            // detection, same completion handling.
            var maxParserSteps = ParseStepBudget.ForTokenCount(_tokens.Count);
            var parserStepCount = 0;
            var lastPosition = -1;
            var stuckCount = 0;

            while (parserStepCount < maxParserSteps)
            {
                var parserResult = parser.Step();
                parserStepCount++;

                if (!parserResult.IsComplete)
                {
                    _parseCache.Store(parser.Context.CurrentTokenIndex, parser.CaptureActivePaths());
                }

                if (parserResult.IsComplete)
                {
                    result.CognitiveGraph = parser.SelectBestParseGraph();
                    result.AmbiguousParses = parser.HandleAmbiguity();
                    break;
                }

                if (parserResult.ActivePathCount == 0)
                {
                    result.Errors.Add($"Parse error at position {parserResult.CurrentPosition}");
                    break;
                }

                if (parserResult.CurrentPosition == lastPosition)
                {
                    stuckCount++;
                    if (stuckCount > 5)
                    {
                        result.Errors.Add(
                            $"Parser appears stuck at position {parserResult.CurrentPosition} after {parserStepCount} steps");
                        break;
                    }
                }
                else
                {
                    stuckCount = 0;
                    lastPosition = parserResult.CurrentPosition;
                }
            }

            result.Success = result.CognitiveGraph != null;
            result.PathCount = parser.ActivePaths.Count;
            result.Context = parser.Context;
            result.ResolvedTokens = result.Success ? parser.GetResolvedTokens() : Array.Empty<StepToken>();
            stopwatch.Stop();
            result.ParseTime = stopwatch.Elapsed;

            // Issue #163: a cache-resumed parse replaces only part of the
            // retained graph content (the re-stepped suffix); the builder
            // keeps every earlier parse's nodes. Once the accumulated
            // writes dominate the current parse's own work by the
            // retained-graph factor, drop the cache so the next edit
            // cold-starts (Initialize disposes and re-creates the builder,
            // bounding session memory instead of growing it linearly in
            // the edit history until the builder's capacity doubling
            // OOM-kills the process).
            if (usedCache)
            {
                var total = parser.GraphNodeWriteCount;
                var own = Math.Max(1, total - nodeWritesBefore);
                if (total > (long)RetainedGraphNodeFactor * own)
                {
                    _parseCache.Clear();
                }
            }

            _lastParse = result;
            LastParseStepCount = parserStepCount;
            LastParseResumeBoundary = resumeBoundary;
            LastParseUsedCache = usedCache;
        }

        /// <summary>
        /// Carry the end-of-input stall state across a spliced tail
        /// (issue #144 windowed re-lex). The bytes after the splice point
        /// were already analyzed before the edit, so a stall there keeps
        /// applying, shifted by the edit delta; a stall inside the deleted
        /// or re-lexed region no longer exists (the window is verified
        /// clean for the splice to fire); a stall inside the untouched
        /// prefix is unaffected.
        /// </summary>
        /// <param name="lexStart">Byte offset the re-lex started at.</param>
        /// <param name="oldEditEnd">Byte offset just past the deleted region in the old text.</param>
        /// <param name="delta">Byte count change caused by the edit.</param>
        private void CarryTailStallState(int lexStart, int oldEditEnd, int delta)
        {
            if (_lastUnmatchedOffset < 0)
            {
                return;
            }

            if (_lastUnmatchedOffset >= oldEditEnd)
            {
                // Stall is inside the re-used tail: shift it with the edit.
                _lastUnmatchedOffset += delta;
                return;
            }

            if (_lastUnmatchedOffset >= lexStart)
            {
                // Stall was inside the re-lexed window (which is clean) or
                // the deleted region: it no longer exists.
                _lastUnmatchedOffset = -1;
                _lastLexConsumedAllInput = true;
            }

            // Otherwise the stall is inside the untouched prefix: keep it.
        }

        /// <summary>
        /// Get the token containing the given byte offset, if any.
        /// </summary>
        /// <param name="offset">Zero-based byte offset.</param>
        /// <returns>The containing token, or <see langword="null"/> when the offset falls between tokens.</returns>
        public StepToken? GetTokenAt(int offset)
        {
            return _tokens.FirstOrDefault(t => t.StartPosition <= offset && offset < t.StartPosition + t.Length);
        }

        /// <summary>
        /// Get all tokens that overlap the given byte range.
        /// </summary>
        /// <param name="start">Zero-based byte offset of the range start.</param>
        /// <param name="end">Byte offset of the range end (exclusive).</param>
        /// <returns>All tokens overlapping <c>[start, end)</c>, ordered by position.</returns>
        public IEnumerable<StepToken> GetTokensInRange(int start, int end)
        {
            return _tokens.Where(t => t.StartPosition < end && t.StartPosition + t.Length > start);
        }

        /// <summary>
        /// Run a full parse of the current text with the underlying engine.
        /// </summary>
        /// <returns>The complete parsing result, including structured diagnostics.</returns>
        public StepParsingResult Parse()
        {
            return _engine.Parse(_source, _fileName);
        }

        /// <summary>
        /// Attempt to reuse the old token tail after a re-lex: old token
        /// objects whose type, value and shifted position align with the
        /// re-lexed suffix are reused (with adjusted positions) so consumers
        /// keep object identity for unchanged text. Alignment is checked
        /// from the end of both lists (both extend to the end of text),
        /// which stays correct even when the edit changes the token count.
        /// </summary>
        /// <param name="relexed">The re-lexed tokens covering the affected region to the end of text.</param>
        /// <param name="oldTail">The old tokens from the first affected token onward.</param>
        /// <param name="delta">Byte count change caused by the edit.</param>
        /// <param name="oldSource">The source text before the edit.</param>
        /// <param name="newSource">The source text after the edit.</param>
        /// <param name="oldEditEnd">The byte offset just past the deleted region in the old text.</param>
        /// <returns>The merged token list covering the affected region to the end of text.</returns>
        private static List<StepToken> MergeWithTail(
            List<StepToken> relexed,
            List<StepToken> oldTail,
            int delta,
            string oldSource,
            string newSource,
            int oldEditEnd)
        {
            if (relexed.Count == 0 || oldTail.Count == 0)
            {
                return relexed;
            }

            // Find the longest suffix of old tail tokens that aligns with
            // the re-lexed suffix. Tokens entirely after the edit shift by
            // the edit delta; the boundary token that starts before the
            // edit is never reused (the re-lexed prefix covers it).
            int aligned = 0;
            int maxCheck = Math.Min(relexed.Count, oldTail.Count);
            for (int j = 1; j <= maxCheck; j++)
            {
                var fresh = relexed[^j];
                var old = oldTail[^j];
                if (old.StartPosition < oldEditEnd)
                {
                    break;
                }

                if (fresh.Type == old.Type && fresh.Value == old.Value && fresh.StartPosition == old.StartPosition + delta)
                {
                    aligned = j;
                }
                else
                {
                    break;
                }
            }

            if (aligned == 0)
            {
                return relexed;
            }

            // Reuse old token objects for the aligned suffix.
            var result = relexed.Take(relexed.Count - aligned).ToList();
            AppendShiftedTail(result, oldTail, oldTail.Count - aligned, delta, oldSource, newSource);
            return result;
        }

        /// <summary>
        /// Append old tail tokens from <paramref name="fromIndex"/> onward
        /// to the result list, reusing the token objects by identity:
        /// StartPosition shifts by the edit delta and line/column
        /// locations are adjusted relative to the first reused (anchor)
        /// token. Shared by the end-aligned tail merge and the
        /// realignment splice of the windowed re-lex (issue #144).
        /// </summary>
        /// <param name="result">The merged token list to append to.</param>
        /// <param name="oldTail">The old tokens from the first affected token onward.</param>
        /// <param name="fromIndex">Index in <paramref name="oldTail"/> of the first token to reuse (the anchor).</param>
        /// <param name="delta">Byte count change caused by the edit.</param>
        /// <param name="oldSource">The source text before the edit.</param>
        /// <param name="newSource">The source text after the edit.</param>
        private static void AppendShiftedTail(
            List<StepToken> result,
            List<StepToken> oldTail,
            int fromIndex,
            int delta,
            string oldSource,
            string newSource)
        {
            var anchor = oldTail[fromIndex];
            var (oldAnchorLine, oldAnchorColumn) = ComputeLineColumn(oldSource, anchor.StartPosition);
            var (newAnchorLine, newAnchorColumn) = ComputeLineColumn(newSource, anchor.StartPosition + delta);
            int deltaLine = newAnchorLine - oldAnchorLine;
            int deltaColumn = newAnchorColumn - oldAnchorColumn;

            for (int i = fromIndex; i < oldTail.Count; i++)
            {
                var token = oldTail[i];
                token.StartPosition += delta;
                AdjustLocation(token, oldAnchorLine, deltaLine, deltaColumn);
                result.Add(token);
            }
        }

        /// <summary>
        /// Count how many of the merged tokens are old tail token objects
        /// kept by reference (i.e. genuinely reused rather than re-lexed).
        /// </summary>
        /// <param name="merged">The merged token list after an edit.</param>
        /// <param name="oldTail">The old tail tokens the merge started from.</param>
        /// <returns>The number of merged tokens reference-identical to an old tail token.</returns>
        private static int CountReusedTokens(List<StepToken> merged, List<StepToken> oldTail)
        {
            if (merged.Count == 0 || oldTail.Count == 0)
            {
                return 0;
            }

            var oldRefs = new HashSet<StepToken>(oldTail, ReferenceEqualityComparer.Instance);
            var reused = 0;
            foreach (var token in merged)
            {
                if (oldRefs.Contains(token))
                {
                    reused++;
                }
            }

            return reused;
        }

        /// <summary>
        /// Adjust the line/column location of a reused token after an edit
        /// shifted its position.
        /// </summary>
        /// <param name="token">The reused token (StartPosition already adjusted).</param>
        /// <param name="oldAnchorLine">The line of the first reused token before the edit.</param>
        /// <param name="deltaLine">Line change between old and new anchor positions.</param>
        /// <param name="deltaColumn">Column change between old and new anchor positions.</param>
        private static void AdjustLocation(StepToken token, int oldAnchorLine, int deltaLine, int deltaColumn)
        {
            if (token.Location is not CodeLocation location)
            {
                return;
            }

            int startLine = location.StartLine;
            int startColumn = location.StartColumn;
            int endLine = location.EndLine;
            int endColumn = location.EndColumn;

            if (startLine == oldAnchorLine)
            {
                startLine += deltaLine;
                startColumn += deltaColumn;
            }
            else if (startLine > oldAnchorLine)
            {
                startLine += deltaLine;
            }

            if (endLine == oldAnchorLine)
            {
                endLine += deltaLine;
                endColumn += deltaColumn;
            }
            else if (endLine > oldAnchorLine)
            {
                endLine += deltaLine;
            }

            token.Location = new CodeLocation(location.File, startLine, startColumn, endLine, endColumn, location.Context);
        }

        /// <summary>
        /// Lex the given text starting at a byte offset, producing tokens
        /// with absolute byte positions and adjusted line/column locations.
        /// </summary>
        /// <param name="text">The full source text.</param>
        /// <param name="start">Zero-based byte offset to start lexing at.</param>
        /// <param name="tracker">
        /// Optional realignment tracker for incremental re-lexes
        /// (issue #144). When the freshly lexed tokens realign with the
        /// old tail inside the safety window, lexing stops at the splice
        /// point and the stall-state updates below do not run (the
        /// spliced tail's state is carried by the caller instead).
        /// </param>
        /// <returns>The tokens from <paramref name="start"/> to the end of text or the realignment splice point.</returns>
        private List<StepToken> LexRange(string text, int start, RealignmentTracker? tracker = null)
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            start = Math.Max(0, Math.Min(start, bytes.Length));

            _lexer.Initialize(new ReadOnlyMemory<byte>(bytes).Slice(start));
            var tokens = new List<StepToken>();

            var maxSteps = (bytes.Length - start) * 10 + 100;
            var steps = 0;
            while (_lexer.ActivePaths.Any(p => p.IsValid && p.Position < bytes.Length - start) && steps < maxSteps)
            {
                var stepResult = _lexer.Step();
                tokens.AddRange(stepResult.NewTokens);
                steps++;

                if (tracker != null)
                {
                    tracker.Inspect(tokens);
                    if (tracker.SpliceIndex >= 0)
                    {
                        // Keep fresh tokens only up to the realignment
                        // splice point; the suffix comes from the old tail.
                        tokens.RemoveRange(tracker.FreshKeepCount, tokens.Count - tracker.FreshKeepCount);
                        break;
                    }
                }

                if (stepResult.IsComplete)
                {
                    break;
                }
            }

            // Issue #108: surface a silent stall instead of returning a
            // prefix-only token list. LastNoMatchPosition is relative to the
            // sliced input, so shift it back to an absolute byte offset.
            // On an early realignment splice the tail state is carried by
            // the caller instead (issue #144).
            if (tracker is not { SpliceIndex: >= 0 })
            {
                _lastUnmatchedOffset = _lexer.LastNoMatchPosition >= 0
                    ? _lexer.LastNoMatchPosition + start
                    : -1;
                _lastLexConsumedAllInput = _lexer.ActivePaths.Any(p => p.IsValid && p.Position >= bytes.Length - start);
            }

            var (baseLine, baseColumn) = ComputeLineColumn(text, start);
            foreach (var token in tokens)
            {
                token.StartPosition += start;
                if (token.Location is CodeLocation location)
                {
                    int startColumn = location.StartLine == 1 ? location.StartColumn + baseColumn - 1 : location.StartColumn;
                    int endColumn = location.EndLine == 1 ? location.EndColumn + baseColumn - 1 : location.EndColumn;
                    token.Location = new CodeLocation(
                        location.File,
                        location.StartLine + baseLine - 1,
                        startColumn,
                        location.EndLine + baseLine - 1,
                        endColumn,
                        location.Context);
                }
            }

            return tokens;
        }

        /// <summary>
        /// Compute the one-based line and byte-based column of a byte offset
        /// in the source text.
        /// </summary>
        /// <param name="text">The source text.</param>
        /// <param name="byteOffset">Zero-based byte offset.</param>
        /// <returns>The one-based line and one-based byte-based column.</returns>
        private static (int line, int column) ComputeLineColumn(string text, int byteOffset)
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            int bounded = Math.Max(0, Math.Min(byteOffset, bytes.Length));

            int line = 1;
            int lineStart = 0;
            for (int i = 0; i < bounded; i++)
            {
                if (bytes[i] == (byte)'\n')
                {
                    line++;
                    lineStart = i + 1;
                }
            }

            return (line, bounded - lineStart + 1);
        }

        /// <summary>
        /// Ensure the backing engine has a grammar loaded and configure the
        /// internal lexer with the grammar's token rules.
        /// </summary>
        private void EnsureGrammarLoaded()
        {
            if (_grammarLoaded)
            {
                return;
            }

            var grammar = _engine.CurrentGrammar
                ?? throw new InvalidOperationException("A grammar must be loaded on the engine before initializing a real-time parsing session.");

            _lexer.Mode = _lexingMode;
            foreach (var rule in grammar.TokenRules)
            {
                _lexer.AddRule(rule);
            }

            _grammarLoaded = true;
        }

        /// <summary>
        /// Tracks forward realignment of an incremental re-lex against the
        /// previous token tail (issue #144 windowed re-lex). Once
        /// <see cref="RealignmentWindowTokens"/> consecutive freshly lexed
        /// tokens match old tail tokens exactly - same type, same value
        /// bytes, same context, position aligned at the edit's byte delta -
        /// the re-lex has demonstrably converged with the state that
        /// produced the old tail, and the remaining byte-identical suffix
        /// is spliced from it instead of being lexed again. Only old tokens
        /// that start at or after the end of the deleted region can anchor
        /// a run, mirroring the boundary rule of the end-aligned merge.
        /// </summary>
        private sealed class RealignmentTracker
        {
            private readonly List<StepToken> _oldTail;
            private readonly int _lexStart;
            private readonly int _delta;
            private readonly int _oldEditEnd;
            private readonly int _window;
            private int _processed;
            private int _run;
            private int _runEnd;

            /// <summary>
            /// Initializes a tracker for one re-lex pass.
            /// </summary>
            /// <param name="oldTail">The old tokens the re-lex synchronizes against (absolute positions).</param>
            /// <param name="lexStart">Absolute byte offset the re-lex starts at; fresh token positions are relative to it while lexing.</param>
            /// <param name="delta">Byte count change caused by the edit.</param>
            /// <param name="oldEditEnd">Byte offset just past the deleted region in the old text.</param>
            /// <param name="window">Required number of consecutive aligned tokens (safety window).</param>
            internal RealignmentTracker(List<StepToken> oldTail, int lexStart, int delta, int oldEditEnd, int window)
            {
                _oldTail = oldTail;
                _lexStart = lexStart;
                _delta = delta;
                _oldEditEnd = oldEditEnd;
                _window = window;
            }

            /// <summary>
            /// Index in the old tail the remaining suffix is spliced from
            /// once realignment succeeded, or -1 while lexing must
            /// continue.
            /// </summary>
            internal int SpliceIndex { get; private set; } = -1;

            /// <summary>
            /// Number of freshly lexed tokens kept when the splice fired;
            /// anything beyond them was discarded in favor of the splice.
            /// </summary>
            internal int FreshKeepCount { get; private set; }

            /// <summary>
            /// Inspect the freshly lexed tokens collected so far and
            /// advance the realignment run; fires the splice once the
            /// safety window is satisfied.
            /// </summary>
            /// <param name="fresh">The freshly lexed token list (grows between calls).</param>
            internal void Inspect(List<StepToken> fresh)
            {
                while (SpliceIndex < 0 && _processed < fresh.Count)
                {
                    var token = fresh[_processed++];

                    if (_run > 0 && Matches(token, _oldTail[_runEnd]))
                    {
                        _run++;
                        _runEnd++;
                    }
                    else
                    {
                        _run = 0;

                        // Try to start a new run: the fresh token must sit
                        // exactly on a shifted old token boundary that lies
                        // at or after the deleted region.
                        var candidate = FindOldIndexAt(token.StartPosition + _lexStart - _delta);
                        if (candidate < 0
                            || _oldTail[candidate].StartPosition < _oldEditEnd
                            || !Matches(token, _oldTail[candidate]))
                        {
                            continue;
                        }

                        _run = 1;
                        _runEnd = candidate + 1;
                    }

                    if (_run >= _window && _runEnd < _oldTail.Count)
                    {
                        SpliceIndex = _runEnd;
                        FreshKeepCount = _processed;
                    }
                }
            }

            /// <summary>
            /// Check whether a freshly lexed token exactly matches an old
            /// tail token: same type, same length, same value bytes (view
            /// comparison, no string materialization) and same relative
            /// context, at the old position shifted by the edit delta.
            /// </summary>
            /// <param name="fresh">The freshly lexed token.</param>
            /// <param name="old">The old tail token candidate.</param>
            /// <returns>True when the tokens align exactly.</returns>
            private bool Matches(StepToken fresh, StepToken old)
            {
                if (fresh.Type != old.Type
                    || fresh.StartPosition + _lexStart != old.StartPosition + _delta
                    || fresh.Length != old.Length
                    || !string.Equals(fresh.Context, old.Context, StringComparison.Ordinal))
                {
                    return false;
                }

                if (fresh.TryGetValueView(out var freshView) && old.TryGetValueView(out var oldView))
                {
                    return freshView.AsSpan().SequenceEqual(oldView.AsSpan());
                }

                return string.Equals(fresh.Value, old.Value, StringComparison.Ordinal);
            }

            /// <summary>
            /// Binary-search the old tail (ordered by StartPosition) for
            /// the leftmost token starting exactly at the given old-text
            /// position, or -1 when none matches.
            /// </summary>
            /// <param name="oldStartPosition">Start position in the old text's byte space.</param>
            /// <returns>The leftmost matching old tail index, or -1.</returns>
            private int FindOldIndexAt(int oldStartPosition)
            {
                if (oldStartPosition < 0)
                {
                    return -1;
                }

                int lo = 0;
                int hi = _oldTail.Count - 1;
                int best = -1;
                while (lo <= hi)
                {
                    int mid = lo + (hi - lo) / 2;
                    int position = _oldTail[mid].StartPosition;
                    if (position < oldStartPosition)
                    {
                        lo = mid + 1;
                    }
                    else if (position > oldStartPosition)
                    {
                        hi = mid - 1;
                    }
                    else
                    {
                        best = mid;
                        hi = mid - 1;
                    }
                }

                return best;
            }
        }
    }
}
