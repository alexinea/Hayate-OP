using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using DotNetCore.HayateOP.Logging;

namespace DotNetCore.HayateOP.Tests
{
    /// <summary>
    /// The cache-line padding on a shard (G-4 / 33-R-4).
    ///
    /// A shard is the pool's stripe, and the pool allocates every shard back to back in one loop (see
    /// <c>HayatePoolBasic&lt;T&gt;._shards</c>), so two neighbouring stripes would otherwise share a cache
    /// line: one thread's writes to its own counters would invalidate the other's line on every borrow and
    /// return. The padding is what stops that — a cache line of space in front of a shard's fields and a cache
    /// line of space behind them.
    ///
    /// It cannot be declared in one place. The runtime refuses explicit layout on a generic type, and a nested
    /// class of <c>HayatePoolBasic&lt;T&gt;</c> is one; it also ignores the <c>Size</c> of a class that holds
    /// references. So the padding is split: the leading half is an explicitly laid out field of the
    /// non-generic base class, and the trailing half is a field of the shard itself.
    ///
    /// Layout only: no member, default value or behaviour changes.
    /// </summary>
    public class CacheLinePaddingTests
    {
        private const int CacheLineSize = 64;
        private const string TrailingPad = "_trailingPad";

        private class TestObject { }

        [Fact]
        public void Shard_ShouldCarryBothHalvesOfTheCacheLinePadding()
        {
            var shardType = ShardType();

            // The leading half. Explicit layout is what makes the offset exact, and it is legal here only
            // because the base is non-generic and holds no references — which is why the padding cannot simply
            // be declared on the shard: the runtime rejects explicit layout on a generic type, and a nested
            // class of HayatePoolBasic<T> is one. The field ends the first cache line, so the shard's own
            // fields start on the next one.
            var baseType = shardType.BaseType;
            Assert.NotNull(baseType);
            Assert.False(baseType!.IsGenericType, "the padding base must be non-generic");
            Assert.Equal(LayoutKind.Explicit, baseType.StructLayoutAttribute?.Value);
            Assert.Equal(CacheLineSize - sizeof(long), Marshal.OffsetOf(baseType, "_leadingPad").ToInt32());

            // The trailing half, declared by the shard — the most derived class — because that is the only place
            // the runtime will put it. A whole cache line of it, so the next shard allocated into the same
            // allocation context does not land on the fields of this one.
            var trailingPad = shardType.GetField(TrailingPad, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(trailingPad);
            Assert.Equal(shardType, trailingPad!.DeclaringType);
            Assert.Equal(LayoutKind.Sequential, trailingPad.FieldType.StructLayoutAttribute?.Value);
            Assert.Equal(CacheLineSize, trailingPad.FieldType.StructLayoutAttribute?.Size);
        }

        [Fact]
        public void Shard_ShouldCostTwoCacheLinesMoreThanTheSameObjectWithoutThePadding()
        {
            var shardType = ShardType();

            // The declarations above say the padding is there; this says it reaches the heap. A shard is
            // allocated through its reflected constructor and so is a control with the shard's exact field set
            // and no padding, through a constructor of the same shape and with the same arguments — so the
            // reflection overhead cancels, the field sets cancel, and the padding is what is left. Removing
            // either half drops the difference to a single cache line and fails the assertion below.
            //
            // The heap distance between two shards would say it more directly, but it is out of reach: the
            // runtime refuses to pin an object that contains references, and a shard, a shard[] and an object[]
            // all contain them, so no address can be taken without unsafe code.
            var controlFields = typeof(UnpaddedShardShape)
                .GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

            // The control mirrors the shard's field set by hand, so a field added to the shard would quietly
            // make the control too small and this test weaker without ever failing. Compare the two sets by
            // name and by storage size, so drift breaks here instead.
            var shardFields = shardType
                .GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
                .Where(field => field.Name != TrailingPad)
                .ToDictionary(field => field.Name);
            Assert.Equal(shardFields.Count, controlFields.Length);
            foreach (var controlField in controlFields)
            {
                Assert.True(shardFields.TryGetValue(controlField.Name, out var shardField),
                    $"the control has a field named {controlField.Name} that the shard does not");
                Assert.True(SameStorage(controlField.FieldType, shardField!.FieldType),
                    $"{controlField.Name} holds {controlField.FieldType} in the control and "
                    + $"{shardField.FieldType} in the shard");
            }

            var padding = PaddingBytes(ShardConstructor(shardType), ControlConstructor(), Arguments());

            // Both halves are worth a cache line, so the two constructions sit 128 B apart, and dropping
            // either half leaves 64 B. The threshold is placed one pointer below two cache lines rather than
            // on them: the estimator below is stable to a fraction of a byte, but a runtime that rounds the
            // layout a little differently should not fail a test about whether the padding is there at all.
            const int Slack = sizeof(long);
            Assert.True(padding >= CacheLineSize * 2 - Slack,
                $"the padding is worth {padding:F3} B — two cache lines are {CacheLineSize * 2} B, and either "
                + "half on its own is worth one");
        }

        private static Type ShardType()
        {
            // The nested type of a constructed generic type comes back as its open definition, whose field
            // types still contain generic parameters — and reflection refuses to read values through those.
            // Close it over the element type the pool under test uses.
            return typeof(HayatePoolBasic<TestObject>)
                .GetNestedType("Shard", BindingFlags.NonPublic)!
                .MakeGenericType(typeof(TestObject));
        }

        private static ConstructorInfo ShardConstructor(Type shardType) =>
            shardType.GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)[0];

