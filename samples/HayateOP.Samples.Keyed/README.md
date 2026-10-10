# HayateOP.Samples.Keyed

> A keyed pool (`ParameterizedHayatePool<TKey,TValue>`) — every key gets its own isolated sub-pool, giving per-tenant capacity, eviction, breaker, and auto-scaling.

## What this sample demonstrates

- `ParameterizedHayatePool<string, Connection>` constructed with a factory and a `maxSizePerKey`; every key gets a full, independent HayateOP sub-pool.
- Isolation: three tenants each borrow 4 times; `KeysInPoolCount` reports 3 separate sub-pools, each tracked independently.
- A busy tenant (`tenant-a`) holding 4 objects shows `PooledCount == 0` (all borrowed) while `tenant-b` stays untouched (`PooledCount == 1`) — capacity never leaks across tenants.
- Retiring a key with `TryRemove` drops its sub-pool and frees the slot.
- Each sub-pool is a complete pool: it shards internally (`ShardCount = min(default, maxSizePerKey)`).

## When to use this pattern

Use for multi-tenant or per-key resource isolation — e.g. one DB connection pool per customer, one HTTP client pool per upstream host — where each tenant must have independent capacity, eviction, circuit breaking, and scaling, and retiring a tenant must cleanly reclaim its resources.

## How to run

```bash
dotnet run --project samples/HayateOP.Samples.Keyed
```

> Requires .NET 10. The app prints a step-by-step walkthrough and exits 0 on success.

## Walkthrough

| Step | Concept | Key API |
|------|---------|---------|
| 1 | Isolation: 3 tenants, tracked separately | `pools.GetObject(tenant)` / `KeysInPoolCount` |
| 2 | A busy tenant grows only its own sub-pool | `pools.GetPool("tenant-a").GetStats()` |
| 3 | Retire a key | `pools.TryRemove("tenant-b")` |
| 4 | Sub-pool shards internally | `pools.GetPool("tenant-a").GetOptions().ShardCount` |

## Key APIs used

| API | Purpose |
|-----|---------|
| `ParameterizedHayatePool<TKey,TValue>(factory, maxSizePerKey)` | One sub-pool per key. |
| `GetObject` / `ReturnObject` | Borrow / return by key. |
| `KeysInPoolCount` / `Keys` / `GetPool(key)` | Enumerate and inspect sub-pools. |
| `TryRemove` | Retire a key and free its sub-pool. |

## Notes & gotchas

- `ParameterizedHayatePool<TKey,TValue>` ships in the core `HayateOP` assembly (no DI package required), and `TValue` must be a reference type (`class`).
- Each sub-pool defaults to `RejectPolicy = CreateOnDemand` and `MinPoolSize = 0`, so it lazily creates and reuses a single object until concurrency demands more — that is why a lightly-used tenant shows `PooledCount == 1` after a few borrows/returns.
