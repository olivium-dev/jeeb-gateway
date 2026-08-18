using Xunit;

// Each integration test can start a complete gateway host, including its
// background services. Keep host concurrency bounded so CI runners do not
// exhaust their process, socket, or memory budget while the suite is active.
[assembly: CollectionBehavior(MaxParallelThreads = 2)]
