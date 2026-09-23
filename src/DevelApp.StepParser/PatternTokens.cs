using System.Collections.Generic;
using DevelApp.StepLexer;

namespace DevelApp.StepParser
{
    /// <summary>
    /// Factory and injection helpers for the standard pattern tokens shared
    /// by every grammar: the metavariable token
    /// (<c>$[A-Z_][A-Z0-9_]*</c>, e.g. <c>$DATA</c>) and the ellipsis token
    /// (<c>...</c>). The tokens can be injected into any grammar so that
    /// rule patterns written in terms of metavariables and ellipses can be
    /// tokenized without each grammar defining its own rules
    /// (see ENFAStepLexer-StepParser issue #65).
    /// </summary>
    public static class PatternTokens
    {
        /// <summary>Gets the token rule name of the standard metavariable token (<c>$NAME</c>).</summary>
        public const string MetavariableTokenName = "METAVARIABLE";

        /// <summary>Gets the token rule name of the standard ellipsis token (<c>...</c>).</summary>
        public const string EllipsisTokenName = "ELLIPSIS";

        /// <summary>Gets the lexer pattern of the standard metavariable token.</summary>
        public const string MetavariablePattern = @"/\$[A-Z_][A-Z0-9_]*/";

        /// <summary>Gets the lexer pattern of the standard ellipsis token.</summary>
        public const string EllipsisPattern = @"/\.\.\./";

        /// <summary>
        /// The default priority assigned to the injected pattern tokens. The
        /// value is deliberately high so the tokens win over ordinary
        /// grammar rules that could otherwise greedily consume the same
        /// input (e.g. an identifier rule consuming <c>DATA</c> after a
        /// literal dollar sign).
        /// </summary>
        public const int DefaultPriority = 1000;

        /// <summary>
        /// Creates the token rule for the standard metavariable token
        /// (<c>$[A-Z_][A-Z0-9_]*</c>).
        /// </summary>
        /// <param name="priority">The rule priority; defaults to <see cref="DefaultPriority"/>.</param>
        /// <returns>A new metavariable token rule.</returns>
        public static TokenRule CreateMetavariableRule(int priority = DefaultPriority)
        {
            return new TokenRule(MetavariableTokenName, MetavariablePattern, priority: priority);
        }

        /// <summary>
        /// Creates the token rule for the standard ellipsis token
        /// (<c>...</c>).
        /// </summary>
        /// <param name="priority">The rule priority; defaults to <see cref="DefaultPriority"/>.</param>
        /// <returns>A new ellipsis token rule.</returns>
        public static TokenRule CreateEllipsisRule(int priority = DefaultPriority)
        {
            return new TokenRule(EllipsisTokenName, EllipsisPattern, priority: priority);
        }

        /// <summary>
        /// Adds the standard pattern token rules to a grammar definition. The
        /// operation is idempotent by rule name: rules already present are not
        /// replaced or duplicated, so a grammar's own definitions of these
        /// tokens always win.
        /// </summary>
        /// <param name="grammar">The grammar to inject the pattern tokens into.</param>
        /// <param name="priority">The rule priority; defaults to <see cref="DefaultPriority"/>.</param>
        /// <returns>The same grammar instance, for fluent use.</returns>
        public static GrammarDefinition AddPatternTokens(GrammarDefinition grammar, int priority = DefaultPriority)
        {
            foreach (var rule in CreatePatternTokenRules(priority))
            {
                if (!grammar.TokenRules.Exists(r => r.Name == rule.Name))
                {
                    grammar.TokenRules.Add(rule);
                }
            }

            return grammar;
        }

        /// <summary>
        /// Creates an overlay grammar containing only the standard pattern
        /// token rules, suitable for use with the grammar overlay/composition
        /// API (issue #66).
        /// </summary>
        /// <param name="priority">The rule priority; defaults to <see cref="DefaultPriority"/>.</param>
        /// <returns>A new grammar definition containing the pattern token rules.</returns>
        public static GrammarDefinition CreatePatternTokenOverlay(int priority = DefaultPriority)
        {
            var overlay = new GrammarDefinition
            {
                Name = "PatternTokens"
            };
            AddPatternTokens(overlay, priority);
            return overlay;
        }

        /// <summary>
        /// Creates the standard pattern token rules (metavariable and
        /// ellipsis).
        /// </summary>
        /// <param name="priority">The rule priority; defaults to <see cref="DefaultPriority"/>.</param>
        /// <returns>The metavariable and ellipsis token rules.</returns>
        public static IEnumerable<TokenRule> CreatePatternTokenRules(int priority = DefaultPriority)
        {
            yield return CreateMetavariableRule(priority);
            yield return CreateEllipsisRule(priority);
        }
    }
}
