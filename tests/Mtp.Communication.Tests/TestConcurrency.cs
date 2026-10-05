// Each collection owns real processes, but inherited stdin/stdout pipe I/O shares
// this test process's thread pool. Bound concurrent fixtures; concurrency inside
// each multi-service/action test remains unchanged.
[assembly: Xunit.CollectionBehavior(MaxParallelThreads = 2)]
