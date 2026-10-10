using DotNetCore.HayateOP;
using DotNetCore.HayateOP.Samples.Keyed;

Console.OutputEncoding = System.Text.Encoding.UTF8;
Console.WriteLine("=== HayateOP sample: keyed pool (one sub-pool per tenant) ===");

// A keyed pool gives every key its OWN sub-pool: capacity, eviction clock, circuit breaker and
// auto-scaling are all per key, so tenant A's connections never leak to tenant B. Every sub-pool is a
// full HayateOP pool, so it shards and scales internally exactly like the single pools above.
var pools = new ParameterizedHayatePool<string, Connection>(
    cs => new Connection { ConnectionString = cs },
    maxSizePerKey: 4);

// --- 1) Isolation: three tenants each borrow, the pool tracks them separately ---
foreach (var tenant in new[] { "tenant-a", "tenant-b", "tenant-c" })
{
    for (int i = 0; i < 4; i++)
    {
        var conn = pools.GetObject(tenant);
        conn.IsOpen = true;
        pools.ReturnObject(tenant, conn);
    }
}
Console.WriteLine($"=== 1. Keys in pool: {pools.KeysInPoolCount} (tenant-a/b/c each got a sub-pool) ===");
Console.WriteLine("   (each sub-pool lazily reuses one object until concurrency demands more -- see section 2)");
foreach (var tenant in pools.Keys)
{
    var s = pools.GetPool(tenant).GetStats();
    Console.WriteLine($"   {tenant}: CurrentSize={s.CurrentSize}, PooledCount={s.PooledCount}");
}

// --- 2) A busy tenant grows only its own sub-pool ---
var busy = new List<Connection>();
for (int i = 0; i < 4; i++) busy.Add(pools.GetObject("tenant-a"));
var aStats = pools.GetPool("tenant-a").GetStats();
var bStats = pools.GetPool("tenant-b").GetStats();
Console.WriteLine($"=== 2. tenant-a holds 4 -> PooledCount={aStats.PooledCount} (all borrowed); " +
                  $"tenant-b PooledCount={bStats.PooledCount} (untouched, idle) ===");
foreach (var c in busy) pools.ReturnObject("tenant-a", c);

// --- 3) Retire a key: drops its sub-pool and frees the slot ---
bool removed = pools.TryRemove("tenant-b");
Console.WriteLine($"=== 3. TryRemove(tenant-b) = {removed}; keys now {pools.KeysInPoolCount} ===");

// --- 4) Each sub-pool is a full pool: it still shards internally ---
var aOptions = pools.GetPool("tenant-a").GetOptions();
Console.WriteLine($"=== 4. tenant-a sub-pool shards internally: ShardCount={aOptions.ShardCount} (min(default, maxSizePerKey)) ===");

pools.Dispose();
Console.WriteLine("done.");
