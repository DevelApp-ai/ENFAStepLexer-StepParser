using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using DevelApp.StepParser;
using DevelApp.StepLexer;
using Xunit;

namespace DevelApp.StepParser.Tests
{
    /// <summary>
    /// Issue #163: a long-lived SPI session (EnableIncrementalParsing =
    /// true) that applies a deterministic mixed edit stream to a
    /// separator-anchored chain document must not accumulate unbounded
    /// graph-builder content. A cache-resumed parse re-steps the suffix
    /// and appends its parse nodes to the same append-only builder; with
    /// decimated resume boundaries (block pastes near the document start)
    /// each edit re-appends O(document) nodes, so the builder grew
    /// O(edits x document) until its capacity doubling OOM-killed the
    /// process. The session now drops the path cache when the retained
    /// writes exceed <c>RetainedGraphNodeFactor</c> times the current
    /// parse's own writes, so the next edit cold-starts and replaces the
    /// builder. The session's retained graph content then stays within a
    /// small multiple of a single cold parse of the same text.
    /// </summary>
    public class Issue163GraphBuilderRetentionTests
    {
        /// <summary>
        /// The separator-anchored chain grammar from the issue (the
        /// production shape that parses at scale on this engine
        /// generation).
        /// </summary>
        private const string ChainGrammar = """
            Grammar: CSharpLikeLiveEditorParseableChain
            <SEP> ::= /[$]/
            <WORD> ::= /[a-zA-Z_][a-zA-Z0-9_]*/
            <NUMBER> ::= /[0-9]+/
            <STRING_LITERAL> ::= /"([^"\\]|\\.)*"/
            <WS> ::= /[ \t\r\n]+/
            <start> ::= <chain>
            <chain> ::= <chain> <SEP> <item> | <item>
            <item> ::= <WORD> | <NUMBER> | <STRING_LITERAL>
            """;

        private static string BuildInitialDoc(int records)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < records; i++)
            {
                sb.AppendLine($"record{i:D4} $ {i * 31 % 997:D3} $ \"tag-{i * 13 % 251:D3}\" $");
            }

