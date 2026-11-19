using BenchmarkDotNet.Running;

namespace DevelApp.Benchmarks
{
    /// <summary>
    /// Entry point for the DevelApp performance benchmarking suite.
    /// </summary>
    /// <remarks>
    /// Benchmarks are run through BenchmarkDotNet's console switcher, so all
    /// standard command line arguments are supported. Common invocations:
    /// <code>
    ///   dotnet run -c Release --project src/DevelApp.Benchmarks -- --list flat
    ///   dotnet run -c Release --project src/DevelApp.Benchmarks -- --filter *
    ///   dotnet run -c Release --project src/DevelApp.Benchmarks -- --filter *LexerBenchmarks* -j short
    ///   dotnet run -c Release --project src/DevelApp.Benchmarks -- --filter *ParserBenchmarks* -j dry
    /// </code>
    /// Results (including a markdown summary) are written to
    /// <c>BenchmarkDotNet.Artifacts/results</c> relative to the working directory.
    /// </remarks>
    public static class Program
    {
        /// <summary>
        /// Main entry point. Delegates to the BenchmarkDotNet console switcher.
        /// </summary>
        /// <param name="args">BenchmarkDotNet command line arguments.</param>
        public static void Main(string[] args)
        {
            // Non-BDN measurement modes for the article-lane issues, run by the
            // article-measurements CI job and uploadable as artifacts:
            //   --measure-issue104 <outDir>  benign-pattern overhead + allocation (issue #104)
            if (args.Length >= 2 && args[0] == "--measure-issue104")
            {
                Issue104Measurement.Run(args[1]);
                return;
            }

            BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
        }
    }
}
