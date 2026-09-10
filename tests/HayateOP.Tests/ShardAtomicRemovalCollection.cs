using Xunit;

namespace DotNetCore.HayateOP.Tests;

/// <summary>
/// Test collection definition for Shard.Remove atomicity: all tests in the collection run serially.
/// <para>
/// The reason is the same as for <see cref="OptimizationRegressionCollection"/> in this repo: this collection contains
/// high-concurrency stress cases (Parallel.For drives thousands of Add/TryTake/Remove calls, plus manually high-frequency driven
/// background threads for eviction/validation callbacks), which leave jitter on the ThreadPool, GC, and timer precision,
/// causing lightweight deterministic cases within the same collection to fail intermittently.
/// DisableParallelization makes the collection's tests run serially, ensuring reproducible results.
/// </para>
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ShardAtomicRemovalCollection
{
    public const string Name = "ShardAtomicRemoval";
}
