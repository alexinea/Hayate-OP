// Assembly-level "fence" (highest priority; requirements R2, R3):
// This assembly focuses on object-pool concurrency and thread stress testing; many classes drive the ThreadPool, GC, SpinLock,
// background eviction/validation callbacks, and scale-up/down cooldown. By default, cross-class parallelism makes heavy-concurrency classes compete for CPU with lightweight deterministic cases,
// causing intermittent timeouts/timing flakiness and adding idle CPU spin on the host machine.
//
// Therefore, disable test parallelism at the **assembly level** here: all collections run serially, acting as the strongest determinism fence.
// The cost is that the whole suite is essentially serial; the benefit is that no case competes concurrently with other classes, so CPU usage converges and results are reproducible.
// If throughput is needed later, once the concurrency classes are stable you can switch to fine-grained parallelism per collection, keeping the lightweight classes in the parallel group.
//
// Generation note (FUTURE): this file belongs to tests/HayateOP.Tests (future generation, xunit v3 + MTP).
// In v3, v2's CollectionBehavior.DisableTestParallelization is obsolete (compiler error CS0619),
// so assembly-level parallelism disable uses v3's Xunit.v3.Parallelization(Mode = ParallelMode.None).
// The net6/net7 (v2 + VSTest) compatible version is in tests/legacy/HayateOP.Tests.Legacy/AssemblyInfo.cs,
// which uses the v2 syntax; this file no longer needs conditional-compilation splits.
using System.Diagnostics.CodeAnalysis;
using Xunit.v3;

// v3: disable all parallelism at the assembly level (equivalent to v2's DisableTestParallelization)
[assembly: Parallelization(Mode = Xunit.Sdk.ParallelMode.None)]

// xUnit1031 ("Test methods should not use blocking task operations") exists because a blocked test
// thread can starve the other tests that share xUnit's thread pool, which risks a deadlock. That
// hazard needs concurrency, and parallelization is switched off immediately above, so the rule no
// longer applies here — xUnit's own documentation says as much for an assembly that disables
// parallelism. The affected cases are the concurrency contracts themselves: they assert that a
// borrow blocks until a return frees the slot, that a pre-warmed acquire stays parked until the
// gate opens, and similar wait-and-wake behaviour. Those assertions are the thing under test, so
// rewriting them as await would delete the coverage rather than fix a defect. Suppressed by
// justification, deliberately not by a blanket NoWarn (see common.tests.props for that convention).
[assembly: SuppressMessage("xUnit", "xUnit1031",
    Justification = "Assembly-level parallelization is disabled, so the blocked-thread starvation hazard the rule guards against cannot occur.")]
