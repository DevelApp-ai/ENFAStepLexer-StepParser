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
        /// Extract variable at location (RefakTS-style surgical operation)
        /// </summary>
        public RefactoringResult ExtractVariable(ICodeLocation location, string variableName)
        {
            if (!_refactoringOps.ContainsKey("extract-variable"))
                return new RefactoringResult { Success = false, Message = "Extract variable operation not available. Load a grammar first." };

            var operation = _refactoringOps["extract-variable"];
            if (operation.Execute == null)
                return new RefactoringResult { Success = false, Message = "Extract variable operation not available" };

            // Find the node at the given location for context
            if (!TryFindNodeAtLocation(location, out var targetNode))
                return new RefactoringResult { Success = false, Message = "No parse node found at location" };

            return operation.Execute(location, _parser.Context);
        }

        /// <summary>
        /// Inline variable at location
        /// </summary>
        public RefactoringResult InlineVariable(ICodeLocation location)
        {
            if (!_refactoringOps.ContainsKey("inline-variable"))
                return new RefactoringResult { Success = false, Message = "Inline variable operation not available. Load a grammar first." };

            var operation = _refactoringOps["inline-variable"];
            if (operation.Execute == null)
                return new RefactoringResult { Success = false, Message = "Inline variable operation not available" };

            if (!TryFindNodeAtLocation(location, out var targetNode))
                return new RefactoringResult { Success = false, Message = "No parse node found at location" };

            return operation.Execute(location, _parser.Context);
        }

        /// <summary>
        /// Rename symbol at location
        /// </summary>
        public RefactoringResult Rename(ICodeLocation location, string newName)
        {
            if (!_refactoringOps.ContainsKey("rename"))
                return new RefactoringResult { Success = false, Message = "Rename operation not available. Load a grammar first." };

            var operation = _refactoringOps["rename"];
            if (operation.Execute == null)
                return new RefactoringResult { Success = false, Message = "Rename operation not available" };

            if (!TryFindNodeAtLocation(location, out var targetNode))
                return new RefactoringResult { Success = false, Message = "No parse node found at location" };

            // Set the new name in context for the operation
            _parser.Context.Variables["newName"] = newName;

            return operation.Execute(location, _parser.Context);
        }

        /// <summary>
        /// Find all usages of symbol at location
        /// </summary>
        public List<ICodeLocation> FindUsages(ICodeLocation location, string scope = "")
        {
            if (!TryFindNodeAtLocation(location, out var targetNode))
                return new List<ICodeLocation>();

            // Get the symbol name from node properties
            var symbolName = "";
            if (targetNode.TryGetProperty("TokenValue", out var tokenValue))
            {
                symbolName = tokenValue.AsString();
            }

            var references = _parser.Context.SymbolTable.FindAllReferences(symbolName);

            if (!string.IsNullOrEmpty(scope))
            {
                return references.Where(r => r.Scope == scope).Select(r => r.Location).ToList();
            }

            return references.Select(r => r.Location).ToList();
        }

        /// <summary>
        /// Get applicable refactoring operations for location
        /// </summary>
        public List<RefactoringOperation> GetApplicableRefactorings(ICodeLocation location)
        {
            if (!TryFindNodeAtLocation(location, out var targetNode))
                return new List<RefactoringOperation>();

            var applicable = new List<RefactoringOperation>();
            var currentContext = _parser.Context.ContextStack.Current() ?? "";

            foreach (var operation in _refactoringOps.Values)
            {
                if (operation.ApplicableContexts.Length == 0 || 
                    operation.ApplicableContexts.Contains(currentContext))
                {
                    if (operation.Preconditions?.Invoke(_parser.Context) ?? true)
                    {
                        applicable.Add(operation);
                    }
                }
            }

            return applicable;
        }

        /// <summary>
        /// Register default refactoring operations (RefakTS-style)
        /// </summary>
        private void RegisterDefaultRefactoringOperations()
        {
            // Extract Variable
            _refactoringOps["extract-variable"] = new RefactoringOperation
            {
                Name = "extract-variable",
                Description = "Extract expression into a variable",
                ApplicableContexts = new[] { "function", "method", "block" },
                Preconditions = context => context.CurrentToken?.Type == "IDENTIFIER" || 
                                         context.CurrentToken?.Type == "expression",
                Execute = (location, context) =>
                {
                    var variableName = context.Variables.ContainsKey("variableName") 
                        ? context.Variables["variableName"].ToString() 
                        : "temp";
                    
                    return new RefactoringResult
                    {
                        Success = true,
                        Message = $"Extracted variable '{variableName}'",
                        Changes = new List<CodeChange>
                        {
                            new CodeChange
                            {
                                Location = location,
                                OriginalText = "expression", // Would need to extract from source
                                NewText = variableName ?? "temp",
                                ChangeType = "replace"
                            }
                        }
                    };
                }
            };

            // Inline Variable
            _refactoringOps["inline-variable"] = new RefactoringOperation
            {
                Name = "inline-variable",
                Description = "Inline variable usage with its value",
                ApplicableContexts = new[] { "function", "method", "block" },
                Execute = (location, context) =>
                {
                    // Would need to get symbol name from location - simplified for now
                    var symbolName = "variable"; // Placeholder
                    var symbolInfo = context.SymbolTable.Lookup(symbolName, 
                        context.ContextStack.Current() ?? "");
                    
                    if (symbolInfo?.CanInline == true && !string.IsNullOrEmpty(symbolInfo.Value))
                    {
                        return new RefactoringResult
                        {
                            Success = true,
                            Message = $"Inlined variable '{symbolName}'",
                            Changes = new List<CodeChange>
                            {
                                new CodeChange
                                {
                                    Location = location,
                                    OriginalText = symbolName,
                                    NewText = symbolInfo.Value,
                                    ChangeType = "replace"
                                }
                            }
                        };
                    }
                    
                    return new RefactoringResult 
                    { 
                        Success = false, 
                        Message = "Variable cannot be inlined" 
                    };
                }
            };

            // Rename
            _refactoringOps["rename"] = new RefactoringOperation
            {
                Name = "rename",
                Description = "Rename symbol and all its references",
                Execute = (location, context) =>
                {
                    var newName = context.Variables.ContainsKey("newName") 
                        ? context.Variables["newName"].ToString() 
                        : "renamed";
                    
                    // Would need to get symbol name from location - simplified for now
                    var symbolName = "symbol"; // Placeholder
                    var references = context.SymbolTable.FindAllReferences(symbolName);
                    var changes = references.Select(r => new CodeChange
                    {
                        Location = r.Location,
                        OriginalText = symbolName,
                        NewText = newName ?? "renamed",
                        ChangeType = "replace"
                    }).ToList();

                    return new RefactoringResult
                    {
                        Success = true,
                        Message = $"Renamed '{symbolName}' to '{newName}' ({references.Count()} references)",
                        Changes = changes
                    };
                }
            };
        }

        /// <summary>
        /// Register custom refactoring operation
        /// </summary>
        public void RegisterRefactoringOperation(RefactoringOperation operation)
        {
            _refactoringOps[operation.Name] = operation;
        }
    }
}