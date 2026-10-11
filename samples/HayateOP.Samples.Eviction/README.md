# HayateOP.Samples.Eviction

> **Eviction Policy** — replace the built-in idle-eviction rule with your own `IHayateEvictionPolicy<T>`,
> installed via `WithEvictionPolicy`. The background maintenance run consults it; idle objects past your
> rule are destroyed.

## What this sample demonstrates

Two pools:

- **Default policy** — `Evict(HayateEvictReason.Idle)` uses the built-in idle-too-long rule (objects past
  `MaxIdleTime` are removed). The sample waits past `MaxIdleTime` and evicts them on demand.
- **Custom policy** — `RetireHeavilyUsedConnections` retires a `Connection` once it has been leased at least
  `N` times (a "wear-out" rule). It is installed with `WithEvictionPolicy` and fired by the background run
  (sped up with `WithEvictionInterval(150)`). After the run, the worn-out idle objects are gone.

## When to use this pattern

- You want eviction driven by something other than age/idle time — lease count, a probe result, a business
  state on the pooled object, a shard's idle count vs. its minimum, etc.
- You want the rule to be the *whole* rule for the run (the built-in rule is fully replaced, not added to).

## How to run

```bash
dotnet run --project samples/HayateOP.Samples.Eviction
```

## Walkthrough

| Step | Concept | Key API |
| --- | --- | --- |
| Wear objects out | borrow/return to climb `LeaseCount` | `Acquire()` / `Release(T)` |
| Default idle evict | built-in `MaxIdleTime` rule | `Evict(HayateEvictReason.Idle)` |
| Install custom rule | replaces the built-in rule | `WithEvictionPolicy(IHayateEvictionPolicy<T>)` |
| Speed up the run | background scan fires often | `WithEvictionInterval(150)` |
| Observe retirement | idle count drops after the run | `GetStats().PooledCount` |

## Key APIs used

- `HayatePoolBuilder<T>.WithEvictionPolicy(IHayateEvictionPolicy<T>)`
- `HayatePoolBuilder<T>.WithEnableEviction(bool)` / `WithEvictionInterval(int)`
- `IHayateEvictionPolicy<T>.ShouldEvict(in HayateEvictionCandidate<T>)`
- `HayateEvictionCandidate<T>` (`.LeaseCount`, `.Age`, `.IdleTime`, `.Item`, …)
- `IHayateObjectPool<T>.Evict(HayateEvictReason)`

## Notes & gotchas

- `ShouldEvict` is consulted **only by the background run**. A manual `Evict(HayateEvictReason)` uses the
  built-in Touched/Idle/Expired rules, not your policy — so to *see* a custom rule fire, let the background
  run execute (or lower `WithEvictionInterval`).
- Only **idle** objects are offered; a borrowed object never reaches the policy, and no verdict can destroy
  one mid-use (the shard re-checks ownership under a CAS).
- The policy runs on the maintenance timer: keep it allocation-free and fast, and make it thread-safe
  (it is called concurrently from the shards). An exception is caught and logged like any background failure.
- A policy that always returns `false` is a supported configuration: idle eviction is simply off (abandoned
  recovery and explicit `Evict` still work).
