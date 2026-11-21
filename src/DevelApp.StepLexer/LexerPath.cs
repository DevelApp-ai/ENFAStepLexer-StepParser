using System;
using System.Collections.Generic;
using System.Text;

namespace DevelApp.StepLexer
{

    /// <summary>
    /// Lexer path for handling multiple tokenization possibilities
    /// </summary>
    public class LexerPath
    {
        /// <summary>
        /// Persistent node of the token spine (most recently appended
        /// token first). Shared by reference between cloned paths, so a
        /// clone is O(1) instead of copying the whole accumulated token
        /// list (issue #157 follow-up, NL-morphology scale: exploratory
        /// lexing forks thousands of paths over word-list grammars and
        /// the O(tokens) clone copy dominated lexing time quadratically).
        /// </summary>
        private sealed class TokenNode
        {
            /// <summary>The token held by this node.</summary>
            public StepToken Value;

            /// <summary>The node of the previously appended token, or null.</summary>
            public TokenNode? Prev;

            /// <summary>Initializes a new node.</summary>
            public TokenNode(StepToken value, TokenNode? prev)
            {
                Value = value;
                Prev = prev;
            }
        }

        /// <summary>
        /// Gets or sets the unique identifier for this path
        /// </summary>
        public int PathId { get; set; }

        /// <summary>
        /// Gets or sets the current position in the input stream
        /// </summary>
        public int Position { get; set; }

        /// <summary>
        /// The live token representation: a persistent spine, most
        /// recently appended token first. Clones share it by reference.
        /// </summary>
        private TokenNode? _tokenSpine;

        /// <summary>Number of tokens on the spine.</summary>
        private int _tokenCount;

        /// <summary>
        /// Lazily materialized legacy view of <see cref="_tokenSpine"/>.
        /// Null while only the spine is authoritative; non-null (and
        /// authoritative until re-synced) once <see cref="Tokens"/> was
        /// read or assigned, so external list mutations stay observable.
        /// </summary>
        private List<StepToken>? _tokensView;

        /// <summary>
        /// Value of <see cref="_tokenCount"/> when <see cref="_tokensView"/>
        /// was last synchronized with the spine; a mismatch means the
        /// view was mutated externally and the spine must be rebuilt from
        /// it before the next spine operation.
        /// </summary>
        private int _tokensViewSyncedCount;

        /// <summary>
        /// Gets or sets the list of tokens found on this path
        /// </summary>
        /// <remarks>
        /// The list is materialized lazily from the path's persistent
        /// token spine. Reading it once materializes it; appending to the
        /// returned list is supported and re-binds the spine on the next
        /// mutation through this path. The lexer's own token emission
        /// appends through the O(1) spine and never materializes.
        /// </remarks>
        public List<StepToken> Tokens
        {
            get
            {
                if (_tokensView == null)
                {
                    var view = new List<StepToken>(_tokenCount);
                    var stack = new Stack<StepToken>(_tokenCount);
                    for (var node = _tokenSpine; node != null; node = node.Prev)
                    {
                        stack.Push(node.Value);
                    }
                    while (stack.Count > 0)
                    {
                        view.Add(stack.Pop());
                    }

                    _tokensView = view;
                    _tokensViewSyncedCount = _tokenCount;
                }

                return _tokensView;
            }
            set
            {
                _tokensView = value ?? new List<StepToken>();
                ResyncSpineFromView();
            }
        }

