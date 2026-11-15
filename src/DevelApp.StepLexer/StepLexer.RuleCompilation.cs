using System;
using System.Collections.Generic;
using System.Text;

namespace DevelApp.StepLexer
{
    /// <summary>
    /// Compile-time rule resolution for the step lexer (issue #87).
    /// Every <see cref="TokenRule"/> is resolved once — when it is added or
    /// when its pattern changes — into a <see cref="CompiledTokenRule"/>
    /// carrying a matcher kind plus precomputed data (literal UTF-8 bytes,
    /// quantifier bounds, parsed class-sequence atoms). The per-step
    /// matching loop then performs no pattern preprocessing, no
    /// re-encoding of literals and no UTF-8→UTF-16 decoding: it dispatches
    /// on the compiled kind and compares byte spans.
    /// </summary>
    public partial class StepLexer
    {
        /// <summary>
        /// The resolved matcher form of a token rule pattern (issue #87).
        /// </summary>
        private enum RulePatternKind
        {
            /// <summary>The pattern never matches (unsupported or empty).</summary>
            NeverMatches,

            /// <summary>The core constant <c>[0-9]+</c>.</summary>
            DigitRun,

            /// <summary>The core constant <c>[a-zA-Z][a-zA-Z0-9]*</c>.</summary>
            IdentifierRun,

            /// <summary>The core constant <c>[ \t\r\n]+</c>.</summary>
            WhitespaceRun,

            /// <summary>The core constant <c>\$[A-Z_][A-Z0-9_]*</c>.</summary>
            MetavariableRun,

            /// <summary>A literal (optionally case-insensitive).</summary>
            Literal,

            /// <summary>A quoted literal sequence (<c>\Q...\E</c>) with a quantifier.</summary>
            QuotedLiteral,

            /// <summary>A Unicode property escape (<c>\p{...}</c>/<c>\P{...}</c>) with a quantifier.</summary>
            UnicodeProperty,

            /// <summary>A general character-class sequence of quantified atoms.</summary>
            ClassSequence,
        }

        /// <summary>
        /// The compiled form of one token rule: the matcher kind plus the
        /// precomputed data for that kind. All fields are computed once at
        /// compile time so the match loop only reads them.
        /// </summary>
        private sealed class CompiledTokenRule
        {
            /// <summary>The full rule pattern this rule was compiled from
            /// (staleness check key, compared ordinally).</summary>
            public required string Pattern { get; init; }

            /// <summary>The resolved matcher kind.</summary>
            public required RulePatternKind Kind { get; init; }

            /// <summary>Literal text (Literal / QuotedLiteral kinds).</summary>
            public string? LiteralText { get; init; }

            /// <summary>UTF-8 encoding of <see cref="LiteralText"/> (Literal / QuotedLiteral kinds).</summary>
            public byte[]? LiteralBytes { get; init; }

            /// <summary>Whether matching is case-insensitive.</summary>
            public bool IgnoreCase { get; init; }

            /// <summary>Minimum repetition count (QuotedLiteral / UnicodeProperty kinds).</summary>
            public int MinCount { get; init; }

            /// <summary>Maximum repetition count (QuotedLiteral / UnicodeProperty kinds).</summary>
            public int MaxCount { get; init; }

            /// <summary>Canonical or loose Unicode property name (UnicodeProperty kind).</summary>
            public string? PropertyName { get; init; }

            /// <summary>True for <c>\P{...}</c> (inverted) matching.</summary>
            public bool PropertyNegated { get; init; }

            /// <summary>Parsed quantified atoms (ClassSequence kind).</summary>
            public List<ClassSequenceAtom>? Atoms { get; init; }
        }

        /// <summary>
        /// Compiled rules by token rule. Entries are invalidated and
        /// recompiled when a rule's pattern changes after being added.
        /// </summary>
        private readonly Dictionary<TokenRule, CompiledTokenRule> _compiledRules = new();

        /// <summary>
        /// Get the compiled form of a rule, recompiling when the rule's
        /// pattern changed since the last compile (rules are compiled
        /// eagerly at add time; a later pattern mutation must not keep a
        /// stale compiled matcher).
        /// </summary>
        /// <param name="rule">The token rule.</param>
        /// <returns>The up-to-date compiled matcher.</returns>
        private CompiledTokenRule GetCompiledRule(TokenRule rule)
        {
            if (_compiledRules.TryGetValue(rule, out var compiled)
                && string.Equals(compiled.Pattern, rule.Pattern, StringComparison.Ordinal))
            {
                return compiled;
            }

            compiled = CompileRule(rule);
            _compiledRules[rule] = compiled;
            return compiled;
        }

