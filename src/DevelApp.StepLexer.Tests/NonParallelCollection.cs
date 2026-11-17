namespace DevelApp.StepLexer.Tests
{
    /// <summary>
    /// Collection for tests that measure process-wide counters such as
    /// <c>GC.GetTotalAllocatedBytes</c>. Those counters include allocations
    /// from every thread in the test host, so such tests must not run in
    /// parallel with any other test - otherwise concurrent test collections
    /// pollute the measurement and flake the assertion on loaded CI runners.
    /// </summary>
    [CollectionDefinition(Name, DisableParallelization = true)]
    public class NonParallelCollection
    {
        public const string Name = "NonParallel";
    }
}
