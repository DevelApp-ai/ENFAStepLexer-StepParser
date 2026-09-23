using System;
using DevelApp.StepLexer;

namespace DevelApp.StepParser
{
    /// <summary>
    /// Pattern-token support for <see cref="StepParserEngine"/>: injection of
    /// the standard metavariable and ellipsis token rules into the currently
    /// loaded grammar and its lexer (see ENFAStepLexer-StepParser issue #65).
    /// </summary>
    public partial class StepParserEngine
    {
        /// <summary>
        /// Injects the standard pattern tokens (metavariable
        /// <c>$[A-Z_][A-Z0-9_]*</c> and ellipsis <c>...</c>) into the
        /// currently loaded grammar and the lexer. A grammar's own rules for
        /// these tokens always win. The operation is idempotent: calling it
        /// more than once has no additional effect.
        /// </summary>
        /// <exception cref="InvalidOperationException">
        /// Thrown when no grammar has been loaded.
        /// </exception>
        public void EnablePatternTokens()
        {
            if (CurrentGrammar is null)
            {
                throw new InvalidOperationException(
                    "A grammar must be loaded before pattern tokens can be enabled. " +
                    "Call LoadGrammarFromContent, LoadGrammarDefinition or LoadGrammarWithOverlay first.");
            }

            var grammar = CurrentGrammar;
            foreach (var rule in PatternTokens.CreatePatternTokenRules())
            {
                if (!grammar.TokenRules.Exists(r => r.Name == rule.Name))
                {
                    grammar.TokenRules.Add(rule);
                    _lexer.AddRule(rule);
                }
            }
        }
    }
}
