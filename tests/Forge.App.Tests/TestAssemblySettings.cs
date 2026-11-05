using Xunit;

// Headless Avalonia sessions install a process-global Application/dispatcher, so concurrent sessions
// race inside EnsureIsolatedApplication and hang or fail. Run this assembly's tests sequentially;
// per-test temp directories stay unique regardless.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