        /// <summary>
        /// Compile a token rule into its matcher form. The dispatch mirrors
        /// the original per-attempt matching exactly: <c>/.../</c> bodies
        /// go through the regex body compiler, quoted literals strip their
        /// quotes, and anything else is a literal.
        /// </summary>
        /// <param name="rule">The token rule to compile.</param>
        /// <returns>The compiled matcher (NeverMatches for unsupported patterns).</returns>
        private CompiledTokenRule CompileRule(TokenRule rule)
        {
            var pattern = rule.Pattern;

            if (pattern.StartsWith("/") && pattern.EndsWith("/") && pattern.Length > 1)
            {
                var body = pattern.Length > 2 ? pattern[1..^1] : "";
                return CompileRegexBody(body, pattern);
            }

            if (pattern.StartsWith("\"") && pattern.EndsWith("\"") && pattern.Length > 1)
            {
                var literal = pattern.Length > 2 ? pattern[1..^1] : "";
                return CompileLiteral(literal);
            }

            if (pattern.StartsWith("'") && pattern.EndsWith("'") && pattern.Length >= 2)
            {
                // Single-quoted literal string match (issue #80).
                var literal = pattern.Length > 2 ? pattern[1..^1] : "";
                return CompileLiteral(literal);
            }

            // Default to literal match.
            return CompileLiteral(pattern);
        }

        /// <summary>
        /// Compile the body of a <c>/.../</c> regex pattern. Reproduces the
        /// original dispatch: preprocessing (inline modifiers, comments,
        /// atomic groups, possessive quantifiers), the core constants, and
        /// the supported constructs. Anything unsupported compiles to
        /// NeverMatches — exactly what the original matcher returned as a
        /// no-match for every attempt.
        /// </summary>
        /// <param name="body">The pattern body (without the surrounding slashes).</param>
        /// <param name="fullPattern">The full rule pattern (kept on the
        /// compiled rule as the staleness key).</param>
        /// <returns>The compiled matcher.</returns>
        private CompiledTokenRule CompileRegexBody(string body, string fullPattern)
        {
            // Strip (?#...) comments and inline modifier groups; honor (?i)
            // for case-insensitive matching and (?x) for extended mode.
            var (core, ignoreCase) = PreprocessRegexPattern(body);

            switch (core)
            {
                case "[0-9]+":
                    return Make(fullPattern, RulePatternKind.DigitRun);
                case "[a-zA-Z][a-zA-Z0-9]*":
                    return Make(fullPattern, RulePatternKind.IdentifierRun);
                case "[ \\t\\r\\n]+":
                    return Make(fullPattern, RulePatternKind.WhitespaceRun);
                case @"\$[A-Z_][A-Z0-9_]*":
                    return Make(fullPattern, RulePatternKind.MetavariableRun);
                case @"\.\.\.":
                    // The ellipsis token is matched case-sensitively, same
                    // as the original dispatch.
                    return MakeLiteral(fullPattern, "...", ignoreCase: false);
            }

            if (core.Length == 0)
            {
                return Make(fullPattern, RulePatternKind.NeverMatches);
            }

            if (core.StartsWith("\\Q"))
            {
                return CompileQuotedLiteral(core, ignoreCase, fullPattern);
            }

            if (core.StartsWith("\\p{") || core.StartsWith("\\P{"))
            {
                return CompileUnicodeProperty(core, fullPattern);
            }

            // Plain text without regex metacharacters is a literal pattern
            // (e.g. /abc/ matches "abc"); patterns with unsupported
            // metacharacters do not match rather than matching wrongly.
            if (!ContainsRegexMetacharacter(core))
            {
                return MakeLiteral(fullPattern, core, ignoreCase);
            }

            // General character-class sequence (issue #83): patterns made of
            // concatenated atoms with optional quantifiers.
            if (TryParseClassSequence(core, ignoreCase, out var atoms))
            {
                return new CompiledTokenRule
                {
                    Pattern = fullPattern,
                    Kind = RulePatternKind.ClassSequence,
                    Atoms = atoms,
                };
            }

            return Make(fullPattern, RulePatternKind.NeverMatches);
        }

        /// <summary>
        /// Compile a literal pattern; an empty literal never matches
        /// (issue #111: zero-width matches are rejected).
        /// </summary>
        /// <param name="text">The literal text.</param>
        /// <returns>The compiled matcher.</returns>
        private CompiledTokenRule CompileLiteral(string text)
        {
            return MakeLiteral(text, text, ignoreCase: false);
        }

