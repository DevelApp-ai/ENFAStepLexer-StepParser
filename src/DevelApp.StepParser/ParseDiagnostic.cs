using System;
using System.Collections.Generic;

namespace DevelApp.StepParser
{
    /// <summary>
    /// Severity of a <see cref="ParseDiagnostic"/>.
    /// </summary>
    public enum DiagnosticSeverity
    {
        /// <summary>Informational message, does not affect parsing success.</summary>
        Information,

        /// <summary>Warning about a potentially unintended construct.</summary>
        Warning,

        /// <summary>Error that prevents successful parsing.</summary>
        Error
    }

    /// <summary>
    /// A structured diagnostic (error, warning or information) produced while
    /// parsing, with precise source location and rendering support for IDEs
    /// and command-line output.
    /// </summary>
    public class ParseDiagnostic
    {
        /// <summary>
        /// Gets or sets the stable diagnostic code (e.g. <c>LX1001</c>).
        /// </summary>
        public string Code { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the severity of the diagnostic.
        /// </summary>
        public DiagnosticSeverity Severity { get; set; } = DiagnosticSeverity.Error;

        /// <summary>
        /// Gets or sets the human-readable diagnostic message.
        /// </summary>
        public string Message { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the name of the file the diagnostic refers to.
        /// </summary>
        public string FileName { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the one-based line number of the diagnostic location.
        /// </summary>
        public int Line { get; set; }

        /// <summary>
        /// Gets or sets the one-based column number of the diagnostic location.
        /// </summary>
        public int Column { get; set; }

        /// <summary>
        /// Gets or sets the zero-based byte offset of the diagnostic location.
        /// </summary>
        public int Position { get; set; }

        /// <summary>
        /// Gets or sets the source line containing the diagnostic location,
        /// or an empty string when unavailable.
        /// </summary>
        public string SourceLine { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets an optional hint suggesting how to resolve the diagnostic.
        /// </summary>
        public string Hint { get; set; } = string.Empty;

        /// <summary>
        /// Initializes a new empty diagnostic. Only intended for serializers;
        /// use <see cref="ParseDiagnostic(string, DiagnosticSeverity, string)"/>.
        /// </summary>
        public ParseDiagnostic() { }

        /// <summary>
        /// Initializes a new diagnostic with the given code, severity and message.
        /// </summary>
        /// <param name="code">The stable diagnostic code.</param>
        /// <param name="severity">The diagnostic severity.</param>
        /// <param name="message">The human-readable message.</param>
        public ParseDiagnostic(string code, DiagnosticSeverity severity, string message)
        {
            Code = code;
            Severity = severity;
            Message = message;
        }

        /// <summary>
        /// Renders the diagnostic as a single-line string with location
        /// information (<c>file(line,col): code: message</c>).
        /// </summary>
        /// <returns>The rendered diagnostic line.</returns>
        public string ToDisplayString()
        {
            var location = string.IsNullOrEmpty(FileName)
                ? string.Empty
                : $"{FileName}({Line},{Column}): ";
            var severityText = Severity switch
            {
                DiagnosticSeverity.Information => "info",
                DiagnosticSeverity.Warning => "warning",
                _ => "error"
            };
            return $"{location}{severityText} {Code}: {Message}";
        }

        /// <summary>
        /// Renders the diagnostic as a source excerpt with a caret pointing
        /// at the diagnostic column, similar to compiler output.
        /// </summary>
        /// <returns>A multi-line string containing the source line and a caret marker.</returns>
        public string ToSourceExcerpt()
        {
            if (string.IsNullOrEmpty(SourceLine))
            {
                return ToDisplayString();
            }

            int caretOffset = Math.Max(0, Math.Min(Column - 1, SourceLine.Length));
            return $"{ToDisplayString()}{Environment.NewLine}    {SourceLine}{Environment.NewLine}    {new string(' ', caretOffset)}^";
        }

        /// <summary>
        /// Returns the display string of the diagnostic.
        /// </summary>
        /// <returns>The rendered diagnostic.</returns>
        public override string ToString() => ToDisplayString();
    }

    /// <summary>
    /// Stable diagnostic codes used by the lexer, parser and grammar loader.
    /// </summary>
    public static class DiagnosticCodes
    {
        /// <summary>Lexer: input could not be matched by any token rule.</summary>
        public const string LexerUnexpectedInput = "LX1001";

        /// <summary>Lexer: the step lexer stopped making progress.</summary>
        public const string LexerStalled = "LX1002";

        /// <summary>Parser: a production rule failed to match the token stream.</summary>
        public const string ParserParseError = "PS2001";

        /// <summary>Parser: the parser stopped making progress.</summary>
        public const string ParserStalled = "PS2002";

        /// <summary>Parser: an unexpected internal exception occurred.</summary>
        public const string ParserInternalError = "PS2003";

        /// <summary>Grammar: a grammar marked as non-inheritable was imported.</summary>
        public const string GrammarNotInheritable = "GR3001";

        /// <summary>Grammar: grammar inheritance contains a cycle.</summary>
        public const string GrammarInheritanceCycle = "GR3002";
    }
}
