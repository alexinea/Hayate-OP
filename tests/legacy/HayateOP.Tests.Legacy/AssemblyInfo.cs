// Test-suite-level "fence" -- semantically consistent with tests/HayateOP.Tests/AssemblyInfo.cs.
// This assembly is dominated by object-pool concurrency / thread stress testing; many classes drive the ThreadPool, GC, SpinLock,
// background eviction / validation callbacks and scaling cooldowns. By default, cross-class parallelism makes the heavy-concurrency classes and the lightweight deterministic cases compete for CPU,
// causing occasional timeouts / timing flakiness and also piling on idle CPU spins on the host machine.
//
// Therefore, test parallelization is disabled at the **assembly level**: all collections run serially, serving as the strongest deterministic fence.
// Its cost is that the whole suite is essentially serial; in exchange, no case contends with other classes, CPU usage converges and results are reproducible.
// If throughput is needed later, once the concurrency classes are stable we can switch to fine-grained collection-level parallelism and keep the lightweight classes in the parallel group.
//
// Generation note (LEGACY): this file belongs to tests/legacy/HayateOP.Tests.Legacy (the legacy generation,
// xunit v2 + VSTest, net6/net7). Assembly-level parallelism disabling uses v2's CollectionBehavior.
// For the net8/net9/net10 (xunit v3 + MTP) version see tests/HayateOP.Tests/AssemblyInfo.cs,
// which switches to Xunit.v3.Parallelization(Mode = ParallelMode.None). The two generations are independent; no conditional compilation is needed.
using Xunit;

// v2: disable cross-collection parallelism at the assembly level
[assembly: CollectionBehavior(DisableTestParallelization = true)]
