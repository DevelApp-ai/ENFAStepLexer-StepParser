using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace DevelApp.StepLexer
{

    /// <summary>
    /// Token produced by the step lexer with position information.
    /// The token value is view-backed (issue #87): when the lexer creates a
    /// token from the UTF-8 input buffer it stores a
    /// <see cref="ZeroCopyStringView"/> over that buffer instead of decoding
    /// the bytes to a string. The UTF-16 form is materialized lazily on the
    /// first <see cref="Value"/> access and only for tokens that are actually
    /// read; tokens that are consumed structurally (parser matching, path
    /// bookkeeping) never pay the decode.
    /// </summary>
    public class StepToken
    {
        private string? _value;

        /// <summary>
        /// The zero-copy view backing <see cref="Value"/>, if the token was
        /// created from the UTF-8 input buffer. Cleared once the value is
        /// materialized or explicitly set.
        /// </summary>
        private ZeroCopyStringView _valueView;

        /// <summary>
        /// Gets or sets the type of the token
        /// </summary>
        public string Type { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the value/content of the token. When the token is
        /// view-backed the first get decodes the view once (issue #87:
        /// conversion at the point of use, not per match); setting a value
        /// replaces and clears the view.
        /// </summary>
        public string Value
        {
            get => _value ??= MaterializeView();
            set
            {
                _value = value;
                _valueView = default;
            }
        }

        /// <summary>
        /// Gets or sets the location of the token in the source code
        /// </summary>
        public ICodeLocation Location { get; set; } = new CodeLocation();

        /// <summary>
        /// Gets or sets the context in which the token was found
        /// </summary>
        public string Context { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets a value indicating whether this token can be split into alternatives
        /// </summary>
        public bool IsSplittable { get; set; }

        /// <summary>
        /// Gets or sets the list of split tokens when ambiguity is resolved
        /// </summary>
        public List<StepToken>? SplitTokens { get; set; }

        /// <summary>
        /// Gets or sets the zero-based byte offset of the first byte of the
        /// token in the source text. Used for incremental (real-time)
        /// re-lexing and edit anchoring.
        /// </summary>
        public int StartPosition { get; set; }

        /// <summary>
        /// Gets or sets the length of the token in bytes.
        /// </summary>
        public int Length { get; set; }

        /// <summary>
        /// Initializes a new instance of the StepToken class
        /// </summary>
        public StepToken() { }

        /// <summary>
        /// Initializes a new instance of the StepToken class with specified parameters
        /// </summary>
        /// <param name="type">The type of the token</param>
        /// <param name="value">The value/content of the token</param>
        /// <param name="location">The location of the token in the source code</param>
        /// <param name="context">The context in which the token was found</param>
        public StepToken(string type, string value, ICodeLocation location, string context = "")
        {
            Type = type;
            Value = value;
            Location = location;
            Context = context;
        }

        /// <summary>
        /// Initializes a new instance of the StepToken class backed by a
        /// zero-copy view over the UTF-8 input buffer (issue #87). The value
        /// is only decoded if and when <see cref="Value"/> is read.
        /// </summary>
        /// <param name="type">The type of the token</param>
        /// <param name="valueView">The zero-copy view of the token text in the UTF-8 input buffer.</param>
        /// <param name="location">The location of the token in the source code</param>
        /// <param name="context">The context in which the token was found</param>
        public StepToken(string type, ZeroCopyStringView valueView, ICodeLocation location, string context = "")
        {
            Type = type;
            _valueView = valueView;
            Length = valueView.Length;
            Location = location;
            Context = context;
        }

        /// <summary>
        /// Try to get the zero-copy UTF-8 view backing this token's value
        /// (issue #87). Returns true when the token is still view-backed
        /// (the value has not been materialized or replaced); callers can
        /// then scan the bytes directly without any decode. Returns false
        /// when the token was created from a string or the value has
        /// already been materialized.
        /// </summary>
        /// <param name="valueView">Receives the zero-copy view of the token text.</param>
        /// <returns>True when a view is available; false otherwise.</returns>
        public bool TryGetValueView(out ZeroCopyStringView valueView)
        {
            valueView = _valueView;
            return !_valueView.IsEmpty || _value is null;
        }

        /// <summary>
        /// Materialize the <see cref="Value"/> from the backing view, if
        /// any. After materialization the view is cleared so the buffer is
        /// no longer kept alive by the token.
        /// </summary>
        /// <returns>The decoded value, or an empty string when the token
        /// has no view and no stored value.</returns>
        private string MaterializeView()
        {
            if (_valueView.IsEmpty)
            {
                return string.Empty;
            }

            var value = _valueView.ToString();
            _valueView = default;
            return value;
        }

        /// <summary>
        /// Returns a string representation of the token
        /// </summary>
        /// <returns>A string in the format "Type:'Value' @Location"</returns>
        public override string ToString()
        {
            return $"{Type}:'{Value}' @{Location}";
        }
    }
}
