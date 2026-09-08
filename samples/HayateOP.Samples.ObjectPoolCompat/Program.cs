// ---------------------------------------------------------------------------
// HayateOP.Extensions.ObjectPoolCompat sample
// 演示 Microsoft.Extensions.ObjectPool（MEOP）调用方「一行切换」HayateOP 后端，
// 以及 HayateCompatOptions 调优、DI 集成与反向适配（HayateObjectPoolAdapter）。
// Build & run:  dotnet run --project samples/HayateOP.Samples.ObjectPoolCompat
// ---------------------------------------------------------------------------
using System.Text;
using DotNetCore.HayateOP;
using DotNetCore.HayateOP.ObjectPoolCompat;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.ObjectPool;

Console.OutputEncoding = Encoding.UTF8;

// ---------------------------------------------------------------------------
// 0. 策略：直接复用 MEOP 内置的 StringBuilderPooledObjectPolicy（官方文档同款：
//    Create 指定初始容量；Return 清空重置、超大对象拒绝回池）。
//    自定义 IPooledObjectPolicy<T> 同样零改动可用——兼容池经
//    HayateCompatPooledObjectPolicy<T> 把 Create/Return 钩子映射到 HayateOP 的
//    Create/OnRelease 语义。
// ---------------------------------------------------------------------------
Console.WriteLine("=== 0. MEOP 风格策略（IPooledObjectPolicy<T>） ===");
var sbPolicy = new StringBuilderPooledObjectPolicy();
Console.WriteLine($"   策略：StringBuilderPooledObjectPolicy（InitialCapacity={sbPolicy.InitialCapacity}, MaximumRetainedCapacity={sbPolicy.MaximumRetainedCapacity}）");

// ---------------------------------------------------------------------------
// 1. 切换前基线：DefaultObjectPoolProvider（原生 MEOP 行为）
// ---------------------------------------------------------------------------
Console.WriteLine("=== 1. 切换前：DefaultObjectPoolProvider（MEOP 基线） ===");
DemoPool("MEOP", new DefaultObjectPoolProvider().Create(sbPolicy));

// ---------------------------------------------------------------------------
// 2. 一行切换：new DefaultObjectPoolProvider() -> new HayateObjectPoolCompatProvider()
//    Get/Return 调用方代码零改动，池后端变为 HayateOP（分片、统计、可观测等能力随包携带）。
// ---------------------------------------------------------------------------
Console.WriteLine("=== 2. 一行切换：HayateObjectPoolCompatProvider ===");
DemoPool("HayateOP", new HayateObjectPoolCompatProvider().Create(sbPolicy));
Console.WriteLine("   ↑ 冷池首次借出 ≈ AcquireTimeout（默认 1s）——HayateOP 的 CreateNew 语义是" +
                  "「等满超时后创建」（与 MEOP 空池立即创建不同，详见 HayateCompatOptions 注释）。" +
                  "用 §3 的 MinSize 预热或缩短 AcquireTimeout 消除。");

// ---------------------------------------------------------------------------
// 3. 参数调优：HayateCompatOptions
//    - MinSize 预热：规避冷池首次借出的 AcquireTimeout 等待（HayateOP 的
//      CreateNew 语义是「等满超时后创建」，与 MEOP「空池立即创建」存在差异）；
//    - AcquireTimeout 缩短：降低冷启动延迟上限；
//    - PoolNamePrefix：池名前缀，便于观测与运维辨识来源。
// ---------------------------------------------------------------------------
Console.WriteLine("=== 3. HayateCompatOptions 调优（MinSize=2 预热 + AcquireTimeout=100ms） ===");
var tunedProvider = new HayateObjectPoolCompatProvider(new HayateCompatOptions
{
    PoolNamePrefix = "MyBiz.HayateCompat.",
    MinSize = 2,                                   // 预热 2 个对象，消除冷启动延迟
    AcquireTimeout = TimeSpan.FromMilliseconds(100)
});
DemoPool("Tuned", tunedProvider.Create(sbPolicy));

// ---------------------------------------------------------------------------
// 4. DI 集成：注册 ObjectPoolProvider 单例，消费者注入 ObjectPool<T>。
//    业务代码与 MEOP 下的注册方式完全一致，仅替换 provider 实例这一处。
// ---------------------------------------------------------------------------
Console.WriteLine("=== 4. DI 集成（ServiceCollection） ===");
var services = new ServiceCollection();
services.AddSingleton<ObjectPoolProvider>(new HayateObjectPoolCompatProvider(new HayateCompatOptions
{
    MinSize = 1,
    AcquireTimeout = TimeSpan.FromMilliseconds(100)
}));
services.AddSingleton(sp => sp.GetRequiredService<ObjectPoolProvider>().Create(sbPolicy));

using var sp = services.BuildServiceProvider();
var diPool = sp.GetRequiredService<ObjectPool<StringBuilder>>();
var diSb = diPool.Get();
try
{
    diSb.Append("from DI");
    Console.WriteLine($"   DI 注入池借出 -> \"{diSb}\"");
}
finally
{
    diPool.Return(diSb);   // 重置后回池
}
Console.WriteLine("   消费者仅依赖 ObjectPool<StringBuilder>，后端可随时在 DI 注册处切换。");

// ---------------------------------------------------------------------------
// 5. 反向适配：HayateObjectPoolAdapter<T>
//    已有原生 HayateOP 池（分片/自动扩缩容/泄漏检测等全功能）时，
//    可包装为 MEOP 的 ObjectPool<T>，供 MEOP 风格消费者使用。
// ---------------------------------------------------------------------------
Console.WriteLine("=== 5. 反向适配：原生 HayateOP 池 -> ObjectPool<T> ===");
using var nativePool = new HayatePoolBuilder<StringBuilder>()
    .WithPoolName("NativeHayateOP.SB")
    .WithMinSize(1)
    .WithMaxSize(32)
    .WithEnableAutoScaling(true)
    .WithRejectPolicy(HayatePoolRejectPolicy.BlockTimeout)
    .WithAcquireTimeout(TimeSpan.FromMilliseconds(100))
    .Build();

using var adapted = new HayateObjectPoolAdapter<StringBuilder>(nativePool);
var sb2 = adapted.Get();
try
{
    sb2.Append("adapted");
    Console.WriteLine($"   经适配器借出 -> \"{sb2}\"（池内当前存活 {nativePool.GetStats().CurrentSize} 个对象）");
}
finally
{
    adapted.Return(sb2);
}

Console.WriteLine();
Console.WriteLine("全部演示完成。");

// ---------------------------------------------------------------------------
// 共用演示：同一套 Get/Return 代码路径跑在任意 ObjectPool<StringBuilder> 上。
// ---------------------------------------------------------------------------
static void DemoPool(string label, ObjectPool<StringBuilder> pool)
{
    var sw = System.Diagnostics.Stopwatch.StartNew();
    var sb = pool.Get();            // 冷池首次借出：HayateOP 兼容池受 AcquireTimeout 影响
    try
    {
        sb.Append("hello from ").Append(label);
        Console.WriteLine($"   借出（{sw.ElapsedMilliseconds}ms） -> \"{sb}\"");
    }
    finally
    {
        pool.Return(sb);            // 策略 Return 钩子重置后回池
    }

    var sb2 = pool.Get();           // 二次借出：命中池内空闲对象，无冷启动延迟
    try
    {
        Console.WriteLine($"   二次借出（{sw.ElapsedMilliseconds}ms，已回池复用） -> \"{sb2}\"（容量已重置）");
    }
    finally
    {
        pool.Return(sb2);
    }
}