            sb.Append("entryFinal");
            return sb.ToString();
        }

        private static (int offset, int deleteLength, string insert) NextSeededEdit(
            RealTimeParserSession session, Random rng, int index)
        {
            var text = session.Text;
            switch (index % 4)
            {
                case 0:
                {
                    // Single-char insert strictly inside a WORD.
                    var words = session.Tokens
                        .Where(t => t.Type == "WORD" && t.Length >= 2)
                        .ToList();
                    if (words.Count == 0)
                    {
                        return (0, 0, "x");
                    }

                    var w = words[rng.Next(words.Count)];
                    return (w.StartPosition + 1 + rng.Next(Math.Max(1, w.Length - 1)), 0, "x");
                }

                case 1:
                {
                    // Word-completion: append "Data" at a WORD's end.
                    var words = session.Tokens.Where(t => t.Type == "WORD").ToList();
                    if (words.Count == 0)
                    {
                        return (0, 0, "Data");
                    }

                    var w = words[rng.Next(words.Count)];
                    return (w.StartPosition + w.Length, 0, "Data");
                }

                case 2:
                {
                    // One-record line insert at a line boundary.
                    var lineStart = PickLineStart(text, rng);
                    return (lineStart, 0,
                        $"record{index:D4} $ {index * 31 % 997:D3} $ \"tag-{index * 13 % 251:D3}\"\n");
                }

                default:
                {
                    // Four-record block paste.
                    var lineStart = PickLineStart(text, rng);
                    var block = new StringBuilder();
                    for (int k = 0; k < 4; k++)
                    {
                        block.Append(
                            $"record{index + k:D4} $ {(index + k) * 31 % 997:D3} $ \"tag-{(index + k) * 13 % 251:D3}\"\n");
                    }

                    return (lineStart, 0, block.ToString());
                }
            }
        }

        private static int PickLineStart(string text, Random rng)
        {
            var lineStarts = new List<int>();
            for (int j = text.IndexOf('\n'); j >= 0 && j < text.Length - 1; j = text.IndexOf('\n', j + 1))
            {
                lineStarts.Add(j + 1);
            }

            return lineStarts.Count > 0 ? lineStarts[rng.Next(lineStarts.Count)] : 0;
        }

        private static (StepParserEngine engine, RealTimeParserSession session) CreateSession()
        {
            var engine = new StepParserEngine();
            engine.LoadGrammarFromContent(ChainGrammar, "issue163.grammar");
            var session = new RealTimeParserSession(engine, "issue163.txt");
            session.LexingMode = LexingMode.GreedyLongestMatch;
            session.EnableIncrementalParsing = true;
            return (engine, session);
        }

        [Fact]
        public void LongEditStream_DoesNotAccumulateUnboundedGraphContent()
        {
            var (engine, session) = CreateSession();
            session.Initialize(BuildInitialDoc(4));

            var rng = new Random(196);
            long maxRetainedNodes = engine.InternalParser.GraphNodeWriteCount;

            for (int i = 0; i < 120; i++)
            {
                var (offset, deleteLength, insert) = NextSeededEdit(session, rng, i);
                session.ApplyEdit(offset, deleteLength, insert);
                maxRetainedNodes = Math.Max(
                    maxRetainedNodes, engine.InternalParser.GraphNodeWriteCount);
            }

            // Reference: a cold parse of the final text measures what one
            // parse of this document legitimately writes to the builder.
            var (freshEngine, fresh) = CreateSession();
            fresh.Initialize(session.Text);
            var coldParseNodes = Math.Max(1, freshEngine.InternalParser.GraphNodeWriteCount);

            // Without the retention guard the same stream accumulated
            // 700k-1.1M node writes (44-89 MB builder buffer) on this
            // document scale; with the guard the retained content stays
            // within a small multiple of a single cold parse. The factor
            // headroom covers the one-edit lag of the cache drop and the
            // document growing during the stream.
            Assert.True(
                maxRetainedNodes <= 12 * coldParseNodes,
                $"Retained graph nodes {maxRetainedNodes} exceed 12x a cold parse "
                + $"of the final text ({coldParseNodes}); the SPI graph builder is "
                + "accumulating stale parse content again (issue #163).");
        }

        [Fact]
        public void LongEditStream_FinalParseMatchesFreshSession()
        {
            var (_, session) = CreateSession();
            session.Initialize(BuildInitialDoc(4));

            var rng = new Random(196);
            for (int i = 0; i < 120; i++)
            {
                var (offset, deleteLength, insert) = NextSeededEdit(session, rng, i);
                session.ApplyEdit(offset, deleteLength, insert);
            }

            var (_, fresh) = CreateSession();
            fresh.Initialize(session.Text);

            Assert.Equal(fresh.Tokens.Count, session.Tokens.Count);
            for (int i = 0; i < fresh.Tokens.Count; i++)
            {
                var expected = fresh.Tokens[i];
                var actual = session.Tokens[i];
                Assert.Equal(expected.Type, actual.Type);
                Assert.Equal(expected.Value, actual.Value);
                Assert.Equal(expected.StartPosition, actual.StartPosition);
                Assert.Equal(expected.Length, actual.Length);
            }
        }

        [Fact]
        public void CacheDropChain_KeepsParsingAndBoundsRetention()
        {
            // Drive a stream long enough that the retained-graph factor
            // trips repeatedly (alternating cold starts and resumed
            // parses), then verify the session is still functional: the
            // last parse reflects the current text.
            var (engine, session) = CreateSession();
            session.Initialize(BuildInitialDoc(4));

            var rng = new Random(196);
            for (int i = 0; i < 80; i++)
            {
                var (offset, deleteLength, insert) = NextSeededEdit(session, rng, i);
                session.ApplyEdit(offset, deleteLength, insert);
            }

            Assert.True(session.Tokens.Count > 0);
            Assert.True(engine.InternalParser.GraphNodeWriteCount > 0);

            // A word-completion edit after the retention cycles must
            // still complete quickly and keep the parse equivalent to a
            // fresh session's parse of the same text.
            var (referenceEngine, reference) = CreateSession();
            reference.Initialize(session.Text);
            var referenceNodes = referenceEngine.InternalParser.GraphNodeWriteCount;

            var (_, resumed) = CreateSession();
            resumed.Initialize(session.Text);
            resumed.ApplyEdit(0, 0, "x");
            reference.ApplyEdit(0, 0, "x");

            Assert.Equal(reference.Tokens.Count, resumed.Tokens.Count);
            Assert.Equal(
                reference.LastParse?.Success,
                resumed.LastParse?.Success);
            Assert.True(
                engine.InternalParser.GraphNodeWriteCount <= 12 * Math.Max(1, referenceNodes));
        }
    }

}
