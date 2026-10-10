# HayateOP.Samples.Sharding

> Internal sharding — capacity is split evenly across shards, and three affinity modes decide where each borrow's scan starts.

## What this sample demonstrates

- `WithShardCount(n)` (1..32) divides the configured `MaxPoolSize` into `MaxPoolSize / ShardCount` idle slots per shard, so each shard's free-list lock is independent and concurrent borrows contend on different shards.
- A `PreWarm(MaxPoolSize)` followed by `TakeSnapshot()` tallying idle objects per shard via `ObjectDetails[].ShardIndex`, proving the even capacity split.
- The three `HayateShardAffinityMode` values, which pick only the *starting* shard of the borrow scan (the pool then rings around for availability):
  - `None` — sequential from shard 0 (default, zero overhead).
  - `Thread` — stable per managed-thread mapping (`Environment.CurrentManagedThreadId % ShardCount`), aiding cache/thread-local reuse.
  - `Custom` — your `Func<int>` delegate picks the start (use `WithCustomShardAffinity`, not `WithShardAffinity(Custom)`); out-of-range/null falls back to sequential scan.

## When to use this pattern

Use sharding when a single global free-list lock becomes a bottleneck under many concurrent borrows. It reduces lock contention without changing any borrow/return semantics.

## How to run

```bash
dotnet run --project samples/HayateOP.Samples.Sharding
```

> Requires .NET 10. The app prints a step-by-step walkthrough and exits 0 on success.

## Walkthrough

| Step | Concept | Key API |
|------|---------|---------|
| 1 | Capacity split across shards | `WithShardCount` + `PreWarm` + `TakeSnapshot().ObjectDetails[].ShardIndex` |
| 2 | Affinity modes (scan start) | `WithShardAffinity(None/Thread)` / `WithCustomShardAffinity(() => 2)` |

## Key APIs used

| API | Purpose |
|-----|---------|
| `HayatePoolBuilder<T>.WithShardCount` | Number of shards (1..32); divides capacity. |
| `HayatePoolBuilder<T>.WithEnableSharding` | Master switch (default true). |
| `HayatePoolBuilder<T>.WithShardAffinity` / `WithCustomShardAffinity` | `None` / `Thread` / `Custom` start-shard policy. |
| `HayatePoolSnapshot.ObjectDetails[].ShardIndex` | Per-object shard location for diagnostics. |

## Notes & gotchas

- Shard capacity = `MaxPoolSize / ShardCount` (with remainder distributed); a pool with `MaxPoolSize < ShardCount` silently fails to honor the ceiling — keep `MaxPoolSize >= ShardCount`.
- Affinity only chooses where the scan *starts*; after that the pool scans in a ring, so no mode ever sacrifices availability for locality.
