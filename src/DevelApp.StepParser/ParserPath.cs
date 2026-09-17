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
    /// Parser path for GLR-style multi-path parsing with CognitiveGraph integration
    /// </summary>
    public class ParserPath
    {
        /// <summary>
        /// Persistent, structurally shared cons cell for the parse stack.
        /// Pushing a symbol allocates one node that references the previous
        /// spine, so cloning a path only copies the head reference (O(1))
        /// instead of duplicating the whole stack.
        /// Each node also caches the depth and an order-sensitive 128-bit
        /// hash of the symbol sequence it covers, so the path key used for
        /// merging identical paths can be produced in O(1) as well.
        /// </summary>
        private sealed class StackNode
        {
            /// <summary>The symbol at this stack position.</summary>
            public readonly GraphNodeRef Value;

            /// <summary>The rest of the stack (elements below this one).</summary>
            public readonly StackNode? Next;

            /// <summary>Number of elements in the stack including this node.</summary>
            public readonly int Depth;

            /// <summary>First half of the cached stack signature hash.</summary>
            public readonly ulong Hash1;

            /// <summary>Second half of the cached stack signature hash.</summary>
            public readonly ulong Hash2;

            public StackNode(GraphNodeRef value, StackNode? next)
            {
                Value = value;
                Next = next;
                Depth = (next?.Depth ?? 0) + 1;

                var nameHash = HashRuleName(value.RuleName);
                Hash1 = ((next?.Hash1 ?? StackSeed1) ^ nameHash) * 1099511628211ul;
                Hash2 = ((next?.Hash2 ?? StackSeed2) + nameHash) * 0x2545F4914F6CDD1Dul;
            }
        }

        /// <summary>
        /// Persistent, structurally shared cons cell for the node offset log.
        /// </summary>
        private sealed class OffsetsNode
        {
            /// <summary>The recorded node offset.</summary>
            public readonly uint Value;

            /// <summary>The previously recorded offsets.</summary>
            public readonly OffsetsNode? Next;

            public OffsetsNode(uint value, OffsetsNode? next)
            {
                Value = value;
                Next = next;
            }
        }

        private const ulong StackSeed1 = 0xCBF29CE484222325ul;
        private const ulong StackSeed2 = 0x9E3779B97F4A7C15ul;

        /// <summary>Gets or sets the unique identifier for this parse path.</summary>
        public int PathId { get; set; }

        /// <summary>Gets or sets the current position in the token stream.</summary>
        public int TokenPosition { get; set; }

        /// <summary>Gets or sets the current parser state.</summary>
        public string CurrentState { get; set; } = string.Empty;

        /// <summary>Gets or sets whether this parse path is still valid.</summary>
        public bool IsValid { get; set; } = true;

        /// <summary>Gets or sets the list of production rules currently being processed.</summary>
        public List<ProductionRule> ActiveProductions { get; set; } = new();

        /// <summary>Gets or sets the state dictionary for parser context information.</summary>
        public Dictionary<string, object> State { get; set; } = new();

        /// <summary>Gets or sets the quality score for this parse path.</summary>
        public float Score { get; set; } = 1.0f; // Path quality score

        /// <summary>
        /// Persistent spine of the parse stack (top element first). This is the
        /// live representation while the legacy <see cref="ParseStack"/> view has
        /// not been materialized.
        /// </summary>
        private StackNode? _stackSpine;

        /// <summary>Depth of <see cref="_stackSpine"/>; authoritative only while the legacy view is not materialized.</summary>
        private int _stackDepth;

        /// <summary>Materialized legacy stack view; while non-null it is the live representation.</summary>
        private Stack<GraphNodeRef>? _stackView;

        /// <summary>Persistent spine of the node offset log (most recent offset first).</summary>
        private OffsetsNode? _offsetsSpine;

        /// <summary>Materialized legacy offset list view; while non-null it is the live representation.</summary>
        private List<uint>? _offsetsView;

        /// <summary>
        /// Gets or sets the parse stack containing graph node references.
        /// The getter materializes (at most once per mutation sequence) a
        /// conventional <see cref="Stack{GraphNodeRef}"/> from the internally
        /// shared persistent representation, and the returned stack then acts
        /// as the live representation until the next clone. The parser engine
        /// itself uses the O(1) internal accessors instead, so ordinary
        /// parsing never pays the materialization cost.
        /// </summary>
        public Stack<GraphNodeRef> ParseStack
        {
            get
            {
                if (_stackView == null)
                {
                    var view = new Stack<GraphNodeRef>(_stackDepth);
                    if (_stackDepth > 0)
                    {
                        // The spine is newest-first; Stack<T>.ToArray is also
                        // newest-first, so fill a bottom-first buffer and push
                        // in order to restore the conventional layout.
                        var buffer = new GraphNodeRef[_stackDepth];
                        var node = _stackSpine;
                        for (int i = _stackDepth - 1; i >= 0 && node != null; i--)
                        {
                            buffer[i] = node.Value;
                            node = node.Next;
                        }
                        for (int i = 0; i < _stackDepth; i++)
                        {
                            view.Push(buffer[i]);
                        }
                    }
                    _stackView = view;
                    _stackSpine = null;
                }
                return _stackView;
            }
            set
            {
                _stackView = value ?? new Stack<GraphNodeRef>();
                _stackSpine = null;
                _stackDepth = _stackView.Count;
            }
        }

        /// <summary>
        /// Gets or sets the list of node offsets for ambiguity handling.
        /// Like <see cref="ParseStack"/>, this is a lazily materialized view
        /// over an internally shared persistent representation.
        /// </summary>
        public List<uint> NodeOffsets
        {
            get
            {
                if (_offsetsView == null)
                {
                    // The spine is newest-first; restore original (oldest-first) order.
                    var buffer = new List<uint>();
                    for (var node = _offsetsSpine; node != null; node = node.Next)
                    {
                        buffer.Add(node.Value);
                    }
                    buffer.Reverse();
                    _offsetsView = buffer;
                    _offsetsSpine = null;
                }
                return _offsetsView;
            }
            set
            {
                _offsetsView = value ?? new List<uint>();
                _offsetsSpine = null;
            }
        }

        /// <summary>
        /// Initializes a new instance of the ParserPath class.
        /// </summary>
        /// <param name="pathId">The unique identifier for this parse path</param>
        public ParserPath(int pathId)
        {
            PathId = pathId;
        }

        /// <summary>Gets the number of symbols on the parse stack.</summary>
        internal int StackDepth => _stackView != null ? _stackView.Count : _stackDepth;

        /// <summary>Pushes a symbol onto the parse stack in O(1).</summary>
        /// <param name="nodeRef">The symbol reference to push.</param>
        internal void PushSymbol(GraphNodeRef nodeRef)
        {
            if (_stackView != null)
            {
                _stackView.Push(nodeRef);
                return;
            }

            _stackSpine = new StackNode(nodeRef, _stackSpine);
            _stackDepth++;
        }

        /// <summary>Pops the top symbol off the parse stack in O(1).</summary>
        /// <returns>The popped symbol reference.</returns>
        internal GraphNodeRef PopSymbol()
        {
            if (_stackView != null)
            {
                return _stackView.Pop();
            }

            if (_stackSpine == null)
            {
                throw new InvalidOperationException("The parse stack is empty.");
            }

            var value = _stackSpine.Value;
            _stackSpine = _stackSpine.Next;
            _stackDepth--;
            return value;
        }

        /// <summary>Returns the top symbol of the parse stack without removing it.</summary>
        /// <returns>The top symbol reference.</returns>
        internal GraphNodeRef PeekSymbol()
        {
            if (_stackView != null)
            {
                return _stackView.Peek();
            }

            if (_stackSpine == null)
            {
                throw new InvalidOperationException("The parse stack is empty.");
            }

            return _stackSpine.Value;
        }

        /// <summary>
        /// Enumerates the parse stack from the top down, matching the
        /// enumeration order of <see cref="Stack{GraphNodeRef}"/>.
        /// </summary>
        internal IEnumerable<GraphNodeRef> StackTopFirst
        {
            get
            {
                if (_stackView != null)
                {
                    return _stackView;
                }
                return EnumerateSpine(_stackSpine);
            }
        }

        private static IEnumerable<GraphNodeRef> EnumerateSpine(StackNode? node)
        {
            while (node != null)
            {
                yield return node.Value;
                node = node.Next;
            }
        }

        /// <summary>
        /// Gets the cached, order-sensitive 128-bit signature hash of the
        /// current stack contents in O(1). Used for merging identical paths.
        /// </summary>
        internal (ulong Hash1, ulong Hash2) StackSignature
        {
            get
            {
                if (_stackView != null)
                {
                    SyncStackViewToSpine();
                }
                return _stackSpine != null
                    ? (_stackSpine.Hash1, _stackSpine.Hash2)
                    : (StackSeed1, StackSeed2);
            }
        }

        /// <summary>Records a node offset in O(1).</summary>
        /// <param name="offset">The node offset to record.</param>
        internal void AddNodeOffset(uint offset)
        {
            if (_offsetsView != null)
            {
                _offsetsView.Add(offset);
                return;
            }
            _offsetsSpine = new OffsetsNode(offset, _offsetsSpine);
        }

        /// <summary>
        /// Creates a deep copy of this parser path with a new path ID.
        /// The parse stack and node offset log are shared structurally with
        /// the persistent spines, so cloning costs O(1) regardless of stack
        /// depth; subsequent mutations of either copy do not affect the other.
        /// </summary>
        /// <param name="newPathId">The unique identifier for the cloned path</param>
        /// <returns>A new ParserPath instance with copied state</returns>
        public ParserPath Clone(int newPathId)
        {
            // Make the persistent spines the live representation so the clone
            // can share them; materialized legacy views are folded back in.
            if (_stackView != null)
            {
                SyncStackViewToSpine();
            }
            if (_offsetsView != null)
            {
                SyncOffsetsViewToSpine();
            }

            var clone = new ParserPath(newPathId)
            {
                TokenPosition = TokenPosition,
                CurrentState = CurrentState,
                IsValid = IsValid,
                ActiveProductions = new List<ProductionRule>(ActiveProductions),
                State = new Dictionary<string, object>(State),
                Score = Score
            };
            clone._stackSpine = _stackSpine;
            clone._stackDepth = _stackDepth;
            clone._offsetsSpine = _offsetsSpine;
            return clone;
        }

        /// <summary>
        /// Folds the materialized legacy stack view back into the persistent
        /// spine. Stack&lt;T&gt;.ToArray yields newest-first; iterating it in
        /// reverse (oldest-first) builds the cons cells so the newest element
        /// ends up at the head of the spine.
        /// </summary>
        private void SyncStackViewToSpine()
        {
            var array = _stackView!.ToArray(); // newest-first
            StackNode? spine = null;
            var depth = 0;
            for (int i = array.Length - 1; i >= 0; i--)
            {
                spine = new StackNode(array[i], spine);
                depth++;
            }
            _stackSpine = spine;
            _stackDepth = depth;
            _stackView = null;
        }

        /// <summary>
        /// Folds the materialized legacy offset list back into the persistent spine.
        /// </summary>
        private void SyncOffsetsViewToSpine()
        {
            OffsetsNode? spine = null;
            foreach (var value in _offsetsView!)
            {
                spine = new OffsetsNode(value, spine);
            }
            _offsetsSpine = spine;
            _offsetsView = null;
        }

        /// <summary>Computes an FNV-1a 64-bit hash of a rule name.</summary>
        private static ulong HashRuleName(string? name)
        {
            var hash = 14695981039346656037ul;
            if (name != null)
            {
                foreach (var c in name)
                {
                    hash ^= c;
                    hash *= 1099511628211ul;
                }
            }
            return hash;
        }
    }
}
