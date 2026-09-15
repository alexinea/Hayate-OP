using System;
using System.Globalization;
using DotNetCore.HayateOP.Specialized;

namespace DotNetCore.HayateOP.Tests;

/// <summary>
/// Z6: the generic direct-write Build overloads on <see cref="SharedStringBuilder"/> — string.Format
/// grammar against the shared builder without an <c>object[]</c> and without boxing the arguments.
/// The lock discipline is unchanged: the write and the snapshot happen under the lock, the builder is
/// cleared before the lock is released, and a failing format releases the lock.
/// </summary>
public class SharedStringBuilderGenericBuildTests
{
    [Fact]
    public void Build_WithFormat_ShouldMatchStringFormat()
    {
        Assert.Equal(
            string.Format("{0}-{1}", "order", 42),
            SharedStringBuilder.Build("{0}-{1}", "order", 42));

        // Repeated holes, escapes, format specifiers, alignment.
        Assert.Equal(
            string.Format("{0}{0}", "x"),
            SharedStringBuilder.Build("{0}{0}", "x"));
        Assert.Equal(
            string.Format("{{{0}}}", 7),
            SharedStringBuilder.Build("{{{0}}}", 7));
        Assert.Equal(
            string.Format("{0:F2}", 1234.5678),
            SharedStringBuilder.Build("{0:F2}", 1234.5678));
        Assert.Equal(
            string.Format("[{0,6}]", 42),
            SharedStringBuilder.Build("[{0,6}]", 42));
        Assert.Equal(
            string.Format("[{0,-6}]", 42),
            SharedStringBuilder.Build("[{0,-6}]", 42));
    }

    [Fact]
    public void Build_WithEightArguments_ShouldMatchStringFormat()
    {
        Assert.Equal(
            string.Format("{7}{6}{5}{4}{3}{2}{1}{0}", 1, 2, 3, 4, 5, 6, 7, 8),
            SharedStringBuilder.Build("{7}{6}{5}{4}{3}{2}{1}{0}", 1, 2, 3, 4, 5, 6, 7, 8));
    }

    [Fact]
    public void Build_WithNullArgument_ShouldRenderEmpty()
    {
        Assert.Equal(
            string.Format("{0}|{1}", "a", (string?)null),
            SharedStringBuilder.Build("{0}|{1}", "a", (string?)null));
    }

    [Fact]
    public void Build_ShouldThrowWhenTheIndexIsOutOfRange()
    {
        Assert.Throws<FormatException>(() => SharedStringBuilder.Build("{1}", "only-one"));
    }

    [Fact]
    public void Build_ShouldReleaseTheLockWhenTheFormatThrows()
    {
        Assert.Throws<FormatException>(() => SharedStringBuilder.Build("{0", "a"));

        // The lock is released on the failure path too, and the builder is cleared.
        using var scope = SharedStringBuilder.Acquire();
        Assert.Equal(0, scope.StringBuilder.Length);
    }

    [Fact]
    public void Build_ShouldLeaveAnEmptyBuilder()
    {
        var content = SharedStringBuilder.Build("{0}: {1}", "order", 42);
        Assert.Equal("order: 42", content);

        using var scope = SharedStringBuilder.Acquire();
        Assert.Equal(0, scope.StringBuilder.Length);
    }

#if !NET48
    // GC.GetAllocatedBytesForCurrentThread does not exist on net48; the no-object[] acceptance is a
    // modern-target assertion. The net48 path formats through IFormattable, matching the primitive
    // appends there.
    [Fact]
    public void Build_WithFormat_ShouldAllocateNothingBeyondTheSnapshot()
    {
        // Warm every measured instantiation with a real loop: tier0 generic code still box-frames its
        // interface dispatch, and only repeated calls promote it.
        for (var i = 0; i < 100; i++)
        {
            _ = SharedStringBuilder.Build("{0}-{1}", "warm", i);
        }

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        // One discarded measured call absorbs the first-call environment cost a test host can add.
        _ = SharedStringBuilder.Build("{0}-{1}", "warm", 0);

        var before = GC.GetAllocatedBytesForCurrentThread();
        var text = SharedStringBuilder.Build("{0}-{1}", "order", 42);
        var during = GC.GetAllocatedBytesForCurrentThread() - before;

        // The only allocation is the returned string ("order-42" is 44 B); a regression to an
        // object[] plus argument boxes would push the delta past 88 B, well over this bound.
        Assert.True(during <= 64, $"The build should allocate only the final string (got {during} B).");
        Assert.Equal("order-42", text);
    }
#endif
}
