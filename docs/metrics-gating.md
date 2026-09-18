# Which switch gates which counter

Counting is governed by **two layers**, not one:

* `EnableDiagnostics` — the **master switch** over the whole diagnostic surface. On by default.
  Switching it off takes the cumulative counters, the timing statistics, the `IHayateMetrics` sink
  and the per-operation debug trace off the borrow and return paths together.
* `EnableMetrics` and `EnableAllocationTracking` — the **sub-switches** below it. They can trim the
  surface further while diagnostics stay on, and they are normalized to `false` whenever the master
  switch is off.

`EnableMetrics` is documented as an observability switch: turn it off and the pool stops feeding
metrics. In practice it is also the switch that decides whether **three of the four cumulative
traffic counters are written at all**. This page records the audit, the decision that follows from
it, how the master switch relates to it, and the rule for any counter added later.

## 1. The audit

Every counter on the general engine, the condition that gates its write, and the members that read
it. "Unconditional" means the write happens on every operation that can move the counter, whatever
the option values are — except through the `EnableDiagnostics` master switch, which is recorded in
its own column because it sits above every row.

| Counter | Write site | Gate | Master switch |
| :--- | :--- | :--- | :--- |
| `TotalAcquired` | borrow hit, async borrow hit, create-on-demand borrow | **Unconditional** | `EnableDiagnostics` |
| `TotalCreated` | object creation | `EnableMetrics` | via `EnableMetrics` |
| `TotalReleased` | successful return | `EnableMetrics` | via `EnableMetrics` |
| `TotalMissed` | abort branch, timeout branches | `EnableMetrics` | via `EnableMetrics` |
| `WaitTime*` (sum, count, min, max) | borrow hit | `EnableMetrics` — `UpdateWaitTimeStats` is only called inside the metrics block | via `EnableMetrics` |
| `LeaseTime*` (sum, count, min, max) | successful return | `EnableMetrics` — `UpdateLeaseTimeStats` is only called inside the metrics block | via `EnableMetrics` |
| `LeakDetectedCount` | `TakeSnapshot()` leak scan | `EnableLeakDetection` | none — follows its own switch |
| `LeakSuspectedCount` | `TakeSnapshot()` leak scan (leak detection off) | none — it is the "leak detection is off" branch | none |
| `AcquireAllocatedBytes` / `ReleaseAllocatedBytes` / `*AllocationSamples` | borrow and return | `EnableAllocationTracking` | via `EnableAllocationTracking` |

Two consequences worth stating plainly:

* **The counters are not one family.** Four different gates are in play — metrics, leak detection,
  allocation tracking, and none at all — while `GetStats()` and `TakeSnapshot()` read every counter
  unconditionally. Reading a `0` from the statistics object therefore does not by itself distinguish
  "nothing happened" from "the gate for that counter is closed".
* **The HealthCheck and Prometheus surfaces expose the split.** They publish `TotalCreated` /
  `TotalReleased` / `TotalMissed` (and, for Prometheus, `TotalAcquired`) straight from the statistics
  object, so a host that runs with metrics off sees three of those four series pinned at 0 while the
  fourth keeps moving.

## 2. The one counter that stays unconditional, and why

`TotalAcquired` is the odd one out and the asymmetry is intentional. It is not only a metric — it is
the pool's **borrow-count contract**, the value callers verify borrow/return balance against, and it
is asserted on by tests that deliberately run with metrics off:

| Test | Configuration | Assertion |
| :--- | :--- | :--- |
| `BasicFunctionTests.Acquire_ShouldIncrementTotalAcquired` | default options — metrics **off** | `TotalAcquired == 3`, then `== 4` after a re-borrow |
| `ObjectPoolCompatTests` (compat package) | explicit `WithEnableMetrics(false)` | `GetStats().TotalAcquired >= 2` |
| `AllocationTrackingTests.Enabled_ShouldNotChangePoolBehaviour` | metrics off on both pools | `offPool.TotalAcquired == onPool.TotalAcquired` |

Gating it behind `EnableMetrics` would silently zero all of them.

### Is there a cheaper path? No — not a correct one

The write is a single `Interlocked.Increment`, i.e. one `lock xadd` on a cache line that is
effectively uncontended once the pool is warm. The alternatives all cost more than they save:

| Alternative | Why it is not taken |
| :--- | :--- |
| Non-atomic increment | Incorrect. The counter is asserted with an **exact** value under `Parallel.For` (`ThreadFuzzTests` checks `workers * Rounds`), and a plain `++` loses updates under concurrency |
| Thread-local counters merged on read | Turns a single atomic add into per-thread storage plus a merge in `GetStats()`, and changes `GetStats()` from O(1) to O(threads). The saving is a few nanoseconds on a path that already pays a `ConcurrentDictionary` probe and a free-list lock on the return side |
| Gate it like the other three | Buys back roughly one atomic add on the borrow path, at the cost of the contract above — a behaviour change with no measurable benefit |

