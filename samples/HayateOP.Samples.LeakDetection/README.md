# HayateOP.Samples.LeakDetection

> **Leak Detection** — with `WithEnableLeakDetection(true)` the pool tracks borrowed objects against
> `WithLeakDetectionThreshold`. An object borrowed past the threshold without being returned is counted as a
> leak. Stack-trace capture is opt-in via `WithLeakTraceCapture` (`Off` / `Sampled` / `EveryAcquire`).

## What this sample demonstrates

A pool with leak detection on and a 200 ms threshold. The sample borrows an object and **does not return it**,
then waits past the threshold. `TakeSnapshot().LeakCount` (and `GetStats().LeakDetectedCount`) is non-zero,
showing the overdue borrow was flagged. With `Sampled` trace capture (sample rate 1 = every borrow) the
captured call stack is also printed from `LeakTraces`.

## When to use this pattern

- You suspect callers drop leases (forget `Release` / `Dispose` of a scope/lease), and want the pool to
  *tell* you rather than silently lose objects.
- You want forensic evidence (who borrowed the leaked object) — opt into `Sampled` or `EveryAcquire` trace
  capture, but remember it has a real allocation cost on the borrow hot path.

## How to run

```bash
dotnet run --project samples/HayateOP.Samples.LeakDetection
```

## Walkthrough

| Step | Concept | Key API |
| --- | --- | --- |
| Enable leak detection | threshold-based flagging | `WithEnableLeakDetection(true)` |
| Set the threshold | borrow age that counts as a leak | `WithLeakDetectionThreshold(TimeSpan)` |
| Opt into trace capture | capture the borrow stack (optional) | `WithLeakTraceCapture(HayateLeakTraceCaptureMode, sampleRate)` |
| Borrow and don't return | simulate a lost lease | `Acquire()` |
| Observe the leak | count + trace | `TakeSnapshot().LeakCount` / `.LeakTraces` |

## Key APIs used

- `HayatePoolBuilder<T>.WithEnableLeakDetection(bool)`
- `HayatePoolBuilder<T>.WithLeakDetectionThreshold(TimeSpan)`
- `HayatePoolBuilder<T>.WithLeakTraceCapture(HayateLeakTraceCaptureMode, int)`
- `HayatePoolSnapshot.LeakCount` / `LeakTraces`
- `HayateLeakTraceCaptureMode` (`Off` / `Sampled` / `EveryAcquire`)

## Notes & gotchas

- Leak detection (the threshold check + `LeakCount`) relies only on the borrow timestamp and incurs **no**
  capture overhead by itself. Stack-frame capture is a separate, opt-in cost — `Off` is the default since 2.1.
- `EveryAcquire` captures the full stack on every borrow (tens of µs CPU / 10–40 KB per capture); use it only
  for diagnostics, not in production hot paths.
- Leak detection is forensic: it counts and (optionally) captures evidence, but does **not** reclaim the
  object. Pair it with Abandoned Recovery if you also want overdue borrows physically reclaimed.