        /// <summary>
        /// Gets or sets the current parsing context
        /// </summary>
        public string CurrentContext { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets a value indicating whether this path is still valid
        /// </summary>
        public bool IsValid { get; set; } = true;

        /// <summary>
        /// Gets or sets the state dictionary for context-specific data
        /// </summary>
        public Dictionary<string, object> State { get; set; } = new();

        /// <summary>
        /// Cached rolling fingerprint over the token type sequence, used by
        /// path merging. <c>-1</c> marks a cold cache; the pair
        /// (<see cref="_fingerprint"/>, <see cref="_fingerprintCount"/> is
        /// advanced incrementally for append-only token lists.
        /// </summary>
        private long _fingerprint;

        /// <summary>
        /// Number of tokens folded into <see cref="_fingerprint"/>, or
        /// <c>-1</c> when the cache is cold.
        /// </summary>
        private int _fingerprintCount = -1;

        /// <summary>
        /// Initializes a new instance of the LexerPath class
        /// </summary>
        /// <param name="pathId">The unique identifier for this path</param>
        /// <param name="position">The starting position in the input stream</param>
        public LexerPath(int pathId, int position = 0)
        {
            PathId = pathId;
            Position = position;
        }

        /// <summary>
        /// Number of tokens on this path. O(1): reads the spine counter,
        /// never materializes the <see cref="Tokens"/> view.
        /// </summary>
        internal int TokenCount
        {
            get
            {
                ReadoptViewIfMutatedExternally();
                return _tokenCount;
            }
        }

        /// <summary>
        /// Appends a token to this path in O(1) by pushing it onto the
        /// persistent spine (issue #157 follow-up: the previous
        /// implementation appended to the materialized list, and every
        /// clone copied the whole list quadratically). The cached
        /// fingerprint is advanced in the same step, so
        /// <see cref="GetTokenFingerprint"/> stays O(1) as well.
        /// </summary>
        /// <param name="token">The token to append.</param>
        internal void AddToken(StepToken token)
        {
            ReadoptViewIfMutatedExternally();
            _tokenSpine = new TokenNode(token, _tokenSpine);
            _tokenCount++;

            // Keep a materialized view consistent with the spine: the
            // view is authoritative while it exists, so an external
            // reader holding it must observe the append as well.
            if (_tokensView != null)
            {
                _tokensView.Add(token);
                _tokensViewSyncedCount = _tokenCount;
            }

            if (_fingerprintCount >= 0)
            {
                _fingerprint = _fingerprint * 31 + (token.Type?.GetHashCode() ?? 0);
                _fingerprintCount = _tokenCount;
            }
        }

        /// <summary>
        /// Defines this path's token sequence as equal to another's by an
        /// exact, order-sensitive comparison of token types, in the
        /// cheaper direction enabled by the spine representation: both
        /// spines are walked in lockstep from the newest token to the
        /// oldest and the comparison stops at the first shared spine
        /// node, so paths that forked from a common ancestor are compared
        /// in O(forking divergence) instead of O(full sequence length).
        /// Falls back to a full list comparison whenever either path's
        /// token list was materialized and mutated externally (the legacy
        /// view is then authoritative and may not share spine nodes).
        /// </summary>
        /// <param name="other">The path to compare with.</param>
        /// <returns>True when both paths carry the same token type sequence.</returns>
        internal bool TokenTypesEqualFromTop(LexerPath other)
        {
            if (ReferenceEquals(this, other))
            {
                return true;
            }

            ReadoptViewIfMutatedExternally();
            other.ReadoptViewIfMutatedExternally();

            if (_tokensView == null && other._tokensView == null)
            {
                var a = _tokenSpine;
                var b = other._tokenSpine;
                if (_tokenCount != other._tokenCount)
                {
                    return false;
                }

                while (true)
                {
                    if (ReferenceEquals(a, b))
                    {
                        // Shared physical tail (this path was cloned from
                        // the other, or both from a common ancestor) or
                        // both chains are exhausted: everything below is
                        // identical by construction.
                        return true;
                    }

                    if (a == null || b == null)
                    {
                        return a == b;
                    }

                    if (a.Value.Type != b.Value.Type)
                    {
                        return false;
                    }

                    a = a.Prev;
                    b = b.Prev;
                }
            }

            var firstTokens = Tokens;
            var secondTokens = other.Tokens;
            if (firstTokens.Count != secondTokens.Count)
            {
                return false;
            }

            for (int i = 0; i < firstTokens.Count; i++)
            {
                if (firstTokens[i].Type != secondTokens[i].Type)
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Computes an order-sensitive rolling fingerprint over the token
        /// type sequence of this path. The result is cached and advanced
        /// incrementally while tokens are only appended, so calling this
        /// after each token is O(1) amortized instead of O(tokens so far).
        /// </summary>
        /// <returns>A fingerprint of the sequence of token types on this path.</returns>
        /// <remarks>
        /// Fingerprints are used to bucket paths for merging; callers must
        /// still verify true sequence equality when fingerprints collide.
        /// Non-append-only modifications of <see cref="Tokens"/> re-bind
        /// the fingerprint to the externally mutated list.
        /// </remarks>
        internal long GetTokenFingerprint()
        {
            ReadoptViewIfMutatedExternally();

            // Materialized-and-externally-owned view: the legacy list is
            // authoritative, so fold it directly (rare; only reached when
            // caller code holds the public Tokens list and mutates it).
            if (_tokensView != null)
            {
                var tokens = _tokensView;
                if (_fingerprintCount < 0 || tokens.Count < _fingerprintCount)
                {
                    _fingerprint = 0;
                    _fingerprintCount = 0;
                }

                for (int i = _fingerprintCount; i < tokens.Count; i++)
                {
                    var type = tokens[i].Type;
                    _fingerprint = _fingerprint * 31 + (type?.GetHashCode() ?? 0);
                }

                _fingerprintCount = tokens.Count;
                return _fingerprint;
            }

            // Spine-authoritative fast path: the rolling fingerprint was
            // advanced by every AddToken (and cloned along), so a hit is a
            // plain counter check and a miss is a cold recompute.
            if (_fingerprintCount == _tokenCount)
            {
                return _fingerprint;
            }

            _fingerprint = 0;
            var nodes = new StepToken?[_tokenCount];
            var node = _tokenSpine;
            for (int i = _tokenCount - 1; node != null && i >= 0; i--)
            {
                nodes[i] = node.Value;
                node = node.Prev;
            }

            for (int i = 0; i < _tokenCount; i++)
            {
                var type = nodes[i]!.Type;
                _fingerprint = _fingerprint * 31 + (type?.GetHashCode() ?? 0);
            }

            _fingerprintCount = _tokenCount;
            return _fingerprint;
        }

        /// <summary>
        /// Creates a clone of this path with a new path ID. The clone
        /// shares this path's persistent token spine by reference, so
        /// cloning is O(1) in the accumulated token count (issue #157
        /// follow-up, NL-morphology scale).
        /// </summary>
        /// <param name="newPathId">The path ID for the cloned path</param>
        /// <returns>A new LexerPath instance that is a copy of this path</returns>
        public LexerPath Clone(int newPathId)
        {
            ReadoptViewIfMutatedExternally();
            return new LexerPath(newPathId, Position)
            {
                _tokenSpine = _tokenSpine,
                _tokenCount = _tokenCount,
                _fingerprint = _fingerprint,
                _fingerprintCount = _fingerprintCount,
                CurrentContext = CurrentContext,
                IsValid = IsValid,
                State = new Dictionary<string, object>(State)
            };
        }

        /// <summary>
        /// When <see cref="Tokens"/> was materialized and then mutated
        /// externally (a count mismatch with the spine), rebuild the spine
        /// from the view, making the mutation an authoritative part of
        /// this path's token sequence. Inert in the lexer's own loops,
        /// which never hold a materialized view while the spine is live.
        /// </summary>
        private void ReadoptViewIfMutatedExternally()
        {
            if (_tokensView != null && _tokensView.Count != _tokensViewSyncedCount)
            {
                ResyncSpineFromView();
            }
        }

        /// <summary>
        /// Rebuilds the spine and counters from the materialized view.
        /// </summary>
        private void ResyncSpineFromView()
        {
            var view = _tokensView!;
            _tokenSpine = null;
            _tokenCount = 0;
            for (int i = 0; i < view.Count; i++)
            {
                _tokenSpine = new TokenNode(view[i], _tokenSpine);
                _tokenCount++;
            }

            _tokensViewSyncedCount = _tokenCount;
            _fingerprintCount = -1;
        }
    }
}
