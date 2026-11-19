using System.Text;

namespace DevelApp.IncrementalBench
{
    /// <summary>
    /// Real-C# benchmark documents for the lexer-level arm of the issue
    /// #101 measurements (docs/measurements/incremental-parsing-ides).
    /// The corpus feasibility sweep showed the engine parses real C#
    /// only at the lexer level (the CSharp10 parse layer is
    /// combinatorially infeasible at file scale, issue #157), so this
    /// corpus feeds the real-file lexing measurements: full lex versus
    /// windowed suffix re-lex around an edit.
    /// </summary>
    public static class RealCSharp
    {
        /// <summary>
        /// The C# 10 grammar (copy of Minotaur-Grammars
        /// programming-languages/csharp10/CSharp10.grammar) used for the
        /// real-C# lexing arm. Loaded with
        /// <see cref="DevelApp.StepLexer.LexingMode.GreedyLongestMatch"/>.
        /// </summary>
        public const string CSharp10GrammarPath = "Corpus/CSharp10.grammar";

        /// <summary>
        /// The solution file that marks the repository root. The runner
        /// resolves it by walking up from the output directory, so the
        /// benchmark reproduces on any clone without configuration.
        /// </summary>
        private const string SolutionFileName = "ENFAStepLexer-StepParser.sln";

        /// <summary>
        /// The fixed, ordered list of source directories the document
        /// generator concatenates from. The list is committed so the
        /// corpus is a deterministic function of the repository
        /// checkout.
        /// </summary>
        private static readonly string[] SourceDirectories =
        {
            "src/DevelApp.StepParser",
            "src/DevelApp.StepLexer",
            "src/DevelApp.StepParser.Tests",
            "src/DevelApp.StepLexer.Tests",
        };

        /// <summary>
        /// Build a deterministic real-C# document of exactly
        /// <paramref name="lines"/> lines from the repository's own
        /// source files. Files are taken whole, round-robined across the
        /// directories of <see cref="SourceDirectories"/> (path-sorted
        /// inside each directory) so the token density stays uniform as
        /// the document grows; the document is cut at exactly
        /// <paramref name="lines"/> lines. Line j of Doc(N) is always
        /// the same source line, so Doc(N) is a prefix of Doc(N+k): the
        /// same checkout always produces the same bytes.
        /// </summary>
        /// <param name="lines">Target line count.</param>
        /// <returns>The document text (LF line endings).</returns>
        public static string Doc(int lines)
        {
            var root = RepoRoot();

            // Path-sorted file list per source directory.
            var perDirectory = new List<List<string>>();
            foreach (var dir in SourceDirectories)
            {
                var full = Path.Combine(root, dir.Replace('/', Path.DirectorySeparatorChar));
                if (!Directory.Exists(full))
                {
                    continue;
                }

                var files = Directory
                    .EnumerateFiles(full, "*.cs", SearchOption.AllDirectories)
                    .ToList();
                files.Sort(StringComparer.Ordinal);
                if (files.Count > 0)
                {
                    perDirectory.Add(files);
                }
            }

            // Round-robin the directories at FILE granularity: whole
            // files keep multi-line constructs untorn, while the
            // round-robin keeps comment-dense and code-dense files
            // uniformly mixed at every document size.
            var orderedFiles = new List<string>();
            for (var index = 0; ; index++)
            {
                var appended = false;
                foreach (var files in perDirectory)
                {
                    if (index < files.Count)
                    {
                        orderedFiles.Add(files[index]);
                        appended = true;
                    }
                }

                if (!appended)
                {
                    break;
                }
            }

            var sb = new StringBuilder(lines * 32);
            var emitted = 0;
            foreach (var path in orderedFiles)
            {
                foreach (var line in File.ReadLines(path))
                {
                    if (emitted >= lines)
                    {
                        return sb.ToString();
                    }

                    sb.Append(line).Append('\n');
                    emitted++;
                }
            }

            throw new InvalidOperationException(
                "The fixed source corpus has only " + emitted
                + " lines; Doc(" + lines + ") cannot be built.");
        }

        /// <summary>
        /// Resolve the repository root by walking up from the output
        /// directory until the solution file is found.
        /// </summary>
        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                if (File.Exists(Path.Combine(dir.FullName, SolutionFileName)))
                {
                    return dir.FullName;
                }

                dir = dir.Parent;
            }

            throw new InvalidOperationException(
                "Could not locate " + SolutionFileName + " above " + AppContext.BaseDirectory
                + "; run the benchmark from the repository build output.");
        }
    }
}
