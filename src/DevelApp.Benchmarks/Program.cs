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
            BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
        }
    }
}