        /// <summary>
        /// Compile a quoted literal sequence pattern (<c>\Q...\E</c>) with
        /// its optional trailing quantifier. An unterminated <c>\Q</c> runs
        /// to the end of the pattern (PCRE2 semantics), an empty literal or
        /// an unsupported quantifier never matches.
        /// </summary>
        /// <param name="core">The preprocessed pattern body starting with <c>\Q</c>.</param>
        /// <param name="ignoreCase">Whether to match case-insensitively.</param>
        /// <param name="fullPattern">The full rule pattern (staleness key).</param>
        /// <returns>The compiled matcher.</returns>
        private CompiledTokenRule CompileQuotedLiteral(string core, bool ignoreCase, string fullPattern)
        {
            int end = core.IndexOf("\\E", 2, StringComparison.Ordinal);

            string literalText;
            string quantifier;
            if (end < 0)
            {
                literalText = core.Substring(2);
                quantifier = string.Empty;
            }
            else
            {
                literalText = core.Substring(2, end - 2);
                quantifier = core.Substring(end + 2);
            }

            if (literalText.Length == 0)
            {
                return Make(fullPattern, RulePatternKind.NeverMatches);
            }

            if (!TryParseQuantifier(quantifier, out int minCount, out int maxCount))
            {
                return Make(fullPattern, RulePatternKind.NeverMatches);
            }

            var literalBytes = Encoding.UTF8.GetBytes(literalText);
            return new CompiledTokenRule
            {
                Pattern = fullPattern,
                Kind = RulePatternKind.QuotedLiteral,
                LiteralText = literalText,
                LiteralBytes = literalBytes,
                IgnoreCase = ignoreCase,
                MinCount = minCount,
                MaxCount = maxCount,
            };
        }

        /// <summary>
        /// Compile a Unicode property escape pattern (<c>\p{Name}</c> or
        /// <c>\P{Name}</c>) with its optional trailing quantifier. Unknown
        /// property names and unsupported trailing syntax never match, so
        /// rule validation surfaces them instead of silently producing
        /// tokens (same convention as the original matcher).
        /// </summary>
        /// <param name="core">The preprocessed pattern body.</param>
        /// <param name="fullPattern">The full rule pattern (staleness key).</param>
        /// <returns>The compiled matcher.</returns>
        private CompiledTokenRule CompileUnicodeProperty(string core, string fullPattern)
        {
            bool negated = core[1] == 'P';

            // Find the closing brace of \p{...} / \P{...}
            int closeIndex = core.IndexOf('}', 3);
            if (closeIndex < 0)
            {
                return Make(fullPattern, RulePatternKind.NeverMatches);
            }

            var propertyName = core.Substring(3, closeIndex - 3);
            if (!UnicodePropertyValidator.IsValidPropertyName(propertyName))
            {
                return Make(fullPattern, RulePatternKind.NeverMatches);
            }

            // Parse the optional quantifier after the closing brace
            var quantifier = core.Substring(closeIndex + 1);
            if (!TryParseQuantifier(quantifier, out int minCount, out int maxCount))
            {
                return Make(fullPattern, RulePatternKind.NeverMatches);
            }

            return new CompiledTokenRule
            {
                Pattern = fullPattern,
                Kind = RulePatternKind.UnicodeProperty,
                PropertyName = propertyName,
                PropertyNegated = negated,
                MinCount = minCount,
                MaxCount = maxCount,
            };
        }

        /// <summary>
        /// Construct a compiled rule with a kind and no extra data.
        /// </summary>
        /// <param name="fullPattern">The full rule pattern (staleness key).</param>
        /// <param name="kind">The matcher kind.</param>
        /// <returns>The compiled matcher.</returns>
        private static CompiledTokenRule Make(string fullPattern, RulePatternKind kind)
        {
            return new CompiledTokenRule
            {
                Pattern = fullPattern,
                Kind = kind,
            };
        }

        /// <summary>
        /// Construct a compiled literal rule; an empty literal never
        /// matches (issue #111: zero-width matches are rejected).
        /// </summary>
        /// <param name="fullPattern">The full rule pattern (staleness key).</param>
        /// <param name="literalText">The literal text.</param>
        /// <param name="ignoreCase">Whether to match case-insensitively.</param>
        /// <returns>The compiled matcher.</returns>
        private static CompiledTokenRule MakeLiteral(string fullPattern, string literalText, bool ignoreCase)
        {
            if (literalText.Length == 0)
            {
                return Make(fullPattern, RulePatternKind.NeverMatches);
            }

            var literalBytes = Encoding.UTF8.GetBytes(literalText);
            return new CompiledTokenRule
            {
                Pattern = fullPattern,
                Kind = RulePatternKind.Literal,
                LiteralText = literalText,
                LiteralBytes = literalBytes,
                IgnoreCase = ignoreCase,
            };
        }
    }
}
