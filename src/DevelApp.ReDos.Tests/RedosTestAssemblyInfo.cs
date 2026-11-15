using Xunit;

// The ReDoS suite measures wall-clock times at microsecond scale
// (greedy scans complete in 0.005-1.6 ms at 100 KB). Running the test
// collections in parallel would distort every measurement: the
// backtracking baselines burn 500 ms match timeouts on other threads
// while the scaling fits are being timed. The whole assembly therefore
// runs serialized - the measurements must be trustworthy in CI, not
// just on an idle machine.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
