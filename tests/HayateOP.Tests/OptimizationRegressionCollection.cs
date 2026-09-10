using Xunit;

namespace DotNetCore.HayateOP.Tests;

/// <summary>
/// Optimization regression test collection definition:
/// All tests within this collection must run serially (not parallelized).
/// Reason: the upstream 100-thread concurrency stress tests (ConcurrentAcquire_*) leave jitter on the shared
/// ThreadPool / GC / timer precision; simple tests (Acquire/Release/Validation
/// state) become intermittently unstable (flaky pass/fail) under such jitter.
/// DisableParallelization makes this collection's tests run serially, avoiding contamination by the upstream concurrency tests.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class OptimizationRegressionCollection
{
    public const string Name = "OptimizationRegression";
}
