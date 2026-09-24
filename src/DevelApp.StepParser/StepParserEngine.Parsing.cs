using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using DevelApp.StepLexer;
using CognitiveGraph;
using CognitiveGraph.Accessors;
using CognitiveGraph.Schema;

namespace DevelApp.StepParser
{
    public partial class StepParserEngine : IDisposable
    {

        /// <summary>
        /// Parse input text and return complete result
        /// </summary>
        public StepParsingResult Parse(string input, string fileName = "")
        {
            var startTime = DateTime.Now;
            var result = new StepParsingResult();

            try
            {
                // Convert string to UTF-8 bytes for zero-copy processing
                var inputBytes = Encoding.UTF8.GetBytes(input);
                var inputMemory = new ReadOnlyMemory<byte>(inputBytes);

                // Issue #75: install the learned path pruner on the parser
                // only while the ML-assist gate is enabled (default off:
                // full GLR is the default behavior).
                _parser.PathPruner = MlAssistOptions.IsEnabled(
                    MlAssistFeature.LearnedPathPruning)
                        ? PathPruner
                        : null;

                // Issue #74: install the learned rule prioritizer on the
                // lexer only while the ML-assist gate is enabled (default
                // off). The prioritizer orders rule evaluation only; the
                // token stream stays bit-identical either way.
                _lexer.RulePrioritizer = MlAssistOptions.IsEnabled(
                    MlAssistFeature.LearnedRulePrioritization)
                        ? RulePrioritizer
                        : null;

                // Phase 1: Lexical analysis
                _lexer.Initialize(inputMemory, fileName);
                var tokens = new List<StepToken>();

                // Safety limit to prevent infinite loops in lexer
                var maxLexerSteps = inputBytes.Length * 10; // Allow reasonable processing overhead
                var lexerStepCount = 0;
                var lastTokenCount = 0;

                while (!_lexer.ActivePaths.All(p => !p.IsValid || p.Position >= inputBytes.Length) && lexerStepCount < maxLexerSteps)
                {
                    var lexerResult = _lexer.Step();
                    tokens.AddRange(lexerResult.NewTokens);
                    lexerStepCount++;

                    if (lexerResult.IsComplete)
                        break;

                    // Progress check: if no new tokens were produced in the last 10 steps,
                    // break to avoid infinite loops. The reference count is only updated at
                    // each 10-step boundary so that whitespace-only steps (which legitimately
                    // produce no tokens) do not trigger a false stall detection.
                    if (lexerStepCount % 10 == 0)
                    {
                        if (tokens.Count == lastTokenCount)
                        {
                            result.Errors.Add($"Lexer appears stuck at step {lexerStepCount} with no progress");
                            result.Diagnostics.Add(new ParseDiagnostic(
                                DiagnosticCodes.LexerStalled,
                                DiagnosticSeverity.Error,
                                $"Lexer appears stuck at step {lexerStepCount} with no progress")
                            {
                                FileName = fileName
                            });
                            break;
                        }
                        lastTokenCount = tokens.Count;
                    }
                }

                // Diagnose unexpected input: no lexer path consumed the input
                // to its end because no rule matched at some position.
                if (!_lexer.CompletedInput && _lexer.LastNoMatchPosition >= 0 && _lexer.LastNoMatchPosition < inputBytes.Length)
                {
                    var diagnostic = BuildSourceDiagnostic(
                        DiagnosticCodes.LexerUnexpectedInput,
                        DiagnosticSeverity.Error,
                        $"Unexpected input: no token rule matches at byte offset {_lexer.LastNoMatchPosition} (input text was not fully consumed)",
                        input,
                        fileName,
                        _lexer.LastNoMatchPosition,
                        "Add or adjust a token rule that matches this input (for example a catch-all or skippable rule).");
                    result.Diagnostics.Add(diagnostic);
                    result.Errors.Add(diagnostic.ToDisplayString());
                }

                result.Tokens = tokens;

                // Phase 2: Syntactic analysis with GLR parsing
                _parser.Initialize(tokens, input);
                
                // Safety limit to prevent infinite loops in parser
                var maxParserSteps = tokens.Count * 20; // Allow reasonable processing overhead
                var parserStepCount = 0;
                var lastPosition = -1;
                var stuckCount = 0;

                while (parserStepCount < maxParserSteps)
                {
                    var parserResult = _parser.Step();
                    parserStepCount++;
                    
                    if (parserResult.IsComplete)
                    {
                        result.CognitiveGraph = _parser.SelectBestParseGraph();
                        result.AmbiguousParses = _parser.HandleAmbiguity();
                        break;
                    }

                    // Check for parsing failure
                    if (parserResult.ActivePathCount == 0)
                    {
                        result.Errors.Add($"Parse error at position {parserResult.CurrentPosition}");
                        result.Diagnostics.Add(BuildTokenDiagnostic(
                            tokens,
                            input,
                            fileName,
                            parserResult.CurrentPosition,
                            "No grammar production matches the token stream at this position."));
                        break;
                    }

                    // Progress check: if position hasn't advanced in several steps, break to avoid infinite loop
                    if (parserResult.CurrentPosition == lastPosition)
                    {
                        stuckCount++;
                        if (stuckCount > 5)
                        {
                            result.Errors.Add($"Parser appears stuck at position {parserResult.CurrentPosition} after {parserStepCount} steps");
                            result.Diagnostics.Add(BuildTokenDiagnostic(
                                tokens,
                                input,
                                fileName,
                                parserResult.CurrentPosition,
                                $"Parser appears stuck after {parserStepCount} steps; the grammar may be left-recursive in a way that cannot be resolved."));
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
                result.PathCount = _parser.ActivePaths.Count;
                result.Context = _parser.Context;
                
                // Store the last parsed graph and source text for spatial queries
                if (result.Success && result.CognitiveGraph != null)
                {
                    _lastParsedGraph = result.CognitiveGraph;
                    _lastSourceText = input;
                    
                    // Build line-offset map for this file
                    if (!string.IsNullOrEmpty(fileName))
                    {
                        _lineOffsetMaps[fileName] = BuildLineOffsetMap(input);
                    }
                }
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.Errors.Add($"Parsing exception: {ex.Message}");
                result.Diagnostics.Add(new ParseDiagnostic(
                    DiagnosticCodes.ParserInternalError,
                    DiagnosticSeverity.Error,
                    $"Parsing exception: {ex.Message}")
                {
                    FileName = fileName,
                    Hint = ex.StackTrace is null ? string.Empty : "See the stack trace for the internal failure point."
                });
            }

            result.ParseTime = DateTime.Now - startTime;
            return result;
        }

        /// <summary>
        /// Build a diagnostic with line/column information for a byte offset
        /// in the source text by scanning for line boundaries.
        /// </summary>
        /// <param name="code">The stable diagnostic code.</param>
        /// <param name="severity">The diagnostic severity.</param>
        /// <param name="message">The human-readable message.</param>
        /// <param name="input">The full source text.</param>
        /// <param name="fileName">The file the diagnostic refers to.</param>
        /// <param name="bytePosition">The zero-based byte offset of the diagnostic location.</param>
        /// <param name="hint">Optional hint for resolving the diagnostic.</param>
        /// <returns>A populated diagnostic with line, column and source excerpt.</returns>
        private static ParseDiagnostic BuildSourceDiagnostic(
            string code,
            DiagnosticSeverity severity,
            string message,
            string input,
            string fileName,
            int bytePosition,
            string hint = "")
        {
            var diagnostic = new ParseDiagnostic(code, severity, message)
            {
                FileName = fileName,
                Position = bytePosition,
                Hint = hint
            };

            var bytes = Encoding.UTF8.GetBytes(input);
            int boundedPosition = Math.Max(0, Math.Min(bytePosition, bytes.Length));

            int line = 1;
            int lineStart = 0;
            for (int i = 0; i < boundedPosition; i++)
            {
                if (bytes[i] == (byte)'\n')
                {
                    line++;
                    lineStart = i + 1;
                }
            }

            int lineEnd = bytes.Length;
            for (int i = lineStart; i < bytes.Length; i++)
            {
                if (bytes[i] == (byte)'\n' || bytes[i] == (byte)'\r')
                {
                    lineEnd = i;
                    break;
                }
            }

            int column = 1;
            for (int i = lineStart; i < boundedPosition;)
            {
                var (_, consumed) = UTF8Utils.GetNextCodepoint(bytes, i);
                column++;
                i += Math.Max(1, consumed);
            }

            diagnostic.Line = line;
            diagnostic.Column = column;
            diagnostic.SourceLine = input.Length > 0
                ? Encoding.UTF8.GetString(bytes, lineStart, lineEnd - lineStart)
                : string.Empty;
            return diagnostic;
        }

        /// <summary>
        /// Build a diagnostic for a parser error identified by a token index.
        /// </summary>
        /// <param name="tokens">The tokens produced by the lexer.</param>
        /// <param name="input">The full source text.</param>
        /// <param name="fileName">The file the diagnostic refers to.</param>
        /// <param name="tokenIndex">The zero-based index of the offending token.</param>
        /// <param name="message">The human-readable message.</param>
        /// <returns>A populated diagnostic pointing at the offending token, or an unlocated diagnostic when the token index is out of range.</returns>
        private static ParseDiagnostic BuildTokenDiagnostic(
            List<StepToken> tokens,
            string input,
            string fileName,
            int tokenIndex,
            string message)
        {
            if (tokens.Count > 0)
            {
                // Clamp to the last token: a failure position past the end of
                // the token stream points at the end of the input.
                var token = tokens[Math.Clamp(tokenIndex, 0, tokens.Count - 1)];
                var atEnd = tokenIndex >= tokens.Count;
                return BuildSourceDiagnostic(
                    DiagnosticCodes.ParserParseError,
                    DiagnosticSeverity.Error,
                    atEnd
                        ? $"Parse error after the last token ({token.Type} '{token.Value}'): {message}"
                        : $"Parse error at token {tokenIndex} ({token.Type} '{token.Value}'): {message}",
                    input,
                    fileName,
                    atEnd ? token.StartPosition + token.Length : token.StartPosition);
            }

            return new ParseDiagnostic(
                DiagnosticCodes.ParserParseError,
                DiagnosticSeverity.Error,
                $"Parse error at token position {tokenIndex}: {message}")
            {
                FileName = fileName
            };
        }

        /// <summary>
        /// Parse input and merge results into an existing CognitiveGraph
        /// Enables incremental parsing where new files can be added to an existing graph
        /// Uses CognitiveGraph 1.0.2 capabilities for high-performance merging
        /// </summary>
        /// <param name="existingGraph">The existing CognitiveGraph to expand with new parsing results</param>
        /// <param name="input">The input text to parse</param>
        /// <param name="fileName">Optional file name for location tracking</param>
        /// <returns>The expanded CognitiveGraph with merged results, or the original graph if parsing fails</returns>
        public CognitiveGraph.CognitiveGraph ParseAndMerge(CognitiveGraph.CognitiveGraph existingGraph, string input, string fileName = "")
        {
            var parseResult = Parse(input, fileName);
            
            if (!parseResult.Success || parseResult.CognitiveGraph == null)
            {
                // Return original graph if parsing failed
                return existingGraph;
            }

            // For now, return the new graph as the merge implementation requires
            // understanding the CognitiveGraph 1.0.2 fluent API better
            // TODO: Implement proper merging using CognitiveGraph 1.0.2 fluent API
            // when more documentation is available
            return parseResult.CognitiveGraph;
        }

        /// <summary>
        /// Parse multiple files and build a combined CognitiveGraph
        /// Useful for analyzing multiple source files in a project
        /// Uses CognitiveGraph 1.0.2 for efficient multi-file processing
        /// </summary>
        /// <param name="files">Dictionary of file paths and their content</param>
        /// <returns>A StepParsingResult with the last successfully parsed CognitiveGraph</returns>
        public StepParsingResult ParseMultipleFiles(Dictionary<string, string> files)
        {
            CognitiveGraph.CognitiveGraph? lastGraph = null;
            var allTokens = new List<StepToken>();
            var allErrors = new List<string>();
            var allDiagnostics = new List<ParseDiagnostic>();
            var startTime = DateTime.Now;
            var totalPaths = 0;
            var successfulParses = 0;

            foreach (var file in files)
            {
                var parseResult = Parse(file.Value, file.Key);
                allTokens.AddRange(parseResult.Tokens);
                allErrors.AddRange(parseResult.Errors);
                allDiagnostics.AddRange(parseResult.Diagnostics);
                totalPaths += parseResult.PathCount;

                if (parseResult.Success && parseResult.CognitiveGraph != null)
                {
                    lastGraph = parseResult.CognitiveGraph;
                    successfulParses++;
                }
            }

            return new StepParsingResult
            {
                Success = successfulParses > 0,
                CognitiveGraph = lastGraph,
                Tokens = allTokens,
                Errors = allErrors,
                Diagnostics = allDiagnostics,
                ParseTime = DateTime.Now - startTime,
                PathCount = totalPaths,
                Context = _parser.Context
            };
        }
    }
}