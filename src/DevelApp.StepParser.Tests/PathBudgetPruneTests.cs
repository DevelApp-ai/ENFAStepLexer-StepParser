using Xunit;
using DevelApp.StepParser;
using System.Collections.Generic;
using System.Linq;

namespace DevelApp.StepParser.Tests
{
    /// <summary>
    /// Tests for the deterministic path-budget prune (issue #157, part 3):
    /// when the budget prunes, the highest-scoring path is always kept, and the
    /// budget is configurable via <see cref="StepParser.PathBudget"/>.
    /// </summary>
    public class PathBudgetPruneTests
    {
        private static List<ParserPath> MakePaths(int count, float survivorScore, int survivorPosition)
        {
            var paths = new List<ParserPath>();
            for (int i = 0; i < count; i++)
            {
                paths.Add(new ParserPath(i)
                {
                    TokenPosition = i == count - 1 ? survivorPosition : 10, // survivor lags in position
                    Score = i == count - 1 ? survivorScore : 1.0f,
                    IsValid = true
                });
            }
            return paths;
        }

        [Fact]
        public void PruneToPathBudget_KeepsHighestScoringPathEvenWhenItRanksLowByPosition()
        {
            var parser = new StepParser();
            // 15 paths; the last one has the highest score (9.5) but the lowest
            // token position (0), so the position-first ordering ranks it last.
            var paths = MakePaths(15, survivorScore: 9.5f, survivorPosition: 0);

            parser.PruneToPathBudget(paths);

            Assert.Equal(10, paths.Count);
            Assert.Contains(paths, p => p.Score == 9.5f);
        }

        [Fact]
        public void PruneToPathBudget_TopScorerInsidePrefix_PlainTruncateNoDuplicates()
        {
            var parser = new StepParser();
            var paths = new List<ParserPath>();
            for (int i = 0; i < 15; i++)
            {
                paths.Add(new ParserPath(i)
                {
                    TokenPosition = 10,
                    Score = i == 0 ? 9.5f : 1.0f, // top scorer ranks first anyway
                    IsValid = true
                });
            }

            parser.PruneToPathBudget(paths);

            Assert.Equal(10, paths.Count);
            Assert.Equal(1, paths.Count(p => p.Score == 9.5f));
        }

        [Fact]
        public void PruneToPathBudget_AtOrBelowBudget_Unchanged()
        {
            var parser = new StepParser();
            var paths = MakePaths(10, survivorScore: 9.5f, survivorPosition: 0);

            parser.PruneToPathBudget(paths);

            Assert.Equal(10, paths.Count);
        }

        [Fact]
        public void PruneToPathBudget_RespectsCustomPathBudget()
        {
            var parser = new StepParser { PathBudget = 4 };
            var paths = MakePaths(12, survivorScore: 9.5f, survivorPosition: 0);

            parser.PruneToPathBudget(paths);

            Assert.Equal(4, paths.Count);
            Assert.Contains(paths, p => p.Score == 9.5f);
        }

        [Fact]
        public void PruneToPathBudget_DefaultBudgetIsTen()
        {
            var parser = new StepParser();
            Assert.Equal(10, parser.PathBudget);
        }
    }
}
