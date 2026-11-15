using Xunit;

// The parser ReDoS suite measures wall-clock times under hard timeouts
// and fits scaling curves. Running the test collections in parallel would
// distort every measurement, exactly as in the lexer-layer suite. The
// whole assembly therefore runs serialized - the measurements must be
// trustworthy in CI, not just on an idle machine.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
