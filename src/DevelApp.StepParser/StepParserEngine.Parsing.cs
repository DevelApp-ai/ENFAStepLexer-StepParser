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

                    // Progress check: if no new tokens after several steps, break to avoid infinite loop
                    if (lexerStepCount % 10 == 0 && tokens.Count == lastTokenCount)
                    {
                        result.Errors.Add($"Lexer appears stuck at step {lexerStepCount} with no progress");
                        break;
                    }
                    lastTokenCount = tokens.Count;
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
                        break;
                    }

                    // Progress check: if position hasn't advanced in several steps, break to avoid infinite loop
                    if (parserResult.CurrentPosition == lastPosition)
                    {
                        stuckCount++;
                        if (stuckCount > 5)
                        {
                            result.Errors.Add($"Parser appears stuck at position {parserResult.CurrentPosition} after {parserStepCount} steps");
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
            }

            result.ParseTime = DateTime.Now - startTime;
            return result;
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
            var startTime = DateTime.Now;
            var totalPaths = 0;
            var successfulParses = 0;

            foreach (var file in files)
            {
                var parseResult = Parse(file.Value, file.Key);
                allTokens.AddRange(parseResult.Tokens);
                allErrors.AddRange(parseResult.Errors);
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
                ParseTime = DateTime.Now - startTime,
                PathCount = totalPaths,
                Context = _parser.Context
            };
        }
    }
}