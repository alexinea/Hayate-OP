# Which counters `EnableMetrics` gates

`EnableMetrics` is documented as an observability switch: turn it off and the pool stops
feeding metrics. In practice it is also the switch that decides whether **three of the
four cumulative traffic counters are written at all**. This page records the audit, the
decision that follows from it, and the rule for any counter added later.

## 1. The audit

Every counter on the general engine, the condition that gates its write, and the members
that read it. "Unconditional" means the write happens on every operation that can move the
counter, whatever the option values are.

| Counter | Write site | Gate | Reported by |
| :--- | :--- | :--- | :--- |
| `TotalAcquired` | borrow hit, async borrow hit, create-on-demand borrow | **Unconditional** | `GetStats()`, `TakeSnapshot()` |
| `TotalCreated` | object creation | `EnableMetrics` | `GetStats()`, `TakeSnapshot()` |
| `TotalReleased` | successful return | `EnableMetrics` | `GetStats()` |
| `TotalMissed` | abort branch, timeout branches | `EnableMetrics` | `GetStats()`, `TakeSnapshot()` |
| `WaitTime*` (sum, count, min, max) | borrow hit | `EnableMetrics` — `UpdateWaitTimeStats` is only called inside the metrics block | `GetStats()` |
| `LeaseTime*` (sum, count, min, max) | successful return | `EnableMetrics` — `UpdateLeaseTimeStats` is only called inside the metrics block | `GetStats()` |
| `LeakDetectedCount` | `TakeSnapshot()` leak scan | `EnableLeakDetection` | `GetStats()`, `TakeSnapshot()` |
| `LeakSuspectedCount` | `TakeSnapshot()` leak scan (leak detection off) | none — it is the "leak detection is off" branch | `GetStats()`, `TakeSnapshot()` |
| `AcquireAllocatedBytes` / `ReleaseAllocatedBytes` / `*AllocationSamples` | borrow and return | `EnableAllocationTracking` | `GetStats()`, `TakeSnapshot()` |

Two consequences worth stating plainly:

* **The counters are not one family.** Four different gates are in play — metrics,
  leak detection, allocation tracking, and none at all — while `GetStats()` and
  `TakeSnapshot()` read every counter unconditionally. Reading a `0` from the statistics
  object therefore does not by itself distinguish "nothing happened" from "the gate for
  that counter is closed".
* **The HealthCheck and Prometheus surfaces expose the split.** They publish
  `TotalCreated` / `TotalReleased` / `TotalMissed` (and, for Prometheus,
  `TotalAcquired`) straight from the statistics object, so a host that runs with metrics
  off sees three of those four series pinned at 0 while the fourth keeps moving.

## 2. The one counter that stays unconditional, and why

`TotalAcquired` is the odd one out and the asymmetry is intentional. It is not only a
metric — it is the pool's **borrow-count contract**, the value callers verify borrow/return
balance against, and it is asserted on by tests that deliberately run with metrics off:

| Test | Configuration | Assertion |
| :--- | :--- | :--- |
| `BasicFunctionTests.Acquire_ShouldIncrementTotalAcquired` | default options — metrics **off** | `TotalAcquired == 3`, then `== 4` after a re-borrow |
| `ObjectPoolCompatTests` (compat package) | explicit `WithEnableMetrics(false)` | `GetStats().TotalAcquired >= 2` |
| `AllocationTrackingTests.Enabled_ShouldNotChangePoolBehaviour` | metrics off on both pools | `offPool.TotalAcquired == onPool.TotalAcquired` |

Gating it would silently zero all of them.

### Is there a cheaper path? No — not a correct one

The write is a single `Interlocked.Increment`, i.e. one `lock xadd` on a cache line that
is effectively uncontended once the pool is warm. The alternatives all cost more than they
save:

| Alternative | Why it is not taken |
| :--- | :--- |
| Non-atomic increment | Incorrect. The counter is asserted with an **exact** value under `Parallel.For` (`ThreadFuzzTests` checks `workers * Rounds`), and a plain `++` loses updates under concurrency |
| Thread-local counters merged on read | Turns a single atomic add into per-thread storage plus a merge in `GetStats()`, and changes `GetStats()` from O(1) to O(threads). The saving is a few nanoseconds on a path that already pays a `ConcurrentDictionary` probe and a free-list lock on the return side |
| Gate it like the other three | Buys back roughly one atomic add on the borrow path, at the cost of the contract above — a behaviour change with no measurable benefit |

So the evaluation result is: **keep the unconditional write.** The cost it adds to the
borrow path is a single uncontended atomic add, below the resolution of the benchmark
harness, and it buys the one traffic counter that does not depend on an option value.

## 3. Rule for counters added later

A counter belongs in the `EnableMetrics` group — the default — when it exists to feed
observability. It may be left unconditional only if it is a **contract** that callers and
tests verify with metrics off, and it must then be listed in the table in §1 and asserted
in `MetricsGatingTests`, so the exception cannot accumulate silently.

`MetricsGatingTests` pins the whole arrangement: with metrics off `TotalAcquired` advances
while `TotalCreated`, `TotalReleased` and `TotalMissed` stay at 0; with metrics on all four
advance; and the leak-detection and allocation-tracking counters follow their own gates
rather than the metrics switch.

## 4. Corrections applied

`ThreadFuzzTests` (future and legacy mirrors) carried the comment
"TotalAcquired/TotalReleased counts are gated by metrics, so assertions need it enabled".
Only `TotalReleased` is gated; the comment has been corrected, and the contract is now
asserted rather than described.

## See also

* [`docs/hot-path-costs.md`](hot-path-costs.md) — what each switch costs on the borrow and
  return paths, including the counter writes covered here.
* [`docs/BREAKING-CHANGES.md`](BREAKING-CHANGES.md) — the lean path keeps no cumulative
  counters at all, which is the extreme case of this gating.
