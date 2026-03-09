using DotNetCore.HealthChecks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;

namespace DotNetCore.HayateOP.Tests;

public class HayatePoolRejectPolicyTests
{
    private class TestObject { }

    [Fact]
    public void RejectPolicy_Abort_ShouldThrowOnTimeout()
    {
        // Arrange
        using var pool = new HayatePoolBuilder<TestObject>()
            .WithMinSize(1)
            .WithMaxSize(1)
            .WithRejectPolicy(HayatePoolRejectPolicy.Abort)
            .Build();

        // 耗尽池
        pool.Acquire();

        // Act & Assert
        Assert.Throws<TimeoutException>(() => pool.Acquire(TimeSpan.FromMilliseconds(50)));
    }

    [Fact]
    public void RejectPolicy_CreateNew_ShouldReturnNewObjectOnTimeout()
    {
        // Arrange
        using var pool = new HayatePoolBuilder<TestObject>()
            .WithMinSize(1)
            .WithMaxSize(1)
            .WithRejectPolicy(HayatePoolRejectPolicy.CreateNew)
            .Build();

        // 耗尽池
        var obj1 = pool.Acquire();

        // Act
        var obj2 = pool.Acquire(TimeSpan.FromMilliseconds(50));

        // Assert
        Assert.NotNull(obj2);
        Assert.NotSame(obj1, obj2);
        Assert.Equal(1, pool.GetStats().TotalMissed);
    }
}