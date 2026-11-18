namespace DevelApp.StepParser;

/// <summary>
/// Scans a <c>/regex/</c> literal for its closing slash, honoring backslash
/// escapes and <c>[...]</c> character classes.
/// </summary>
/// <remarks>
/// Issue #143: previously the loaders treated the first inner unescaped
/// <c>/</c> as the end of the regex body even when the slash was escaped
/// (<c>\/</c>) or inside a character class (<c>[/]</c>). The rest of the
/// pattern was silently dropped and the truncated rule never matched, with
/// no load-time error.
/// </remarks>
internal static class RegexSymbolScanner
{
    /// <summary>
    /// Given <paramref name="text"/> and the index of the opening
    /// <c>/</c>, returns the index of the matching closing <c>/</c>, or -1
    /// when the regex is unterminated. A backslash skips the next character;
    /// inside a <c>[...]</c> class a <c>/</c> does not terminate the literal
    /// (a <c>]</c> immediately after <c>[</c> or <c>[^</c> is a literal
    /// class member, not the class end).
    /// </summary>
    public static int FindClosingSlash(string text, int openIndex)
    {
        var inClass = false;
        var classBodyStart = 0;
        for (var i = openIndex + 1; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\\')
            {
                i++; // skip the escaped character
                continue;
            }
            if (inClass)
            {
                if (c == ']' && i > classBodyStart)
                {
                    inClass = false;
                }
                continue;
            }
            if (c == '[')
            {
                inClass = true;
                classBodyStart = i + 1;
                if (classBodyStart < text.Length && text[classBodyStart] == '^')
                {
                    classBodyStart++;
                }
                continue;
            }
            if (c == '/')
            {
                return i;
            }
        }
        return -1;
    }
}
