using Xunit;

namespace DotNetCore.HayateOP.Tests;

/// <summary>
/// T04（Shard.Remove 原子化）用例集合定义：集合内所有测试串行执行。
/// <para>
/// 原因与本仓库 <see cref="OptimizationRegressionCollection"/> 一致：本集合包含
/// 高并发压力用例（Parallel.For 驱动上千次 Add/TryTake/Remove，以及手动高频驱动
/// 驱逐 / 校验回调的后台线程），会在 ThreadPool、GC 与计时器精度上留下抖动，
/// 使同集合内的轻量确定性用例偶发失败。
/// 通过 DisableParallelization 让集合内测试串行跑，保证结果可复现。
/// </para>
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ShardAtomicRemovalCollection
{
    public const string Name = "ShardAtomicRemoval";
}
