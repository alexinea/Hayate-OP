# HayateOP.Samples.Presets

> Scenario-oriented configuration via `HayatePoolPresets` / `WithPreset` — start from a tuned profile, then override specific knobs.

## What this sample demonstrates

- Iterating the `HayatePoolPreset` enum and building options with `HayatePoolPresets.Create(preset)`.
- Folding a preset into a fluent builder with `WithPreset(preset)` and then overriding individual options (e.g. `WithMaxSize`).
- Printing the resolved option matrix (min/max, sharding, validation, eviction, leak detection, metrics, reject policy) for every preset so you can compare them at a glance.

## When to use this pattern

Use a preset as a sane, battle-tested starting point instead of hand-tuning every knob. Override only the few options your workload actually needs to differ — keeping the rest at their scenario-optimized defaults.

## How to run

```bash
dotnet run --project samples/HayateOP.Samples.Presets
```

> Requires .NET 10. The app prints a step-by-step walkthrough and exits 0 on success.

## Walkthrough

| Step | Concept | Key API |
|------|---------|---------|
| loop | Build options directly from each preset | `HayatePoolPresets.Create(preset)` |
| loop | Fold preset into a builder + override | `new HayatePoolBuilder<Resource>().WithPreset(preset).WithMaxSize(16)` |
| loop | Print the resolved option matrix | `pool.GetOptions()` |

## Key APIs used

| API | Purpose |
|-----|---------|
| `HayatePoolPresets.Create` | Produce a `HayatePoolOptions` for a preset. |
| `HayatePoolBuilder<T>.WithPreset` | Apply a preset, then chain further `With*` overrides. |
| `HayatePoolPreset` | Enum of built-in scenario profiles (Default, Lean, Full, HighThroughput, LowLatency, MemoryConstrained, ConnectionPool, BatchProcessing, Deterministic). |

## Notes & gotchas

- A preset seeds defaults; any `With*` call after `WithPreset` wins, so put the preset first and your overrides after.
- The printed matrix reflects `HayatePoolOptions` (e.g. `MinPoolSize`, `RejectPolicy`), not the builder method names — keep the two namespaces distinct when reading it.
