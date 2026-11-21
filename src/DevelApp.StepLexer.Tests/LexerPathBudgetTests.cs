using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using DevelApp.StepLexer;
using Xunit;

namespace DevelApp.StepLexer.Tests
{
    /// <summary>
    /// Tests for the deterministic exploratory-mode lexer path budget
    /// (issue #157, part 1): overlap-heavy grammars fork one path per
    /// matching rule per position, which compounds exponentially; the
    /// budget caps the concurrent active path set after every step
    /// (position-first ordering) while completed progress, emitted
    /// tokens and greedy mode stay unchanged.
    /// </summary>
    public class LexerPathBudgetTests
    {
        /// <summary>
        /// Step the lexer until it completes (or the guard trips), the
        /// same way <c>DevelApp.StepParser.StepParserEngine.RunLexingPhase</c>
        /// drives it. Tracks the maximum active path count observed.
        /// </summary>
        private static (List<StepToken> Tokens, int MaxActivePaths, bool Completed) RunToCompletion(
            StepLexer lexer, byte[] input)
        {
            lexer.Initialize(input, "budget-test.txt");
            var tokens = new List<StepToken>();
            var maxActivePaths = 0;
            var guard = 0;
            while (lexer.ActivePaths.Any(p => p.IsValid && p.Position < input.Length))
            {
                var result = lexer.Step();
                tokens.AddRange(result.NewTokens);
                maxActivePaths = Math.Max(maxActivePaths, lexer.ActivePaths.Count);
                if (++guard > 20_000)
                {
                    break;
                }
            }
            return (tokens, maxActivePaths, lexer.CompletedInput);
        }

        /// <summary>
        /// A grammar whose rules overlap on every position: three rules
        /// match any letter run (single letter, any run, and a two-letter
        /// prefix shape), so exploratory mode forks roughly three paths
        /// per position and doubles from there. Without a budget this
        /// shape is the corpus blowup from issue #157 (fork factor > 1
        /// per step sustained over hundreds of bytes).
        /// </summary>
        private static StepLexer CreateForkGrammar(int budget)
        {
            var lexer = new StepLexer { Mode = LexingMode.Exploratory, PathBudget = budget };
            lexer.AddRule(new TokenRule("word", "/[a-z]+/", "", 5));
            lexer.AddRule(new TokenRule("letter", "/[a-z]/", "", 4));
            lexer.AddRule(new TokenRule("two-char-lookahead", "/[a-z][a-z]?/", "", 3));
            lexer.AddRule(new TokenRule("ws", "/[ \\t\\r\\n]+/", "", 1) { IsSkippable = true });
            return lexer;
        }

        private static byte[] LongForkInput()
        {
            // 300 "words" of a single letter would pack fork chains back
            // to back; mixed lengths exercise partial-match interplay.
            var sb = new StringBuilder();
            for (int i = 0; i < 300; i++)
            {
                sb.Append('a');
                if (i % 3 == 0)
                {
                    sb.Append(' ');
                }
            }
            return Encoding.UTF8.GetBytes(sb.ToString());
        }

        [Fact]
        public void ExploratoryMode_OverlapGrammar_BudgetCapsActivePaths()
        {
            var lexer = CreateForkGrammar(budget: 8);
            var input = LongForkInput();

            var (tokens, maxActivePaths, completed) = RunToCompletion(lexer, input);

            Assert.True(completed, "the furthest-advanced path must survive pruning and tile the input");
            Assert.True(maxActivePaths <= 8, $"active paths must stay within the budget, saw {maxActivePaths}");
            Assert.True(tokens.Count > 0);
        }

        [Fact]
        public void ExploratoryMode_BudgetPruning_IsDeterministicAcrossRuns()
        {
            var input = LongForkInput();

            var first = RunToCompletion(CreateForkGrammar(budget: 4), (byte[])input.Clone());
            var second = RunToCompletion(CreateForkGrammar(budget: 4), (byte[])input.Clone());

            Assert.Equal(
                first.Tokens.Select(t => t.Type),
                second.Tokens.Select(t => t.Type));
        }

