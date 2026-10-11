# HayateOP.Samples.BorrowOrder

> **Borrow Order** — choose whether the pool hands out the *oldest* returned object (`Fifo`, the
> default) or the *most recently* returned one (`Lifo`) via `HayateBorrowStrategy`, set with
> `WithBorrowStrategy`.

## What this sample demonstrates

HayateOP serves borrows from an ordered idle list. `Fifo` (first-in-first-out) returns the object that
has been idle the longest; `Lifo` (last-in-first-out) returns the one returned most recently. The sample
borrows three distinct `Widget`s, returns them in order, then borrows one more and prints its identity to
show which end of the list was served.

## When to use this pattern

- **`Fifo`** (default): best when objects are interchangeable and you want the longest-idle object reused
  first — spreads wear evenly and keeps every object warm.
- **`Lifo`**: best when the most-recently-used object is also the "hottest" (e.g. a cached connection
  whose buffers/state are still warm in cache), at the cost of letting older objects sit idle longer.

## How to run

```bash
dotnet run --project samples/HayateOP.Samples.BorrowOrder
```

## Walkthrough

| Step | Concept | Key API |
| --- | --- | --- |
| Build a single-shard pool | one ordered idle list ⇒ deterministic order | `WithEnableSharding(false)` |
| Select the strategy | `Fifo` or `Lifo` hand-out order | `WithBorrowStrategy(HayateBorrowStrategy)` |
| Borrow / return 3 objects | fills the idle list head→tail | `Acquire()` / `Release(T)` |
| Borrow one more | observe which end was served | `Acquire()` |

## Key APIs used

- `HayatePoolBuilder<T>.WithBorrowStrategy(HayateBorrowStrategy)`
- `HayateBorrowStrategy.Fifo` / `HayateBorrowStrategy.Lifo`
- `IHayateObjectPool<T>.Acquire()` / `Release(T)`

## Notes & gotchas

- Borrow order is meaningful only on the **general-purpose** engine. The *lean* fast path keeps no ordered
  idle list, so `Lifo` is effectively a no-op there (it still serves, just without the recency guarantee).
- With **sharding enabled**, the order is decided per shard by shard affinity, so a multi-shard pool does
  not present a single global Fifo/Lifo sequence — disable sharding (as this sample does) when the exact
  order matters.
- `Fifo`/`Lifo` only affects *which idle object* is chosen; it never changes creation, validation, or
  eviction behaviour.
