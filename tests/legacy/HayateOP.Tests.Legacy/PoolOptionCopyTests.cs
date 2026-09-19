using System;
using System.Reflection;

namespace DotNetCore.HayateOP.Tests;

/// <summary>
/// Option-copy fidelity. <c>HayatePoolOptions.CopyTo</c> is the channel every copy-based surface goes
/// through: <c>GetOptions()</c> hands out a copy, the configuration binder merges through a copy, and the
/// DI registration copies the bound options onto the builder. A property the copy forgets is therefore a
/// setting that silently disappears on the way in or on the way out, while every other surface keeps
/// reporting it as if it had survived.
/// Acceptance: a copy carries every readable option, in both the "fresh instance" and the "into an
/// existing instance" overload; and the ArrayPool storage switch -- the one that was actually being
/// dropped -- survives all the way to the observable <c>GetOptions()</c> path.
/// </summary>
public class PoolOptionCopyTests
{
    private sealed class TestObject { }

    /// <summary>
    /// Reflection-driven on purpose: a newly declared option is covered the moment it exists, instead of
    /// the day someone remembers to extend a hand-written list.
    /// </summary>
    private static void AssertCopyCarriesEveryOption(HayatePoolOptions source, HayatePoolOptions copy)
    {
        foreach (var property in typeof(HayatePoolOptions).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!property.CanRead) continue;
            Assert.Equal(property.GetValue(source), property.GetValue(copy));
        }
    }

    /// <summary>
    /// Writes a value that differs from the shipped default into every writable option, so the assertion
    /// above cannot pass on a property the source never touched.
    /// </summary>
    private static void FillWithNonDefaultValues(HayatePoolOptions options)
    {
        foreach (var property in typeof(HayatePoolOptions).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!property.CanWrite) continue;
            property.SetValue(options, NonDefaultValue(property));
        }
    }

    private static object? NonDefaultValue(PropertyInfo property)
    {
        var current = property.GetValue(new HayatePoolOptions());
        var type = property.PropertyType;

        if (type == typeof(bool)) return !(bool)current!;
        if (type == typeof(int)) return (int)current! + 1;
        if (type == typeof(double)) return (double)current! + 0.25;
        if (type == typeof(TimeSpan)) return ((TimeSpan)current!) + TimeSpan.FromSeconds(1);
        if (type == typeof(HayateCircuitBreakerOptions)) return new HayateCircuitBreakerOptions { FailureThreshold = 7 };
        if (type == typeof(Func<int>)) return new Func<int>(() => 0);
        if (type == typeof(Action<HayatePoolCapacityAlarmEventArgs>))
        {
            return new Action<HayatePoolCapacityAlarmEventArgs>(_ => { });
        }
        if (type == typeof(Action<HayatePoolAvailabilityEventArgs>))
        {
            return new Action<HayatePoolAvailabilityEventArgs>(_ => { });
        }

        if (type.IsEnum)
        {
            foreach (var value in Enum.GetValues(type))
            {
                if (!Equals(value, current)) return value;
            }

            throw new InvalidOperationException($"Option {property.Name}: enum {type.Name} has no second member to switch to");
        }

        // Reaching this line means a new option was declared with a type this helper does not know how
        // to probe. Extending the helper is part of declaring it -- failing here is the reminder.
        throw new InvalidOperationException(
            $"Option {property.Name} has type {type.Name}, which the copy-fidelity probe does not handle yet; " +
            "add it to NonDefaultValue so the option is covered.");
    }

    [Fact]
    public void CopyTo_ShouldCarryEveryOption()
    {
        var source = new HayatePoolOptions();
        FillWithNonDefaultValues(source);

        var copy = source.CopyTo();

        AssertCopyCarriesEveryOption(source, copy);
        Assert.True(copy.EnableArrayPoolStorage);
    }

    [Fact]
    public void CopyToIntoAnExistingInstance_ShouldCarryEveryOption()
    {
        var source = new HayatePoolOptions();
        FillWithNonDefaultValues(source);

        var target = new HayatePoolOptions();
        source.CopyTo(target);

        AssertCopyCarriesEveryOption(source, target);
    }

    [Fact]
    public void CopyTo_ShouldNotShareTheCircuitBreakerSettings()
    {
        // The nested settings object is copied, not aliased: mutating the copy must not reconfigure the
        // pool the copy came from.
        var source = new HayatePoolOptions { EnableCircuitBreaker = true };
        source.CircuitBreaker.FailureThreshold = 9;

        var copy = source.CopyTo();
        copy.CircuitBreaker.FailureThreshold = 2;

        Assert.Equal(9, source.CircuitBreaker.FailureThreshold);
        Assert.Equal(2, copy.CircuitBreaker.FailureThreshold);
    }

    [Fact]
    public void GetOptions_ShouldReportTheArrayPoolStorageBackend()
    {
        // The storage shape is live only on the lean fast path, which is exactly the configuration whose
        // snapshot used to lose the switch and report a fixed-buffer pool.
        using var pool = new HayatePoolBuilder<TestObject>()
            .WithLean()
            .WithArrayPoolStorage()
            .WithMinSize(0)
            .WithMaxSize(8)
            .WithRejectPolicy(HayatePoolRejectPolicy.CreateOnDemand)
            .Build();

        Assert.True(pool.GetOptions().EnableArrayPoolStorage);
    }
}
