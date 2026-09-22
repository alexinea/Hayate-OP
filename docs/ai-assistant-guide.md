# HayateOP for AI coding assistants

This page exists for one workflow: a developer asks an AI assistant to "add object pooling with
HayateOP" and the assistant has to get the integration right without reading the whole repository.
It is deliberately a **single file** — the shape decision, the skeleton, the traps and the facts that
are easy to get wrong — with links out to the long-form pages.

If you are a human reading this, it is also the fastest orientation to the library.

## 1. Pick the shape first

Almost every mistake starts with the wrong pool shape. Ask these in order:

| Question | Answer | Use |
| :--- | :--- | :--- |
| Does the resource handle concurrency itself (a lock, a `SemaphoreSlim`)? | yes | **Not a pool** — use striped lookup. See [`docs/hot-path-costs.md`](hot-path-costs.md#the-third-option-striped-lookup) |
| Is creation more expensive than a round trip? | no | **Not a pool** — plain `new` is faster. See [`docs/hot-path-costs.md`](hot-path-costs.md#5-when-pooling-is-the-wrong-tool) |
| Must the resource be **exclusive** for the lease? | yes | A borrow/return pool — keep reading |
| Is the element a `StringBuilder` / `MemoryStream`? | yes | The specialized package's pools (`StringBuilderPool`, `MemoryStreamPool`) |
| Is it a `T[]` of a few known sizes? | yes | `HayateBufferPool<T>` (rounds up to the nearest bucket) |
| Does a borrow ever need an async connect / handshake? | yes | `HayatePreparationPool<T>` |
| Do you need a ceiling on concurrent leases? | yes | The bounded general pool |
| No ceiling needed? | — | `HayateUnboundedPool<T>` (bounds retention, not leases) |
| Is it a small stateless object on a measured hot path? | yes | Add `.UseLeanProfile()` — and read §4 below first |

The first three rows are the ones that can rule pooling out; ask them before the rest, because a
"which pool?" answer to a "should I pool?" question is the most common way an integration goes wrong.

## 2. The canonical skeleton

```csharp
using var pool = new HayatePoolBuilder<MyResource>()
    .WithPoolName("my-resource")
    .WithMinSize(4)
    .WithMaxSize(32)
    .Build();

var resource = pool.Acquire();
try
{
    // use it — only inside this block
}
finally
{
    pool.Release(resource);
}
```

Or let the library own the `finally`:

```csharp
using var lease = pool.AcquireScoped();
lease.Value.DoWork();
```

`AcquireScoped()` costs one small allocation per call (the lease is a class); the plain
`Acquire`/`Release` pair does not. Both are correct; pick by whether the call site is hot.

**Returning the object transfers ownership.** Do not touch it afterwards, do not return it twice, do
not return it to a different pool. The engine checks this on the general path and does **not** check
it on the lean path — the full table of what is and is not detected is in
[`docs/ownership.md`](ownership.md).

## 3. Five things not to do

| Do not | Why |
| :--- | :--- |
| `pool.Release(obj)` without a `try`/`finally` | An exception on the use path leaks the lease; the pool cannot see it |
| Use the object after `Release` | It may already be in another thread's hands. Nothing detects this |
| Dispose each pooled object before returning it | Not required — disposing the pool releases everything it holds. Doing it only costs the next borrow a re-creation |
| Use `.UseLeanProfile()` when you want misuse detected | The lean path keeps no registry, so a foreign object is **accepted** and a double return is not caught |
| Read `MaxPoolSize` as "0 means unlimited" | It means the opposite here. See §4 |

## 4. Facts that are easy to get wrong

Each of these was checked against the source; several contradict older documentation and blog-shaped
intuition.

| Fact | Detail |
| :--- | :--- |
| `MaxPoolSize = 0` turns the pool **off** | It disables cold-boot creation and the derived per-shard capacity is 0, so nothing is created and nothing is retained: every borrow ends in a timeout, a rejection or an indefinite wait. It is accepted only with `MinPoolSize = 0`. Some third-party pools read `0` as "no limit" — this is the opposite |
| `AvailableSlots` == `PooledCount` | Both report the current idle count. `AvailableSlots` is not "how many more I may borrow" |
| `TotalDestroyed` is always `0` | Nothing writes it. Do not use it to detect disposal |
| `TotalCreated` is only counted when metrics are on | With `EnableMetrics = false` it reports 0 even though objects were created. `TotalAcquired` is the one counter that is written unconditionally |
| `AcquireAsync(ct)` does not read `DefaultAcquireTimeout` | The default timeout applies to the parameterless synchronous `Acquire()` only. Pass an explicit `TimeSpan` to the async overload |
| The async path does not read the reject policy | `RejectPolicy` shapes the synchronous miss; do not expect `CreateNew` to change async behaviour |
| Default `ShardCount` is 4 | Sharding is on by default |
| `HayatePoolFactory.Create` overwrites a same-named registration and **leaks the old pool** | Use `GetOrCreatePool` when a pool may already exist |
| `IHayateObjectPoolRegistry.Remove` does **not** dispose | Only `HayateSharedPoolRegistry.Remove<T>` disposes what it removes |
| `ReloadConfig` cannot change the feature switches, `ShardCount` or `SoftCapacity` | Changing `SoftCapacity` fails the whole reload. `MaxLifeTime` and `GenerationThresholdMs` can be changed |
| `WithCustomShardAffinity` takes a `Func<int>` | Return an out-of-range value (`-1`) for "no affinity" — that falls back to the sequential scan |
| `ValidateOnReturn` / `ValidateWhileIdle` **are** wired | Older comments claimed they were not |
| The core package has **no dependencies** | Adding `Microsoft.Extensions.Logging` or Serilog to it is rejected by design; third-party loggers are bridged with `HayateMicrosoftLoggerAdapter<T>` |
| `EnableDiagnostics` is the master switch | It gates counters, timing, the metrics sink and the trace together. `EnableLean` implies it off |

## 5. Where the numbers are

* [`docs/hot-path-costs.md`](hot-path-costs.md) — the measured ladder and, for every switch, **where**
  its cost lands. Read §5 before deciding to pool a cheap object at all.
* [`docs/metrics-gating.md`](metrics-gating.md) — which counters survive which switch. This is the
  page that explains why a statistics object can read 0 without anything being wrong.
* [`docs/benchmarks/baseline/baseline.json`](benchmarks/baseline/baseline.json) — the machine-readable
  baseline the regression gate compares against.

## 6. Where to read next

| You need | Read |
| :--- | :--- |
| The full option surface | the usage guide's options reference |
| The return contract in one place | [`docs/ownership.md`](ownership.md) |
| Who disposes what | [`docs/disposal.md`](disposal.md) |
| Async borrow semantics | [`docs/async-policy.md`](async-policy.md) |
| Behaviour changes between versions | [`docs/BREAKING-CHANGES.md`](BREAKING-CHANGES.md), then `CHANGELOG.md` |
| A runnable example | the `reports/examples/` case set (repository-only) |

> The `reports/` directory is a local knowledge base and is not part of the published package. Anything
> an integration must be able to rely on lives in `docs/`, `README.md` or the XML documentation.
