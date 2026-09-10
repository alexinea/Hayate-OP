using Xunit;

namespace DotNetCore.HayateOP.Tests;

/// <summary>
/// ShardAtomicRemoval test collection definition: all tests in the collection run serially.
/// <para>
/// The reason is the same as for this repo's <see cref="OptimizationRegressionCollection"/>: this collection contains
/// high-concurrency stress cases (Parallel.For driving thousands of Add/TryTake/Remove calls, plus manually high-frequency driven
/// eviction / validation-callback background threads), which leave jitter on the ThreadPool, GC and timer precision,
/// causing the lightweight deterministic cases in the same collection to fail occasionally.
/// Disabling parallelization makes the in-collection tests run serially, guaranteeing reproducible results.
/// </para>
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ShardAtomicRemovalCollection
{
    public const string Name = "ShardAtomicRemoval";
}
