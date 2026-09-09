using System;
using System.Threading;
using System.Threading.Tasks;

namespace DotNetCore.HayateOP.Tests;

/// <summary>
/// PR-E A3（=M5 统一 CT 传递）：AcquireAsync 超时重载与取消传播测试。
/// 语义对齐同步 Acquire(TimeSpan)：超时抛 TimeoutException（含 missed 计数/强制扩容），
/// 外部取消传播 TaskCanceledException；旧签名 AcquireAsync(ct) 保持不变。
/// </summary>
public class AcquireAsyncTimeoutTests
{
    private class TestObject { }

    [Fact]
    public async Task Timeout_ShouldThrowTimeoutExceptionWhenExhausted()
    {
        // Arrange：容量 1，首借自举拿走唯一对象；第二借无归还 → 超时。
        using var pool = new HayatePoolBuilder<TestObject>()
            .WithPoolName("a3-timeout-exhausted")
            .WithEnableAutoScaling(false)
            .WithMinSize(0)
            .WithMaxSize(1)
            .WithRejectPolicy(HayatePoolRejectPolicy.BlockTimeout)
            .Build();

        var first = await pool.AcquireAsync(CancellationToken.None);
        Assert.NotNull(first);

        // Act + Assert：池耗尽（1 借出，Max=1 不自举），200ms 必超时
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var ex = await Assert.ThrowsAsync<TimeoutException>(
            () => pool.AcquireAsync(TimeSpan.FromMilliseconds(200)));
        sw.Stop();

        Assert.Contains("超时", ex.Message);
        Assert.True(sw.Elapsed >= TimeSpan.FromMilliseconds(150), $"过早返回：{sw.Elapsed.TotalMilliseconds:F0}ms");
    }

    [Fact]
    public async Task Timeout_ShouldReturnImmediatelyWhenAvailable()
    {
        // Arrange：Min=1 预热，池有可用对象
        using var pool = new HayatePoolBuilder<TestObject>()
            .WithPoolName("a3-timeout-available")
            .WithEnableAutoScaling(false)
            .WithMinSize(1)
            .WithMaxSize(5)
            .WithRejectPolicy(HayatePoolRejectPolicy.BlockTimeout)
            .Build();

        // Act：极短超时也应立即成功（可用对象无需等待）
        var obj = await pool.AcquireAsync(TimeSpan.FromMilliseconds(200));

        // Assert
        Assert.NotNull(obj);
        pool.Release(obj);
    }

    [Fact]
    public async Task Timeout_ShouldSucceedAfterReleaseWithinWindow()
    {
        // Arrange：容量 1，首借占用；100ms 后后台归还
        using var pool = new HayatePoolBuilder<TestObject>()
            .WithPoolName("a3-timeout-release-in-window")
            .WithEnableAutoScaling(false)
            .WithMinSize(0)
            .WithMaxSize(1)
            .WithRejectPolicy(HayatePoolRejectPolicy.BlockTimeout)
            .Build();

        var first = await pool.AcquireAsync(CancellationToken.None);

        _ = Task.Run(async () =>
        {
            await Task.Delay(100);
            pool.Release(first);
        });

        // Act：5s 窗口内等待归还信号唤醒
        var second = await pool.AcquireAsync(TimeSpan.FromSeconds(5));

        // Assert：拿到的是归还的那个对象（池化复用）
        Assert.Same(first, second);
        pool.Release(second);
    }

    [Fact]
    public async Task ExternalCancel_ShouldThrowTaskCanceledNotTimeout()
    {
        // Arrange：容量 1，首借占用；外部 CT 200ms 后取消（远小于 10s 超时）
        using var pool = new HayatePoolBuilder<TestObject>()
            .WithPoolName("a3-external-cancel")
            .WithEnableAutoScaling(false)
            .WithMinSize(0)
            .WithMaxSize(1)
            .WithRejectPolicy(HayatePoolRejectPolicy.BlockTimeout)
            .Build();

        var first = await pool.AcquireAsync(CancellationToken.None);
        using var cts = new CancellationTokenSource(200);

        // Act + Assert：外部取消必须传播为 OCE 族，而非被转译成 TimeoutException
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => pool.AcquireAsync(TimeSpan.FromSeconds(10), cts.Token));
    }

    [Fact]
    public async Task InfiniteTimeout_ShouldHonorExternalCancel()
    {
        // Arrange：InfiniteTimeSpan 转调无超时版本，外部预取消令牌
        using var pool = new HayatePoolBuilder<TestObject>()
            .WithPoolName("a3-infinite-cancel")
            .WithEnableAutoScaling(false)
            .WithMinSize(0)
            .WithMaxSize(1)
            .WithRejectPolicy(HayatePoolRejectPolicy.BlockTimeout)
            .Build();

        var first = await pool.AcquireAsync(CancellationToken.None);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // Act + Assert
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => pool.AcquireAsync(Timeout.InfiniteTimeSpan, cts.Token));
    }

    [Fact]
    public async Task LegacySignature_ShouldRemainAvailable()
    {
        // 旧签名 AcquireAsync(ct) 保持不变（A3 零破坏承诺）
        using var pool = new HayatePoolBuilder<TestObject>()
            .WithPoolName("a3-legacy-signature")
            .WithEnableAutoScaling(false)
            .WithMinSize(1)
            .WithMaxSize(5)
            .Build();

        var obj = await pool.AcquireAsync(CancellationToken.None);
        Assert.NotNull(obj);
        pool.Release(obj);
    }
}