        [Fact]
        public void ExploratoryMode_BudgetOfOne_StillCompletesTileableInput()
        {
            // With a budget of one only the path ordering decides
            // survival; a fully matchable input must still be consumed
            // to the end, because pruning always keeps the
            // furthest-advanced path (position-first ordering).
            var lexer = CreateForkGrammar(budget: 1);
            var input = Encoding.UTF8.GetBytes("aa bb ccc ");

            var (tokens, maxActivePaths, completed) = RunToCompletion(lexer, input);

            Assert.True(completed);
            Assert.True(maxActivePaths <= 1);
            Assert.True(tokens.Count > 0);
        }

        [Fact]
        public void ExploratoryMode_ZeroBudget_DisablesPruning_UnboundedForkSeen()
        {
            // 0 disables the budget (opt-out for analyses that need the
            // full unpruned path set). Unpruned, the fork grammar's path
            // count multiplies every step, so a HANDFUL of steps already
            // exceeds the default budget; running further would grow the
            // set without bound (that is precisely the blowup the budget
            // contains). A fixed small step count proves the pruning
            // assertions actually exercise the cap instead of never
            // tripping it, without allocating the exponential set.
            var sb = new StringBuilder();
            sb.Append('a', 10);
            var input = Encoding.UTF8.GetBytes(sb.ToString());

            var unbudgeted = CreateForkGrammar(budget: 0);
            unbudgeted.Initialize(input, "unbudgeted.txt");
            for (int i = 0; i < 8; i++)
            {
                unbudgeted.Step();
            }
            var unbudgetedActive = unbudgeted.ActivePaths.Count(p => p.IsValid);

            var budgeted = CreateForkGrammar(budget: 8);
            budgeted.Initialize(input, "budgeted.txt");
            for (int i = 0; i < 8; i++)
            {
                budgeted.Step();
            }
            var budgetedActive = budgeted.ActivePaths.Count(p => p.IsValid);

            Assert.True(unbudgetedActive > 64,
                $"unbudgeted fork grammar must exceed the default budget within 8 steps, saw {unbudgetedActive}");
            Assert.True(budgetedActive <= 8,
                $"budgeted fork grammar must stay within its budget, saw {budgetedActive}");
        }

        [Fact]
        public void GreedyMode_BudgetIsInert_SinglePathUnchanged()
        {
            // Greedy mode already keeps exactly one path per step; the
            // budget must not change its token stream.
            var greedy = new StepLexer { Mode = LexingMode.GreedyLongestMatch, PathBudget = 64 };
            greedy.AddRule(new TokenRule("word", "/[a-z]+/", "", 5));
            greedy.AddRule(new TokenRule("letter", "/[a-z]/", "", 4));
            greedy.AddRule(new TokenRule("ws", "/[ \\t\\r\\n]+/", "", 1) { IsSkippable = true });

            var input = Encoding.UTF8.GetBytes("hej med dig");
            var (tokens, maxActivePaths, completed) = RunToCompletion(greedy, input);

            Assert.True(completed);
            Assert.True(maxActivePaths <= 1);
            Assert.Equal(new[] { "word", "word", "word" }, tokens.Select(t => t.Type));
        }

        [Fact]
        public void Pruning_PreservesAlreadyEmittedTokenUnion_Coverage()
        {
            // The budget truncates FUTURE path exploration only; tokens
            // emitted before pruning stay in the collected union, so the
            // span coverage of the union never shrinks below the spans
            // of any surviving path. Every byte of the input must be
            // covered by at least one emitted token or skipped span.
            var lexer = CreateForkGrammar(budget: 8);
            var input = Encoding.UTF8.GetBytes("ab cd ef gh ij kl mn op qr st");

            var (tokens, _, completed) = RunToCompletion(lexer, input);

            Assert.True(completed);
            var covered = new bool[input.Length];
            foreach (var token in tokens)
            {
                for (int i = token.StartPosition; i < token.StartPosition + token.Length && i < covered.Length; i++)
                {
                    covered[i] = true;
                }
            }

            // Skippable spans (the whitespace between words) never emit
            // tokens by design, so only NON-skippable positions must be
            // covered by the union.
            Assert.All(
                Enumerable.Range(0, input.Length).Where(i => input[i] != (byte)' '),
                i => Assert.True(covered[i], $"letter position {i} must be covered by the pruned token union"));
        }
    }
}
