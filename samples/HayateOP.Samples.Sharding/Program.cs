using DotNetCore.HayateOP;
using DotNetCore.HayateOP.Samples.Sharding;

// This is a top-level Statements file, so `Program` lives in the global namespace. The `using` directives
// below are what bring the pool builder, the affinity enum and the local `PooledItem` type into scope.
Console.OutputEncoding = System.Text.Encoding.UTF8;
Console.WriteLine("=== HayateOP sample: sharding ===");

// --- 1) Shard capacity is split evenly across shards ---
// Demonstrates the core sharding invariant: `WithShardCount(n)` divides the configured `MaxPoolSize`
// into `MaxPoolSize / ShardCount` idle slots per shard. Splitting the capacity keeps each shard's
// free-list lock independent, so concurrent borrows contend on different shards instead of one global lock.
// With MaxPoolSize = 16 and 4 shards, each shard may hold at most 16/4 = 4 idle objects.
const int shardCount = 4;
const int maxSize = 16;
var pool = new HayatePoolBuilder<PooledItem>()
    .WithMaxSize(maxSize)
    .WithShardCount(shardCount)        // 1..32; divides capacity across shards
    .WithEnableSharding(true)          // default true; set false to collapse to a single elastic shard
    .WithRejectPolicy(HayatePoolRejectPolicy.CreateOnDemand)
    .Build();

pool.PreWarm(maxSize);                // create maxSize objects, distributed across shards
var snapshot = pool.TakeSnapshot();
// Tally how many idle objects each shard currently owns by reading the per-object detail captured in the snapshot.
int[] perShard = new int[shardCount];
foreach (var d in snapshot.ObjectDetails)
    if (!d.IsBorrowed && d.ShardIndex >= 0 && d.ShardIndex < shardCount)
        perShard[d.ShardIndex]++;
Console.WriteLine($"=== 1. Capacity split: MaxPoolSize={maxSize}, ShardCount={shardCount} (per-shard max = {maxSize / shardCount}) ===");
for (int i = 0; i < shardCount; i++)
    Console.WriteLine($"   shard {i}: {perShard[i]} idle objects");
Console.WriteLine($"   total idle = {perShard.Sum()} (equals MaxPoolSize)");

pool.Dispose();

// --- 2) Affinity modes decide where a borrow SCAN STARTS (it still rings around for availability) ---
// Explains the three `HayateShardAffinityMode` values. Affinity only picks the *starting* shard for the
// borrow scan; after that the pool continues scanning shards in a ring, so no mode ever sacrifices
// availability for locality. They change hit priority/cache locality, not correctness.
Console.WriteLine($"=== 2. Shard-affinity modes (where the borrow scan starts) ===");

// None: sequential scan from shard 0 (default, zero overhead).
var none = new HayatePoolBuilder<PooledItem>()
    .WithMaxSize(8).WithShardCount(shardCount)
    .WithShardAffinity(HayateShardAffinityMode.None)
    .WithRejectPolicy(HayatePoolRejectPolicy.CreateOnDemand).Build();
var firstNone = none.Acquire();
var noneSnap = none.TakeSnapshot();
Console.WriteLine($"   None   -> first borrowed object lives on shard {noneSnap.ObjectDetails.First(d => d.IsBorrowed).ShardIndex} (sequential from 0)");
none.Release(firstNone); none.Dispose();

// Thread: stable per managed-thread mapping, helps CPU-cache / thread-local reuse.
var thread = new HayatePoolBuilder<PooledItem>()
    .WithMaxSize(8).WithShardCount(shardCount)
    .WithShardAffinity(HayateShardAffinityMode.Thread)
    .WithRejectPolicy(HayatePoolRejectPolicy.CreateOnDemand).Build();
var firstThread = thread.Acquire();
var threadSnap = thread.TakeSnapshot();
Console.WriteLine($"   Thread -> first borrowed object starts on shard {threadSnap.ObjectDetails.First(d => d.IsBorrowed).ShardIndex} (Environment.CurrentManagedThreadId % {shardCount})");
thread.Release(firstThread); thread.Dispose();

// Custom: your delegate picks the starting shard; out-of-range/null falls back to sequential scan.
// Use `WithCustomShardAffinity` (not `WithShardAffinity(Custom)`) to supply the selector delegate.
// If the delegate returns null or an out-of-range value the borrow falls back to sequential scanning
// rather than throwing, preserving borrow-path robustness.
var custom = new HayatePoolBuilder<PooledItem>()
    .WithMaxSize(8).WithShardCount(shardCount)
    .WithCustomShardAffinity(() => 2)   // always start scanning from shard 2
    .WithRejectPolicy(HayatePoolRejectPolicy.CreateOnDemand).Build();
var firstCustom = custom.Acquire();
var customSnap = custom.TakeSnapshot();
Console.WriteLine($"   Custom -> first borrowed object starts on shard {customSnap.ObjectDetails.First(d => d.IsBorrowed).ShardIndex} (delegate returned 2)");
custom.Release(firstCustom); custom.Dispose();

Console.WriteLine("done.");
