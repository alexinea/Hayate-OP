# HayateOP.Samples.Scaling

> Dynamic auto-scaling — the pool grows on demand and trims itself back as load falls, with runtime hot-resize and a custom metrics collector.

## What this sample demonstrates

- The auto-scaling knobs: `WithScaleUpThreshold` / `WithScaleDownThreshold` (utilization triggers), `WithScaleUpStep` / `WithScaleDownStep` (objects per adjustment), `*CooldownSeconds` (anti-thrash gap), and `WithScalingInterval` (background maintenance cadence that drives scale-DOWN).
- Deterministic warm-up: `PreWarm(MinPoolSize)` forces the pool up to its floor before any load.
- Demand-driven scale-UP: with `CreateOnDemand`, borrows create objects up to `MaxPoolSize`; `CurrentSize` climbs to meet demand.
- Scale-DOWN: releasing everything drops utilization below `ScaleDownThreshold`; the background tick evicts idle objects in `ScaleDownStep` chunks (honoring `ScaleDownCooldownSeconds`), recorded by a custom `IHayateMetrics`.
- Runtime resize: `ReloadConfig(o => o.MaxPoolSize = 48)` raises the ceiling with no rebuild.

## When to use this pattern

Use when workload varies over time and you want the pool to track demand automatically — expanding under bursts and contracting during quiet periods to free memory — while still being able to react to traffic shifts at runtime.

## How to run

```bash
dotnet run --project samples/HayateOP.Samples.Scaling
```

> Requires .NET 10. The app prints a step-by-step walkthrough and exits 0 on success.

## Walkthrough

| Step | Concept | Key API |
|------|---------|---------|
| 1 | Deterministic warm-up to `MinPoolSize` | `pool.PreWarm(minSize)` |
| 2 | Demand-driven scale-UP | borrow 20 → `CurrentSize` grows to `MaxPoolSize` |
| 3 | Background scale-DOWN | release all + wait `ScalingInterval` ticks |
| 4 | Runtime resize | `pool.ReloadConfig(o => o.MaxPoolSize = 48)` |

## Key APIs used

| API | Purpose |
|-----|---------|
| `WithEnableAutoScaling` + scaling knobs | Threshold/step/cooldown/interval tuning. |
| `WithScalingInterval` | Background tick that performs scale-DOWN. |
| `PreWarm` / `CurrentSize` | Warm-up and live size. |
| `WithMetrics` + custom `IHayateMetrics` | Capture `RecordPoolScaled` events (`ScalingCollector`). |
| `ReloadConfig` | Hot-resize `MaxPoolSize` at runtime. |

## Notes & gotchas

- A custom `IHayateMetrics` is only invoked when metrics are enabled, so this sample pairs `WithMetrics(metrics)` with `WithEnableMetrics(true)` — omitting the latter throws at registration.
- Scale-DOWN lives in the background maintenance tick, so the demo waits (`Task.Delay`) for it to run; in production, tune `ScalingInterval` / `ScaleDownCooldownSeconds` to your traffic shape.
