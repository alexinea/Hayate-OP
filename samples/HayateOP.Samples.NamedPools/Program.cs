// HayateOP sample: multiple named pools of the same type via DI
// Build & run:  dotnet run --project samples/HayateOP.Samples.NamedPools
using System;
using DotNetCore.HayateOP;
using Microsoft.Extensions.DependencyInjection;

using DotNetCore.HayateOP.Samples.NamedPools;

Console.OutputEncoding = System.Text.Encoding.UTF8;

var services = new ServiceCollection();
services.AddHayatePoolSupport()
        .AddNamedPool<DbConnection>("primary", o => o.MaxPoolSize = 64)
        .AddNamedPool<DbConnection>("replica", o => o.MaxPoolSize = 16)
        .RegisterHayatePool<DbConnection>(o => o.MaxPoolSize = 32); // unnamed pool

using var sp = services.BuildServiceProvider();
var accessor = sp.GetRequiredService<IHayateNamedPoolAccessor>();

Console.WriteLine("=== 1. Named pools coexist per name ===");
var primary = accessor.GetPool<DbConnection>("primary");
var replica = accessor.GetPool<DbConnection>("replica");
Console.WriteLine($"   primary max={primary.GetOptions().MaxPoolSize}");
Console.WriteLine($"   replica max={replica.GetOptions().MaxPoolSize}");
Console.WriteLine($"   registered names: {string.Join(", ", accessor.GetPoolNames<DbConnection>())}");

Console.WriteLine("=== 2. Borrow from a named pool ===");
var conn = primary.Acquire();
try { Console.WriteLine($"   borrowed from primary -> {conn}"); }
finally { primary.Release(conn); }

Console.WriteLine("=== 3. The unnamed pool resolves as IHayateObjectPool<T> ===");
var unnamed = sp.GetRequiredService<IHayateObjectPool<DbConnection>>();
var c2 = unnamed.Acquire();
try { Console.WriteLine($"   borrowed from unnamed -> {c2} (max={unnamed.GetOptions().MaxPoolSize})"); }
finally { unnamed.Release(c2); }
