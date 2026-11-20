using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace DevelApp.StepParser
{
    public partial class GrammarLoader
    {
        /// <summary>
        /// Matches a nonterminal reference <c>&lt;name&gt;</c> or <c>&lt;name(context)&gt;</c>.
        /// </summary>
        private static readonly Regex NonterminalRefRegex =
            new(@"<([A-Za-z_0-9\-]+)(?:\([^)]*\))?>", RegexOptions.Compiled);

        /// <summary>
        /// Find nonterminals referenced from production right-hand sides that are
        /// never defined by any production rule or token rule of the (fully
        /// inherited) grammar (issue #157, part 2: grammars with undefined
        /// nonterminals used to load silently and then wander into parse blowups).
        /// Quoted literals and /regex/ terminals in the RHS are stripped before
        /// scanning so a literal such as "&lt;foo&gt;" is not misread as a
        /// reference.
        /// </summary>
        /// <param name="lines">The grammar file lines.</param>
        /// <param name="grammar">The fully parsed (and inherited) grammar; its
        /// production and token rule names form the defined set.</param>
        /// <returns>The distinct undefined nonterminal names, sorted.</returns>
        public List<string> FindUndefinedNonterminalReferences(string[] lines, GrammarDefinition grammar)
        {
            var defined = new HashSet<string>(StringComparer.Ordinal);
            foreach (var rule in grammar.ProductionRules)
            {
                defined.Add(rule.Name);
            }
            foreach (var token in grammar.TokenRules)
            {
                defined.Add(token.Name);
            }

            var undefined = new SortedSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < lines.Length; i++)
            {
                var line = lines[i].Trim();
                if (!IsProductionRule(line))
                {
                    continue;
                }

                var fullRule = CollectMultiLineRule(lines, ref i);
                var colonIdx = fullRule.IndexOf("::=", StringComparison.Ordinal);
                if (colonIdx < 0)
                {
                    continue;
                }

                var rhs = StripTerminals(fullRule.Substring(colonIdx + 3));
                foreach (Match m in NonterminalRefRegex.Matches(rhs))
                {
                    var name = m.Groups[1].Value;
                    if (!defined.Contains(name))
                    {
                        undefined.Add(name);
                    }
                }
            }

            return undefined.ToList();
        }

        /// <summary>
        /// Remove quoted literals (<c>"..."</c>, <c>'...'</c>) and /regex/
        /// terminals from a right-hand-side fragment so only real symbol
        /// references remain. Scanning is escape- and character-class-aware,
        /// mirroring the rule-line scanners.
        /// </summary>
        private static string StripTerminals(string rhs)
        {
            var outText = new System.Text.StringBuilder(rhs.Length);
            int i = 0;
            while (i < rhs.Length)
            {
                char c = rhs[i];
                if (c == '"' || c == '\'')
                {
                    // quoted literal: skip to the closing quote (escapes honored)
                    char quote = c;
                    i++;
                    while (i < rhs.Length)
                    {
                        if (rhs[i] == '\\' && i + 1 < rhs.Length) { i += 2; continue; }
                        if (rhs[i] == quote) { i++; break; }
                        i++;
                    }
                    outText.Append(' ');
                    continue;
                }
                if (c == '/')
                {
                    // regex terminal: skip to the closing unescaped slash,
                    // character-class-aware
                    int j = i + 1;
                    bool inClass = false;
                    while (j < rhs.Length)
                    {
                        char d = rhs[j];
                        if (d == '\\' && j + 1 < rhs.Length) { j += 2; continue; }
                        if (inClass)
                        {
                            if (d == ']') inClass = false;
                            j++;
                            continue;
                        }
                        if (d == '[') { inClass = true; j++; continue; }
                        if (d == '/') { j++; break; }
                        j++;
                    }
                    i = j;
                    outText.Append(' ');
                    continue;
                }
                outText.Append(c);
                i++;
            }
            return outText.ToString();
        }
    }
}
