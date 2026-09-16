using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace DevelApp.StepLexer
{
    public partial class StepLexer
    {

        /// <summary>
        /// Check if token can be split for ambiguity resolution
        /// </summary>
        private bool CanSplitToken(StepToken token, TokenRule rule)
        {
            // For demonstration - tokens like "\x{41}\xFF" can be split
            return token.Value.Contains("\\x{") && token.Value.Contains("}");
        }

        /// <summary>
        /// Generate split tokens for ambiguity resolution
        /// </summary>
        private List<StepToken> GenerateSplitTokens(StepToken originalToken, TokenRule rule, ICodeLocation location)
        {
            var splits = new List<StepToken>();
            
            // Example implementation for hex escape splitting
            if (originalToken.Value.Contains("\\x{") && originalToken.Value.Contains("}"))
            {
                // Split into individual hex escapes
                var parts = originalToken.Value.Split(new[] { "\\x{" }, StringSplitOptions.RemoveEmptyEntries);
                foreach (var part in parts)
                {
                    if (part.Contains("}"))
                    {
                        var closingBraceIndex = part.IndexOf("}");
                        if (closingBraceIndex > 0) // Make sure it's found and not at position 0
                        {
                            var hex = part.Substring(0, closingBraceIndex);
                            splits.Add(new StepToken("HEX_ESCAPE", $"\\x{{{hex}}}", location, originalToken.Context));
                        }
                    }
                }
            }
            
            return splits;
        }

        /// <summary>
        /// Merge identical paths to optimize performance
        /// </summary>
        private List<LexerPath> MergePaths(List<LexerPath> paths)
        {
            var merged = new Dictionary<string, LexerPath>();
            
            foreach (var path in paths)
            {
                var key = $"{path.Position}:{path.CurrentContext}:{string.Join(",", path.Tokens.Select(t => t.Type))}";
                if (!merged.ContainsKey(key))
                {
                    merged[key] = path;
                }
            }
            
            return merged.Values.ToList();
        }
    }
}