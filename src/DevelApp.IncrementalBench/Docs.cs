using System.Text;

namespace DevelApp.IncrementalBench
{
    /// <summary>
    /// Deterministic benchmark document corpus for the issue #101
    /// incremental-parsing measurements (docs/measurements/
    /// incremental-parsing-ides). All generators are pure functions of
    /// their arguments: the same <c>lines</c> always produce the same
    /// bytes, so every arm, run and machine edits the very same text.
    /// </summary>
    public static class Docs
    {
        /// <summary>
        /// The StepIdl benchmark grammar: a purpose-built, line-oriented
        /// assignment language whose documents parse to completion at file
        /// scale (5000 lines cold-parse in seconds, see the measured
        /// tables). The corpus feasibility sweep found no real-world
        /// Minotaur-Grammars grammar that parses real files of 2000+ lines:
        /// JSON aborts the host, INI/Makefile/YAML token rules collapse
        /// file-scale input to a handful of greedy multi-line tokens, and
        /// the natural-language grammars lex zero tokens on repeated
        /// example text. StepIdl makes the parse-level incremental
        /// behavior measurable at real file sizes; real C# files are
        /// measured at the lexer level (the level at which the engine
        /// supports real C#), see <see cref="RealCSharp"/>.
        /// </summary>
        public const string StepIdlGrammarPath = "Corpus/StepIdl.grammar";

        /// <summary>
        /// One deterministic <c>vi = ai + 12 + bi</c> statement per line.
        /// </summary>
        public static string StepIdl(int lines)
        {
            var sb = new StringBuilder(lines * 24);
            for (int i = 0; i < lines; i++)
            {
                sb.Append('v').Append(i).Append(" = a").Append(i % 7)
                  .Append(" + 12 + b").Append(i % 13).Append('\n');
            }
            return sb.ToString();
        }
    }
}
