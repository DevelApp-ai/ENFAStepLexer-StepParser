using System;
using System.Collections.Generic;
using System.Linq;
using DevelApp.StepParser;
using DevelApp.StepLexer;
using Xunit;

namespace DevelApp.StepParser.Tests
{
    /// <summary>
    /// Issue #101, Selective Path Invalidation: parse-level incremental
    /// behavior on <see cref="RealTimeParserSession"/>. Every warm edit
    /// must resume from the nearest cached parser snapshot below the
    /// impact boundary and produce a parse equivalent to a cold full
    /// parse of the same edited text, while stepping strictly fewer
    /// parser steps than the cold parse.
    /// </summary>
    public class Issue101IncrementalParseTests
    {
        private static readonly string ExprGrammar =
            TestGrammars.Get("test-grammars/step-parser-tests/StepParserTests/SimpleExpr.grammar");

        private static RealTimeParserSession CreateParsingSession(string source)
        {
            var session = new RealTimeParserSession(ExprGrammar);
            session.EnableIncrementalParsing = true;
            session.Initialize(source);
            return session;
        }

        private static string BuildExprDoc(int terms)
        {
            var parts = new List<string>(terms);
            for (int i = 0; i < terms; i++)
            {
                parts.Add((i % 2 == 0 ? "a" : "12") + " + ");
            }

            return string.Join(string.Empty, parts) + "z";
        }

        private static void AssertSameParse(RealTimeParserSession expected, RealTimeParserSession actual)
        {
            Assert.NotNull(expected.LastParse);
            Assert.NotNull(actual.LastParse);
            Assert.Equal(expected.LastParse!.Success, actual.LastParse!.Success);
            Assert.Equal(expected.LastParse.Errors.Count, actual.LastParse.Errors.Count);

            var expectedSpans = expected.LastParse.ResolvedTokens
                .Select(t => (t.StartPosition, t.Length, t.Type, t.Value))
                .OrderBy(s => s.StartPosition)
                .ToList();
            var actualSpans = actual.LastParse.ResolvedTokens
                .Select(t => (t.StartPosition, t.Length, t.Type, t.Value))
                .OrderBy(s => s.StartPosition)
                .ToList();
            Assert.Equal(expectedSpans, actualSpans);
        }

        [Fact]
        public void Initialize_ColdParse_PopulatesCacheAndSucceeds()
        {
            var session = CreateParsingSession("a + 12 + b + 3");
            Assert.True(session.EnableIncrementalParsing);
            Assert.NotNull(session.LastParse);
            Assert.True(session.LastParse!.Success);
            Assert.Empty(session.LastParse.Errors);
            Assert.False(session.LastParseUsedCache);
            Assert.Equal(0, session.LastParseResumeBoundary);
            Assert.True(session.LastParseStepCount > 0);
            Assert.True(session.ParseCacheBoundaryCount > 0);
        }

        [Fact]
        public void ApplyEdit_WordCompletion_ResumesFromCache_AndMatchesColdParse()
        {
            var doc = BuildExprDoc(40);
            var session = CreateParsingSession(doc);
            var coldSteps = session.LastParseStepCount;

            // Word completion inside a mid-document identifier.
            var at = doc.IndexOf("a +", StringComparison.Ordinal) + ("a + ".Length * 15);
            var markerAt = doc.IndexOf('a', at);
            Assert.True(markerAt > 0);
            session.ApplyEdit(markerAt, 0, "bc");

            Assert.True(session.LastParseUsedCache, "the edit far from the start should resume from the cache");
            Assert.True(session.LastParseResumeBoundary > 0, "a cached boundary below the edit must be restored");
            Assert.True(
                session.LastParseStepCount < coldSteps,
                $"resumed parse stepped {session.LastParseStepCount} steps, cold parse stepped {coldSteps}");

            var control = CreateParsingSession(session.Text);
            Assert.False(control.LastParseUsedCache);
            AssertSameParse(control, session);
        }

        [Fact]
        public void ApplyEdit_SingleCharInsertIntoNumber_MatchesColdParse()
        {
            var doc = BuildExprDoc(20);
            var session = CreateParsingSession(doc);

            var numberAt = doc.IndexOf("12", StringComparison.Ordinal);
            Assert.True(numberAt > 0);
            session.ApplyEdit(numberAt + 1, 0, "3"); // "12" -> "132" at the same length class

            var control = CreateParsingSession(session.Text);
            AssertSameParse(control, session);
            Assert.True(session.LastParse!.Success);
        }

        [Fact]
        public void ApplyEdit_BlockInsert_MatchesColdParse_WithManyInvalidatedBoundaries()
        {
            var doc = BuildExprDoc(30);
            var session = CreateParsingSession(doc);

            var at = doc.IndexOf("a + ", 20, StringComparison.Ordinal);
            Assert.True(at > 0);
            session.ApplyEdit(at, 0, "12 + 44 + ");

            Assert.True(session.LastParseUsedCache);
            var control = CreateParsingSession(session.Text);
            AssertSameParse(control, session);
        }

        [Fact]
        public void ApplyEdit_Delete_MatchesColdParse()
        {
            var doc = BuildExprDoc(25);
            var session = CreateParsingSession(doc);

            var at = doc.IndexOf("12 + ", 5, StringComparison.Ordinal);
            Assert.True(at > 0);
            session.ApplyEdit(at, "12 + ".Length, "");

            Assert.True(session.LastParseUsedCache);
            var control = CreateParsingSession(session.Text);
            AssertSameParse(control, session);
        }

        [Fact]
        public void ApplyEdit_AtFirstToken_ColdStarts_WhenEntireCacheIsInvalidated()
        {
            var doc = BuildExprDoc(30);
            var session = CreateParsingSession(doc);
            var coldBoundaries = session.ParseCacheBoundaryCount;
            Assert.True(coldBoundaries > 0);

            // Edit at offset 0 affects the very first token: every cached
            // boundary (>= 0) is invalidated and the re-parse must cold
            // start rather than resume from stale state.
            session.ApplyEdit(0, 0, "q");
            Assert.False(session.LastParseUsedCache);
            Assert.Equal(0, session.LastParseResumeBoundary);

            var control = CreateParsingSession(session.Text);
            AssertSameParse(control, session);
        }

        [Fact]
        public void SequentialEdits_EachMatchesColdParse_AndCacheHitsReturnAfterColdStart()
        {
            var doc = BuildExprDoc(30);
            var session = CreateParsingSession(doc);

            // First: a cold-starting edit at the very beginning.
            session.ApplyEdit(0, 0, "q + ");
            Assert.False(session.LastParseUsedCache);

            // Then warm edits further down: cache repopulated by the cold
            // parse and hit again.
            for (int i = 1; i <= 5; i++)
            {
                var at = session.Text.IndexOf("12 + ", 20, StringComparison.Ordinal);
                Assert.True(at > 0);
                session.ApplyEdit(at, 0, "x");

                var control = CreateParsingSession(session.Text);
                AssertSameParse(control, session);
                Assert.True(session.LastParseUsedCache || i == 0, $"edit {i} should have resumed from the cache");
            }
        }
    }
}
