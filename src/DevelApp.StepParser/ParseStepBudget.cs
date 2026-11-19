namespace DevelApp.StepParser
{
    /// <summary>
    /// Shared parser step budget (issue #158): <c>StepParserEngine.Parse</c>
    /// and <c>RealTimeParserSession.RunParse</c> previously ran slightly
    /// different budgets (tokens*20 vs tokens*20+64), so the two entry points
    /// were not comparable. One definition keeps them aligned. The +64 floor
    /// covers tiny or empty token streams, which still need enough steps to
    /// reach end of input.
    /// </summary>
    internal static class ParseStepBudget
    {
        /// <summary>Parser steps allowed per token of the input.</summary>
        public const int StepsPerToken = 20;

        /// <summary>Minimum step floor so tiny or empty inputs can complete.</summary>
        public const int MinSteps = 64;

        /// <summary>
        /// The parser step budget for a parse of a stream with the given
        /// token count.
        /// </summary>
        /// <param name="tokenCount">The number of tokens to parse.</param>
        /// <returns>The maximum number of parser steps the entry point runs.</returns>
        public static int ForTokenCount(int tokenCount)
        {
            return tokenCount * StepsPerToken + MinSteps;
        }
    }
}
