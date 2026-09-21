using DotNetCore.HayateOP;
using DotNetCore.HealthChecks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace HayateOP.Tests.HealthCheck.Legacy;

/// <summary>
/// Health check granularity (B4): what the check reports, and on what evidence.
/// Legacy mirror of <c>tests/HayateOP.Tests.HealthCheck/HayateOpHealthCheckTests.cs</c> — the two files are
/// kept in step; a change to one is a change to both.
/// </summary>
public class HayateOpHealthCheckTests
{
    public sealed class Pooled
    {
    }

    /// <summary>A probe whose answer, and whose failure mode, the test decides.</summary>
    private sealed class StubProbe : IHayateObjectHealthProbe<Pooled>
    {
        private readonly bool _verdict;
        private readonly bool _throws;

        public int Calls;

        public StubProbe(bool verdict = true, bool throws = false)
        {
            _verdict = verdict;
            _throws = throws;
        }

        public Task<bool> IsHealthyAsync(Pooled obj, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Calls);

            if (_throws) throw new InvalidOperationException("the probe itself failed");

            return Task.FromResult(_verdict);
        }
    }

    private static IHayateObjectPool<Pooled> BuildPool(int minSize = 1, int maxSize = 4)
    {
        return new HayatePoolBuilder<Pooled>()
            .WithMinSize(minSize)
            .WithMaxSize(maxSize)
            .Build();
    }

    private static IHayateObjectPool<Pooled> BuildPoolWithBreaker()
    {
        return new HayatePoolBuilder<Pooled>()
            .WithMinSize(1)
            .WithMaxSize(4)
            .WithEnableCircuitBreaker()
            .WithCircuitBreaker(new HayateCircuitBreakerOptions { FailureThreshold = 1 })
            .Build();
    }

    [Fact]
    public async Task CircuitBreakerOpen_ShouldBeReportedAsUnhealthy()
    {
        var pool = BuildPoolWithBreaker();
        var check = new HayateOpHealthCheck<Pooled>(pool);

        var before = await check.CheckHealthAsync(new HealthCheckContext());
        Assert.Equal(HealthStatus.Healthy, before.Status);

        // The pool has free slots throughout — only the breaker moves, so this isolates the new judgment.
        pool.SetUnavailable("dependency down");
        Assert.False(pool.CheckAvailable());
        Assert.True(pool.GetStats().AvailableSlots > 0);

        var after = await check.CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Unhealthy, after.Status);
        Assert.Contains("circuit breaker", after.Description.ToLowerInvariant());
        Assert.Equal(true, after.Data["CircuitBreakerOpen"]);
    }

    [Fact]
    public async Task ProbeRejectingTheObject_ShouldBeReportedAsUnhealthy()
    {
        var pool = BuildPool();
        var probe = new StubProbe(verdict: false);
        var check = new HayateOpHealthCheck<Pooled>(pool, probe);

        var result = await check.CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Equal(1, probe.Calls);
    }

    [Fact]
    public async Task ProbeAcceptingTheObject_ShouldBeHealthyAndShouldGiveTheObjectBack()
    {
        var pool = BuildPool();
        var idleBefore = pool.GetStats().AvailableSlots;
        var probe = new StubProbe(verdict: true);
        var check = new HayateOpHealthCheck<Pooled>(pool, probe);

        var result = await check.CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Healthy, result.Status);
        Assert.Equal(1, probe.Calls);

        // The probe borrows a real object; a check that quietly consumed one would drain the pool a slot
        // per poll.
        Assert.Equal(idleBefore, pool.GetStats().AvailableSlots);
    }

    [Fact]
    public async Task ProbeThrowing_ShouldBeUnhealthyAndShouldStillGiveTheObjectBack()
    {
        var pool = BuildPool();
        var idleBefore = pool.GetStats().AvailableSlots;
        var probe = new StubProbe(throws: true);
        var check = new HayateOpHealthCheck<Pooled>(pool, probe);

        var result = await check.CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.NotNull(result.Exception);
        Assert.Equal(idleBefore, pool.GetStats().AvailableSlots);
    }

    [Fact]
    public async Task SlotsExhausted_ShouldBeDegradedAndShouldNotBeProbed()
    {
        var pool = BuildPool(minSize: 1, maxSize: 1);
        var probe = new StubProbe(verdict: true);
        var check = new HayateOpHealthCheck<Pooled>(pool, probe);

        var borrowed = pool.Acquire();
        Assert.Equal(0, pool.GetStats().AvailableSlots);

        var result = await check.CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Degraded, result.Status);

        // Probing here would mean waiting for the object the test is holding, so the check must not try.
        Assert.Equal(0, probe.Calls);

        pool.Release(borrowed);
    }

    [Fact]
    public async Task RegisterHealthChecksWithAProbe_ShouldResolveACheckThatConsultsIt()
    {
        var services = new ServiceCollection();
        services.AddHayatePoolSupport()
            .RegisterHayatePool<Pooled>(o =>
            {
                o.MinPoolSize = 1;
                o.MaxPoolSize = 4;
            })
            .RegisterHealthChecks<Pooled, StubProbe>();

        var provider = services.BuildServiceProvider();
        var check = provider.GetRequiredService<HayateOpHealthCheck<Pooled>>();

        // StubProbe's default verdict is "healthy", so this only proves the wiring reaches the probe — the
        // verdict itself is what the other cases cover.
        var result = await check.CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Healthy, result.Status);
        Assert.Equal(1, provider.GetRequiredService<StubProbe>().Calls);
    }

    [Fact]
    public async Task Payload_ShouldCarryTheCountersItUsedToOmit()
    {
        var pool = BuildPool();
        var check = new HayateOpHealthCheck<Pooled>(pool);

        var result = await check.CheckHealthAsync(new HealthCheckContext());

        foreach (var key in new[]
                 {
                     "PooledCount", "MinPoolSize", "CurrentSize", "AvailableSlots",
                     "TotalCreated", "TotalReleased", "TotalAcquired", "TotalDestroyed", "TotalMissed",
                     "LeakDetectedCount", "LeakSuspectedCount", "AbandonedRemovedCount",
                     "LifetimeRotatedCount", "CircuitBreakerOpen"
                 })
        {
            Assert.True(result.Data.ContainsKey(key), $"payload is missing '{key}'");
        }
    }
}
