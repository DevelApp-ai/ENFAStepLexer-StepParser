using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;

namespace DevelApp.StepParser.Tests
{
    /// <summary>
    /// Loads grammar definitions for tests, benchmarks, and demos from the
    /// consolidated grammar repository (Minotaur-Grammars) instead of inline
    /// C# string literals.
    ///
    /// Resolution order (per relative path, results cached):
    ///  1. MINOTAUR_GRAMMARS_DIR environment variable - local checkout of
    ///     https://github.com/DevelApp-ai/Minotaur-Grammars (offline/dev mode).
    ///  2. HTTP fetch from MINOTAUR_GRAMMARS_RAW_BASE (default: raw GitHub,
    ///     main branch of DevelApp-ai/Minotaur-Grammars).
    ///
    /// Note: the grammar files must exist on the main branch of
    /// Minotaur-Grammars (see test-grammars/ there) for network mode to work.
    /// </summary>
    public static class TestGrammars
    {
        public const string DefaultRawBaseUrl =
            "https://raw.githubusercontent.com/DevelApp-ai/Minotaur-Grammars/main/";

        private static readonly Dictionary<string, string> Cache = new();
        private static readonly object Gate = new();
        private static HttpClient? _httpClient;

        /// <summary>
        /// Gets the content of a grammar file by its repository-relative path,
        /// e.g. "test-grammars/step-parser-tests/StepParserTests/SimpleExpr.grammar".
        /// </summary>
        public static string Get(string relativePath)
        {
            if (string.IsNullOrWhiteSpace(relativePath))
                throw new ArgumentException("Relative path must not be empty.", nameof(relativePath));

            lock (Gate)
            {
                if (Cache.TryGetValue(relativePath, out var cached))
                    return cached;
            }

            var content = Load(relativePath);

            lock (Gate)
            {
                Cache[relativePath] = content;
            }

            return content;
        }

        private static string Load(string relativePath)
        {
            var localDir = Environment.GetEnvironmentVariable("MINOTAUR_GRAMMARS_DIR");
            if (!string.IsNullOrWhiteSpace(localDir))
            {
                var localPath = Path.Combine(localDir, relativePath.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(localPath))
                    return File.ReadAllText(localPath);

                throw new InvalidOperationException(
                    $"MINOTAUR_GRAMMARS_DIR is set but grammar file was not found: {localPath}");
            }

            var baseUrl = Environment.GetEnvironmentVariable("MINOTAUR_GRAMMARS_RAW_BASE")
                ?? DefaultRawBaseUrl;
            if (!baseUrl.EndsWith("/"))
                baseUrl += "/";

            try
            {
                _httpClient ??= new HttpClient();
                var content = _httpClient.GetStringAsync(baseUrl + relativePath).GetAwaiter().GetResult();
                if (string.IsNullOrWhiteSpace(content))
                    throw new InvalidOperationException($"Grammar '{relativePath}' fetched but empty.");
                return content;
            }
            catch (InvalidOperationException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"Could not load grammar '{relativePath}' from {baseUrl}. " +
                    "Tests consume grammars from the Minotaur-Grammars repository. " +
                    "Set MINOTAUR_GRAMMARS_DIR to a local checkout of " +
                    "https://github.com/DevelApp-ai/Minotaur-Grammars to run offline.", ex);
            }
        }
    }
}
