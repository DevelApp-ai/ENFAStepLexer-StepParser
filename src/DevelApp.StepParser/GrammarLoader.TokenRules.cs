using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using DevelApp.StepLexer;
using CognitiveGraph.Accessors;

namespace DevelApp.StepParser
{
    public partial class GrammarLoader
    {

        /// <summary>
        /// Parse token rules from grammar content
        /// </summary>
        private void ParseTokenRules(string[] lines, GrammarDefinition grammar)
        {
            foreach (var line in lines)
            {
                var trimmed = line.Trim();
                if (IsTokenRule(trimmed))
                {
                    var tokenRule = ParseTokenRule(trimmed);
                    if (tokenRule != null)
                    {
                        grammar.TokenRules.Add(tokenRule);
                    }
                }
            }
        }

        /// <summary>
        /// Check if line defines a token rule.
        /// </summary>
        /// <remarks>
        /// A rule is a token rule only when its right-hand side is a single
        /// terminal pattern: a complete <c>/regex/</c> or a complete quoted
        /// literal (<c>'lit'</c> / <c>"lit"</c>), optionally followed by a
        /// semantic action and/or a trailing semicolon. Anything else —
        /// including mixed productions such as
        /// <c>&lt;branch_pipe&gt; ::= 'branches' ':' &lt;named_pipe&gt;</c> and
        /// alternatives separated by <c>|</c> — is a production rule
        /// (issue #80: the previous "starts with a terminal" check
        /// misclassified such rules and swallowed their literals into one
        /// broken lexer pattern).
        /// </remarks>
        private bool IsTokenRule(string line)
        {
            // Token rules start with < and contain ::=
            if (!line.StartsWith("<") || !line.Contains("::=")) return false;

            var colonIndex = line.IndexOf("::=");
            if (colonIndex == -1) return false;

            var rightSide = StripRuleTail(line.Substring(colonIndex + 3).Trim());

            return IsSingleTerminal(rightSide);
        }

        /// <summary>
        /// Strip a trailing semantic action (<c>=&gt; { ... }</c>) and a
        /// trailing semicolon from a rule right-hand side, leaving only the
        /// terminal/production symbols.
        /// </summary>
        private static string StripRuleTail(string rightSide)
        {
            var actionMatch = Regex.Match(rightSide, @"\s*=>\s*\{.*", RegexOptions.Singleline);
            if (actionMatch.Success)
            {
                rightSide = rightSide.Substring(0, actionMatch.Index).Trim();
            }

            // A trailing semicolon terminates the rule; it is never part of
            // the last symbol because complete quoted literals end with a
            // quote character and complete regexes end with '/'.
            rightSide = rightSide.Trim();
            if (rightSide.EndsWith(";"))
            {
                rightSide = rightSide.Substring(0, rightSide.Length - 1).Trim();
            }

            return rightSide;
        }

        /// <summary>
        /// Check whether a right-hand side consists of exactly one complete
        /// terminal: a <c>/regex/</c> or a quoted literal. Alternatives
        /// separated by a top-level <c>|</c> (outside quotes) are not single
        /// terminals.
        /// </summary>
        private static bool IsSingleTerminal(string rightSide)
        {
            if (string.IsNullOrEmpty(rightSide) || rightSide.Length < 2)
            {
                return false;
            }

            var first = rightSide[0];
            if (first != '/' && first != '\'' && first != '"')
            {
                return false;
            }

            var close = first == '/' ? '/' : first;
            var quote = first != '/';
            for (int i = 1; i < rightSide.Length; i++)
            {
                var c = rightSide[i];
                if (quote && c == close)
                {
                    // The literal is complete: it must extend to the end of
                    // the right-hand side to be a single terminal.
                    return i == rightSide.Length - 1;
                }
                if (!quote && c == close && i > 0)
                {
                    return i == rightSide.Length - 1;
                }
            }

            // Unterminated quote/regex: not a complete terminal.
            return false;
        }

        /// <summary>
        /// Parse individual token rule
        /// </summary>
        private TokenRule? ParseTokenRule(string line)
        {
            try
            {
                // Pattern: <TOKEN_NAME> ::= pattern => { action }
                // Strip the trailing semicolon first so it never becomes part
                // of the captured pattern (issue #80).
                var ruleText = line.Trim();
                if (ruleText.EndsWith(";"))
                {
                    ruleText = ruleText.Substring(0, ruleText.Length - 1).Trim();
                }

                // Fixed regex to properly capture the full pattern
                var match = Regex.Match(ruleText, @"<([^>]+)>\s*::=\s*(.+?)(?:\s*=>\s*\{([^}]*)\})?$");
                if (!match.Success) return null;

                var name = match.Groups[1].Value.Trim();
                var pattern = match.Groups[2].Value.Trim();
                var action = match.Groups[3].Success ? match.Groups[3].Value.Trim() : "";

                // Issue #111: reject zero-width token rules at load. An
                // empty quoted literal ("") or ('') as the whole pattern
                // matches zero-width at every input position without
                // consuming anything: the lexer emits an unbounded stream
                // of zero-length tokens, the stall tracking never fires, and
                // the step cap ends lexing with the input unconsumed while
                // reporting success (vacuous acceptance). The rule is
                // dropped here; the lexer additionally treats zero-length
                // matches as no-matches as defense in depth. Authors who
                // need an empty/optional construct should use a production
                // alternative or EBNF optionality, where empty
                // alternatives are already skipped (epsilon productions).
                if (pattern == "\"\"" || pattern == "''")
                {
                    Console.WriteLine(
                        $"Rejected zero-width token rule: {line}. A token rule must consume at least one character (issue #111).");
                    return null;
                }

                var context = ExtractContext(name);
                var cleanName = RemoveContext(name);

                var rule = new TokenRule(cleanName, pattern, context.context)
                {
                    Priority = context.priority,
                    IsSkippable = action.Contains("skip") || action.Contains("/* skip")
                };

                if (!string.IsNullOrEmpty(action))
                {
                    rule.Action = CreateTokenAction(action);
                }

                return rule;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error parsing token rule: {line}. Error: {ex.Message}");
                return null;
            }
        }
    }
}
