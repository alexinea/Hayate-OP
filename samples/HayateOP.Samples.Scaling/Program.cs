using DotNetCore.HayateOP;
using DotNetCore.HayateOP.Samples.Scaling;

Console.OutputEncoding = System.Text.Encoding.UTF8;
Console.WriteLine("=== HayateOP sample: dynamic auto-scaling ===");

var metrics = new ScalingCollector();

// Auto-scaling knobs (all on HayatePoolOptions, wired through the builder):
//   ScaleUpThreshold   - utilization above this grows the pool
//   ScaleDownThreshold - utilization below this shrinks it (toward MinPoolSize)
//   ScaleUpStep/ScaleDownStep - objects added/removed per adjustment
//   *CooldownSeconds   - minimum gap between adjustments (anti-thrash)
//   ScalingInterval    - background maintenance cadence that drives scale-DOWN
var pool = new HayatePoolBuilder<DbConnection>()
    .WithMinSize(2)
    .WithMaxSize(24)
    .WithEnableAutoScaling(true)
    .WithScaleUpThreshold(0.5)
    .WithScaleDownThreshold(0.2)
    .WithScaleUpStep(4)
    .WithScaleDownStep(4)
    .WithScaleUpCooldownSeconds(0)
    .WithScaleDownCooldownSeconds(1)
    .WithScalingInterval(300)            // background tick every 300ms (scale-DOWN lives here)
    .WithRejectPolicy(HayatePoolRejectPolicy.CreateOnDemand)
    .WithEnableMetrics(true)            // required: a custom IHayateMetrics only receives events when metrics are enabled
    .WithMetrics(metrics)
    .Build();

// --- 1) Deterministic warm-up growth: PreWarm forces the pool up to MinPoolSize ---
var minSize = pool.GetOptions().MinPoolSize;
pool.PreWarm(minSize);
Console.WriteLine($"=== 1. PreWarm grew CurrentSize to {pool.GetStats().CurrentSize} (MinPoolSize={minSize}) ===");

// --- 2) Demand-driven scale-UP: borrows create objects on demand up to MaxPoolSize; the auto-scaler trims them back ---
var held = new List<DbConnection>();
for (int i = 0; i < 20; i++) held.Add(pool.Acquire());
var upSize = pool.GetStats().CurrentSize;
Console.WriteLine($"=== 2. Borrowed {held.Count} (util {held.Count}/24 = {held.Count / 24.0:P0}) -> CurrentSize={upSize} ===");
Console.WriteLine($"   (borrows grew the pool on demand; the auto-scaler records scale-DOWN as load falls)");
Console.WriteLine($"   scaling events so far: {string.Join(", ", metrics.ScaledEvents)}");

// --- 3) Scale-DOWN: return everything, let the background tick shrink toward MinPoolSize ---
foreach (var c in held) pool.Release(c);
held.Clear();
Console.WriteLine("=== 3. Released all; waiting for background scale-DOWN (cooldown 1s) ===");
await Task.Delay(6500);
var downSize = pool.GetStats().CurrentSize;
Console.WriteLine($"   CurrentSize after wait = {downSize} (dropped from {upSize}, toward MinPoolSize={minSize})");
Console.WriteLine($"   scaling events total: {string.Join(", ", metrics.ScaledEvents)}");

// --- 4) Runtime resize: change the ceiling without rebuilding the pool ---
pool.ReloadConfig(o => o.MaxPoolSize = 48);
Console.WriteLine($"=== 4. ReloadConfig raised MaxPoolSize to {pool.GetOptions().MaxPoolSize} (no rebuild) ===");

pool.Dispose();
Console.WriteLine("done.");
