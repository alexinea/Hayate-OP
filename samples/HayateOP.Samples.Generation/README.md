# HayateOP.Samples.Generation

> **Generation Optimization** — long-lived objects are promoted to the *old* generation once they pass
> `WithGenerationThreshold` of age; the old generation then skips validation on most borrows (governed by
> `WithOldGenerationValidationInterval`), reducing validation frequency for objects that are clearly healthy.

## What this sample demonstrates

With generation optimization on (`WithEnableGenerationOptimization(true)`), a single object is held borrowed
past the threshold, returned, then borrowed again — on that second borrow the engine promotes it to the old
generation. `TakeSnapshot().ObjectDetails[].Generation` shows `1` (old) instead of `0` (young), proving the
promotion. The old generation now validates only every `OldGenerationValidationInterval`-th borrow.

## When to use this pattern

- You validate objects (on borrow/return/idle) and want to stop re-validating the same long-lived, clearly
  healthy objects on every single borrow — a cheap way to cut validation cost on hot, stable objects.
- You have objects whose validity is expensive to check (a ping, a handshake) and want to amortize it.

## How to run

```bash
dotnet run --project samples/HayateOP.Samples.Generation
```

## Walkthrough

| Step | Concept | Key API |
| --- | --- | --- |
| Enable validation | baseline the object check | `WithEnableValidation(true)` / `WithValidateInterval(50)` |
| Enable generation optimization | promote old objects | `WithEnableGenerationOptimization(true)` |
| Set promotion age | age at which an object becomes "old" | `WithGenerationThreshold(150)` |
| Set old-gen skip | validate 1 of every N borrows | `WithOldGenerationValidationInterval(5)` |
| Hold past threshold, re-borrow | triggers promotion | `Acquire()` / `Release(T)` |
| Observe generation | 1 = old | `TakeSnapshot().ObjectDetails[].Generation` |

## Key APIs used

- `HayatePoolBuilder<T>.WithEnableGenerationOptimization(bool)`
- `HayatePoolBuilder<T>.WithGenerationThreshold(int)` (ms)
- `HayatePoolBuilder<T>.WithOldGenerationValidationInterval(int)`
- `IHayateObjectPool<T>.TakeSnapshot()` → `HayatePoolObjectDetail.Generation`

## Notes & gotchas

- Generation optimization only affects **validation frequency** — it never changes creation, eviction, or
  leak semantics. Validation must be enabled for it to matter.
- Promotion is age-based: an object is promoted on borrow once its age exceeds `GenerationThresholdMs`. A
  short, borrowed-then-returned lifecycle may never reach the old generation.
- The old generation still validates occasionally (every `OldGenerationValidationInterval` borrows), so a
  genuinely broken long-lived object is still caught — just less often.
- `LeaseCount`, `Generation` and the idle timestamps are tracked by the general-purpose engine; the *lean*
  fast path keeps none of this state, so generation optimization is a no-op there.
