using System;
using System.Diagnostics;
using System.Linq;
using System.Text;
using DevelApp.StepLexer;
using DevelApp.StepParser;
using Xunit;

namespace DevelApp.StepParser.Tests
{
    /// <summary>
    /// Issue #157 follow-up (NL-morphology scale): the 13 natural-language
    /// morphology grammars timed out at the 60s corpus cap even after the
    /// part 1-4 fixes, because exploratory lexing over an ~850-rule
    /// word-list grammar paid four avoidable costs per path step: the
    /// full-rule-scan loop with a per-rule compiled-rule lookup and
    /// pattern-string comparison, a RegexNfa simulation per
    /// (rule, position) even when a forked path had already simulated
    /// the same rule at the same position, an O(tokens) clone copy, and
    /// an O(tokens) fingerprint rebuild per merge candidate. The lexer
    /// now buckets rules by possible first byte, memoizes NFA matches per
    /// input position, and keeps token sequences on persistent
    /// clone-shared spines. These tests pin the associated scale and
    /// semantic invariants.
    /// </summary>
    public class Issue157NlMorphologyScaleTests
    {
        /// <summary>Word count of the synthetic vocabulary.</summary>
        private const int VocabularySize = 700;

        /// <summary>
        /// Build a Danish-flavored morphology grammar: one literal rule per
        /// vocabulary word plus broad regex word-root and suffix rules that
        /// overlap every literal, so exploratory lexing forks and merges
        /// paths continuously (the pathology the NL grammars expose). The
        /// <c>word</c> parse rule has one alternative per vocabulary word
        /// so the parser-side reduction candidate indexes face the same
        /// width the real grammars do.
        /// </summary>
        private static string BuildMorphologyGrammar()
        {
            var sb = new StringBuilder();
            sb.AppendLine("Grammar: NlMorphologyScale");
            sb.AppendLine();
            for (int i = 0; i < VocabularySize; i++)
            {
                sb.AppendLine($"<W{i:D3}> ::= 'morph{i:D3}'");
            }

            sb.AppendLine("<WORD_ROOT> ::= /[a-z]+/");
            sb.AppendLine("<SUFFIX_IZE> ::= /[a-z]{3,9}(ize|ized|izing)/");
            sb.AppendLine("<WS> ::= /[ ]+/");
            sb.AppendLine();
            sb.AppendLine("<text> ::= <words>");
            sb.AppendLine("<words> ::= <words> <word>");
            sb.AppendLine("<words> ::= <word>");
            sb.AppendLine("<word> ::= <word> <SUFFIX_IZE>");
            for (int i = 0; i < VocabularySize; i++)
            {
                sb.AppendLine(i == 0
                    ? "<word> ::= <W000>"
                    : $"        | <W{i:D3}>");
            }

            return sb.ToString();
        }

        /// <summary>
        /// Build a word-salad input that cycles through the vocabulary so
        /// every one of the hundreds of literal rules is exercised.
        /// </summary>
        private static string BuildInput(int wordCount)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < wordCount; i++)
            {
                if (i > 0)
                {
                    sb.Append(' ');
                }

                sb.Append($"morph{i % VocabularySize:D3}");
            }

