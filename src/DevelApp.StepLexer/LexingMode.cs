namespace DevelApp.StepLexer
{
    /// <summary>
    /// Lexing mode of <see cref="StepLexer"/> (issue #133, Gap 2 of #129).
    /// </summary>
    public enum LexingMode
    {
        /// <summary>
        /// Default. Every rule that matches at a position forks a lexer
        /// path (GLR-style token ambiguity, resolved later by path merging
        /// and parser-side selection). Correct for ambiguous grammars, but
        /// the path count can explode combinatorially on file-scale input.
        /// </summary>
        Exploratory,

        /// <summary>
        /// Deterministic. At each position only the longest match is kept
        /// (ties broken by rule priority, then rule order), so exactly one
        /// lexer path survives every step: a single coherent token path,
        /// linear in the input length. Use for file-scale lexing of
        /// grammars whose token layer is intentionally deterministic
        /// (keyword-over-identifier via priority, longest-match literals).
        /// </summary>
        GreedyLongestMatch,
    }
}
