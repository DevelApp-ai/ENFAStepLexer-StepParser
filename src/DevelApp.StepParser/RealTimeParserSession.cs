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
        /// Occurs after the token list has been updated by
        /// <see cref="ApplyEdit"/> or <see cref="Initialize"/>. Read
        /// <see cref="Version"/> and <see cref="Tokens"/> in the handler.
        /// </summary>
        public event EventHandler? Reparsed;

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
            var relexed = LexRange(newSource, lexStart);
            var oldTail = _tokens.Skip(firstAffected).ToList();

            var merged = MergeWithTail(relexed, oldTail, delta, oldSource, newSource, offset + deleteLength);

            // Issue #76 instrumentation: measure how much of the old tail
            // the deterministic alignment actually reused.
            LastReusedTokenCount = CountReusedTokens(merged, oldTail);
            LastReuseRatio = oldTail.Count == 0
                ? 1.0
                : (double)LastReusedTokenCount / oldTail.Count;

            _tokens = _tokens.Take(firstAffected).Concat(merged).ToList();
            Version++;
            Reparsed?.Invoke(this, EventArgs.Empty);
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
            var anchor = oldTail[^aligned];
            var (oldAnchorLine, oldAnchorColumn) = ComputeLineColumn(oldSource, anchor.StartPosition);
            var (newAnchorLine, newAnchorColumn) = ComputeLineColumn(newSource, anchor.StartPosition + delta);
            int deltaLine = newAnchorLine - oldAnchorLine;
            int deltaColumn = newAnchorColumn - oldAnchorColumn;

            for (int i = oldTail.Count - aligned; i < oldTail.Count; i++)
            {
                var token = oldTail[i];
                token.StartPosition += delta;
                AdjustLocation(token, oldAnchorLine, deltaLine, deltaColumn);
                result.Add(token);
            }

            return result;
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
        /// <returns>The tokens from <paramref name="start"/> to the end of text.</returns>
        private List<StepToken> LexRange(string text, int start)
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
                if (stepResult.IsComplete)
                {
                    break;
                }
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

            foreach (var rule in grammar.TokenRules)
            {
                _lexer.AddRule(rule);
            }

            _grammarLoaded = true;
        }
    }
}