So the evaluation result is: **keep the unconditional write.** The cost it adds to the borrow path
is a single uncontended atomic add, below the resolution of the benchmark harness, and it buys the
one traffic counter that does not depend on an option value.

## 3. The master switch that does opt out

The conclusion in §2 is about `EnableMetrics` — it must not take `TotalAcquired` with it, because a
caller who only turned metrics off still expects the borrow balance to move. `EnableDiagnostics`
answers a different question, and that is why it exists as a separate, higher switch rather than as
another value of the metrics option:

* **It states the intent differently.** `EnableMetrics = false` means "record less but stay
  observable". `EnableDiagnostics = false` means "take the bookkeeping surface off this path
  entirely" — the configuration for a pool sitting on a measured hot path, where the atomic add, the
  `ConcurrentDictionary` probe and the trace's `params object[]` are all unwanted. There is no
  caller contract to preserve in that configuration because the caller has said, explicitly and in
  one place, that they do not want any of it.
* **It is the only switch that reaches every row.** The debug traces on the borrow, return and
  shard paths are not metrics-gated — they are emitted whenever metrics are off and diagnostics are
  on, so trimming them needed a switch above both. Gating them on `EnableMetrics` instead would have
  changed what `EnableMetrics = false` means for existing users.
* **It composes with the sub-switches instead of replacing them.** Normalization (§3.1) collapses it
  downwards: the master switch being off implies the sub-switches are off, so every
  `if (_enableMetrics)` block may rely on diagnostics being open, and only the writes that are *not*
  metrics-gated — `TotalAcquired` and the traces — carry their own check. That invariant is what
  keeps the number of new branches on the hot path at three.

### 3.1 Normalization, and what it does not touch

`ApplyFeatureSwitches()` closes `EnableMetrics` and `EnableAllocationTracking` whenever
`EnableDiagnostics` is off, and both profiles write the master switch explicitly (`UseLeanProfile()`
closes it, `UseFullProfile()` opens it). The collapsed result is visible through `GetOptions()`. This
follows the existing "mode wins" rule rather than rejecting the combination, exactly as the lean
block does.

Two things deliberately stay outside the master switch:

* **Lifecycle and problem logs.** `Information` / `Warning` / `Error` entries — construction,
  disposal, capacity alarms, prepare failures, abandoned reclaims — are not per-operation traces and
  keep being written. Turning off diagnostics must not make a pool that is failing quietly.
* **Counters owned by another feature.** `LeakDetectedCount`, `LeakSuspectedCount`, the capacity
  alarm and `IsDisposed` keep following their own switches. `EnableDiagnostics` gates the
  *bookkeeping surface*, not the pool's correctness machinery.

### 3.2 A sink with no surface to write to

Registering a custom `IHayateMetrics` while diagnostics are off fails the build, with a message that
names both switches. This is the pre-existing `EnableMetrics` guard (a registered sink that cannot
record is a mistake in either configuration) extended to cover the master switch, and it is why the
dependency-injection registration consults `EnableDiagnostics` before attaching its own sink — a DI
user never fast-fails on a configuration they did not write.

## 4. Rule for counters added later

A counter belongs in the `EnableMetrics` group — the default — when it exists to feed observability.
It may be left unconditional only if it is a **contract** that callers and tests verify with metrics
off, and it must then be listed in the table in §1 and asserted in `MetricsGatingTests`, so the
exception cannot accumulate silently.

`MetricsGatingTests` pins the whole arrangement: with metrics off `TotalAcquired` advances while
`TotalCreated`, `TotalReleased` and `TotalMissed` stay at 0; with metrics on all four advance; with
diagnostics off all four stay at 0 and no per-operation trace reaches the logger; and the
leak-detection and allocation-tracking counters follow their own gates rather than the metrics
switch.

## 5. Corrections applied

`ThreadFuzzTests` (future and legacy mirrors) carried the comment
"TotalAcquired/TotalReleased counts are gated by metrics, so assertions need it enabled".
Only `TotalReleased` is gated; the comment has been corrected, and the contract is now
asserted rather than described.

## See also

* [`docs/hot-path-costs.md`](hot-path-costs.md) — what each switch costs on the borrow and
  return paths, including the counter writes covered here.
* [`docs/BREAKING-CHANGES.md`](BREAKING-CHANGES.md) — the lean path keeps no cumulative
  counters at all, which is the extreme case of this gating: it forces `EnableDiagnostics` off.
