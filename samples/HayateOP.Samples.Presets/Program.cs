// HayateOP sample: scenario-oriented configuration via HayatePoolPresets / WithPreset
// Build & run:  dotnet run --project samples/HayateOP.Samples.Presets
using System;
using DotNetCore.HayateOP;

using DotNetCore.HayateOP.Samples.Presets;

Console.OutputEncoding = System.Text.Encoding.UTF8;

foreach (HayatePoolPreset preset in Enum.GetValues<HayatePoolPreset>())
{
    // Either build options directly...
    var options = HayatePoolPresets.Create(preset);
    // ...or fold the preset into a fluent builder and override specific knobs:
    //   new HayatePoolBuilder<Resource>().WithPreset(preset).WithMaxSize(32).Build();

    using var pool = new HayatePoolBuilder<Resource>()
        .WithPreset(preset)
        .WithMinSize(2)
        .WithMaxSize(16)
        .Build();

    Console.WriteLine($"=== {preset} ===");
    Console.WriteLine($"   Min={options.MinPoolSize}, Max={options.MaxPoolSize}, " +
                      $"Sharding={options.EnableSharding}, Validation={options.EnableValidation}, " +
                      $"Eviction={options.EnableEviction}, LeakDetection={options.EnableLeakDetection}, " +
                      $"Metrics={options.EnableMetrics}, Reject={options.RejectPolicy}");

    var obj = pool.Acquire();
    pool.Release(obj);
}

Console.WriteLine();
Console.WriteLine("Presets sample completed.");