            return sb.ToString();
        }

        /// <summary>
        /// A 600-word input over the 700-rule overlapping vocabulary must
        /// lex to completion far inside the corpus cap. Before the fix the
        /// per-step full-rule scan, NFA re-simulation and quadratic path
        /// cloning made a grammar of this shape orders of magnitude slower
        /// (the Danish grammar lexed a comparable input for over 30
        /// seconds).
        /// </summary>
        [Fact]
        public void Lex_WithOverlappingMorphologyVocabulary_CompletesWithinBound()
        {
            var engine = new StepParserEngine();
            engine.LoadGrammarFromContent(BuildMorphologyGrammar(), "nl-morphology-scale");

            var input = BuildInput(600);
            var sw = Stopwatch.StartNew();
            var result = engine.Lex(input, "nl-morphology-scale.txt");
            sw.Stop();

            // Every vocabulary word must be consumed; the broad
            // WORD_ROOT overlap means the token union is at least the
            // word count.
            Assert.True(result.Tokens.Count >= 600,
                $"expected at least 600 tokens, got {result.Tokens.Count}");
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(30),
                $"lexing 600 words over a 700-rule vocabulary took {sw.ElapsedMilliseconds}ms");
        }

        /// <summary>
        /// The full lex+parse pipeline over the wide-branching grammar
        /// must complete within the same generous bound: the parser-side
        /// reduction planning now derives candidates from symbol-keyed
        /// indexes instead of scanning all 700+ rules per path per step.
        /// </summary>
        [Fact]
        public void Parse_WithWideBranchingMorphologyGrammar_CompletesWithinBound()
        {
            var engine = new StepParserEngine();
            engine.LoadGrammarFromContent(BuildMorphologyGrammar(), "nl-morphology-scale");

            var input = BuildInput(300);
            var sw = Stopwatch.StartNew();
            var result = engine.Parse(input, "nl-morphology-scale.txt");
            sw.Stop();

            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(60),
                $"parsing 300 words over a 700-alternative grammar took {sw.ElapsedMilliseconds}ms");
            Assert.True(result.Tokens.Count >= 300,
                $"expected at least 300 tokens, got {result.Tokens.Count}");
        }

        /// <summary>
        /// A cloned lexer path shares the original's token spine: cloning
        /// is O(1) and appending a token to one path must not leak into
        /// the other. Token type equality (used by ambiguity merging)
        /// diverges exactly when the sequences diverge.
        /// </summary>
        [Fact]
        public void LexerPath_CloneSharesSpine_AndAppendsDiverge()
        {
            var original = new LexerPath(1, 10);
            original.AddToken(MakeToken("A", "morph000"));
            original.AddToken(MakeToken("B", "morph001"));

            var clone = original.Clone(2);

            Assert.Equal(2, clone.TokenCount);
            Assert.True(original.TokenTypesEqualFromTop(clone));

            clone.AddToken(MakeToken("C", "morph002"));
            Assert.Equal(3, clone.TokenCount);
            Assert.Equal(2, original.TokenCount);

            // Different lengths: sequences are no longer equal.
            Assert.False(original.TokenTypesEqualFromTop(clone));

            // The materialized views mirror the independent sequences.
            Assert.Equal("A,B", string.Join(",", original.Tokens.Select(t => t.Type)));
            Assert.Equal("A,B,C", string.Join(",", clone.Tokens.Select(t => t.Type)));
        }

        /// <summary>
        /// Token type equality over shared spines must match the exact
        /// list semantics of the previous materialized-list comparison:
        /// equal-length equal-type sequences compare true (even when the
        /// tokens are different objects), and a divergence anywhere makes
        /// them false.
        /// </summary>
        [Fact]
        public void LexerPath_TokenTypesEqualFromTop_MatchesListSemantics()
        {
            var first = new LexerPath(1, 0);
            first.AddToken(MakeToken("A", "morph000"));
            first.AddToken(MakeToken("B", "morph001"));
            first.AddToken(MakeToken("A", "morph002"));

            var second = new LexerPath(2, 0);
            second.AddToken(MakeToken("A", "morph100"));
            second.AddToken(MakeToken("B", "morph101"));
            second.AddToken(MakeToken("A", "morph102"));

            Assert.True(first.TokenTypesEqualFromTop(second));
            Assert.Equal(first.GetTokenFingerprint(), second.GetTokenFingerprint());

            second.AddToken(MakeToken("A", "morph103"));
            Assert.False(first.TokenTypesEqualFromTop(second));
        }

        /// <summary>
        /// External mutations through the public <see cref="LexerPath.Tokens"/>
        /// list stay authoritative: an append through the materialized
        /// view re-binds the spine, is visible through the O(1) counters,
        /// and participates in equality and fingerprint comparisons.
        /// </summary>
        [Fact]
        public void LexerPath_ExternalTokenListMutation_IsAuthoritative()
        {
            var path = new LexerPath(1, 0);
            path.Tokens.Add(MakeToken("A", "morph000"));
            path.Tokens.Add(MakeToken("B", "morph001"));

            // Spine-side operations adopt the externally mutated view.
            Assert.Equal(2, path.TokenCount);

            path.AddToken(MakeToken("C", "morph002"));
            var view = path.Tokens;
            Assert.Equal(3, view.Count);
            Assert.Equal("C", view[2].Type);

            var clone = path.Clone(2);
            Assert.True(path.TokenTypesEqualFromTop(clone));
            Assert.Equal(path.GetTokenFingerprint(), clone.GetTokenFingerprint());
        }

        /// <summary>
        /// The rolling fingerprint is cached and advanced incrementally:
        /// repeated requests after appends are cheap and stable, and two
        /// paths pinned to the same spine prefix keep equal fingerprints.
        /// </summary>
        [Fact]
        public void LexerPath_Fingerprint_StableAndIncremental()
        {
            var path = new LexerPath(1, 0);
            long previous = path.GetTokenFingerprint();
            for (int i = 0; i < 100; i++)
            {
                path.AddToken(MakeToken(i % 2 == 0 ? "A" : "B", $"morph{i:D3}"));
                var current = path.GetTokenFingerprint();
                Assert.Equal(current, path.GetTokenFingerprint());
                if (i > 0)
                {
                    Assert.NotEqual(previous, current);
                }

                previous = current;
            }
        }

        /// <summary>Make a string-backed token for spine tests.</summary>
        private static StepToken MakeToken(string type, string value)
        {
            return new StepToken(type, value, new CodeLocation("spine-test", 1, 1, 1, 1, "default"), "default");
        }
    }
}
