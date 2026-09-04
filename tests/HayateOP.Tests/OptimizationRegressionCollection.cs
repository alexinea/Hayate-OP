using Xunit;

namespace DotNetCore.HayateOP.Tests;

/// <summary>
/// 优化回归用例集合定义：
/// 该 collection 内的所有测试必须串行执行（不被并行化）。
/// 原因：上游的 100 线程并发压力测试（ConcurrentAcquire_*）会在共享的
/// ThreadPool / GC / 计时器精度上留下抖动；简单测试（Acquire/Release/Validation
/// 状态）在这种抖动下偶发通过/失败不稳定。
/// 通过 DisableParallelization 让该集合内的测试串行跑，避免被上游并发测试污染。
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class OptimizationRegressionCollection
{
    public const string Name = "OptimizationRegression";
}