        private static ConstructorInfo ControlConstructor() => typeof(UnpaddedShardShape)
            .GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic)[0];

        // Both constructors take the same five arguments in the same order, so the one array fits either and
        // the per-invocation cost of passing it cannot differ between the two measurements. The logger is left
        // null on purpose: the constructor only stores it, and nothing here calls a shard.
        private static object[] Arguments() => new object[] { new HayatePoolOptions(), 0, 16, null, false };

        /// <summary>
        /// The padding in bytes: the average of the per-construction difference between what a shard costs
        /// and what the same object without the padding costs.
        /// </summary>
        /// <remarks>
        /// A paired difference, not two independent sizes. The allocation counter is not byte-exact — it
        /// reads a couple of bytes apart between runs, and the smallest of a few hundred samples still lands
        /// on values no object can be, since every allocation is a multiple of the pointer size — but both
        /// constructions go through the same reflection invoke, which adds the same per-call overhead to
        /// each, so subtracting them inside one iteration cancels it. What is left is stable to a fraction
        /// of a byte: 128.000 over repeated runs on .NET 8 and .NET 10, against 64.000 for either half alone.
        /// </remarks>
        private static double PaddingBytes(ConstructorInfo shard, ConstructorInfo control, object[] arguments)
        {
            const int Samples = 512;
            var keep = new object[Samples * 2];
            long paired = 0;
            for (var i = 0; i < Samples; i++)
            {
                var before = GC.GetAllocatedBytesForCurrentThread();
                keep[i] = shard.Invoke(arguments);
                var middle = GC.GetAllocatedBytesForCurrentThread();
                keep[Samples + i] = control.Invoke(arguments);
                var after = GC.GetAllocatedBytesForCurrentThread();
                paired += (middle - before) - (after - middle);
            }

            GC.KeepAlive(keep);
            return (double)paired / Samples;
        }

        // A value type counts by the space it occupies; every reference type counts the same, as a reference.
        private static bool SameStorage(Type control, Type shard) => control.IsValueType == shard.IsValueType
            && (!control.IsValueType || Marshal.SizeOf(control) == Marshal.SizeOf(shard));

        /// <summary>
        /// The shard's field set without the padding: the same field names and types in the same order, a
        /// constructor of the shard's exact shape, and a body that allocates exactly what the shard's body
        /// allocates — the three collections its field initialisers create.
        /// </summary>
        private sealed class UnpaddedShardShape
        {
            private readonly IHayateLogger _logger;
            private readonly bool _enableDiagnostics;
            private readonly LinkedList<object> _list;
            private readonly bool _takeNewest;
            private readonly LinkedList<object> _borrowed;
            private readonly bool _trackBorrowed;
            private readonly ConcurrentDictionary<object, object> _objects;
            private SpinLock _lock;
            private int _maxSize;
            private readonly bool _prefersAsyncDisposal;
            private SpinLock _spareLock;
            private object _spareHead;
            private int _spareCount;

            public int Index { get; }

            private UnpaddedShardShape(HayatePoolOptions options, int index, int maxSize, IHayateLogger logger,
                bool prefersAsyncDisposal)
            {
                Index = index;
                _maxSize = maxSize;
                _logger = logger;
                _prefersAsyncDisposal = prefersAsyncDisposal;
                _trackBorrowed = options.RemoveAbandonedOnBorrow || options.RemoveAbandonedOnMaintenance;
                _enableDiagnostics = options.EnableDiagnostics;
                _takeNewest = options.BorrowStrategy == HayateBorrowStrategy.Lifo;
                _list = new LinkedList<object>();
                _borrowed = new LinkedList<object>();
                _objects = new ConcurrentDictionary<object, object>();
                _lock = new SpinLock(enableThreadOwnerTracking: false);
                _spareLock = new SpinLock(enableThreadOwnerTracking: false);
                _spareCount = 0;
                _spareHead = null;
                _ = _logger;
                _ = _enableDiagnostics;
                _ = _takeNewest;
                _ = _trackBorrowed;
                _ = _prefersAsyncDisposal;
                _ = _spareCount;
                _ = _spareHead;
            }
        }
    }
}
