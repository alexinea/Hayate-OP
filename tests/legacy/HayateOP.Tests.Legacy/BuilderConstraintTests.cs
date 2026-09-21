using System;
using DotNetCore.HayateOP.Policies;

namespace DotNetCore.HayateOP.Tests
{
    /// <summary>
    /// B1 (2.9): pooling a type that has no public parameterless constructor. The entry points that can be
    /// given a policy — the fluent builder and the container registrations — no longer require <c>new()</c>
    /// at compile time, which is what a connection-like type needs. The requirement did not disappear: it
    /// moved to the moment the default policy is actually resolved, where it is reported with an actionable
    /// message instead of a compile error. Verified: a no-constructor type is pooled through
    /// <c>WithPolicy</c> and through the unbounded pool's factory overload, the default policy is still the
    /// one that resets and validates (a delegate-based policy would do neither), and the entry points that
    /// cannot take a policy keep saying so.
    /// </summary>
    public class BuilderConstraintTests
    {
        /// <summary>A type with no public parameterless constructor — the shape a connection has.</summary>
        private sealed class ConnectionLike
        {
            public ConnectionLike(string connectionString) => ConnectionString = connectionString;

            public string ConnectionString { get; }
        }

        private sealed class ConnectionPolicy : IHayateObjectPolicy<ConnectionLike>
        {
            private readonly string _connectionString;

            public ConnectionPolicy(string connectionString) => _connectionString = connectionString;

            public ConnectionLike Create() => new(_connectionString);

            public bool OnRelease(ConnectionLike item) => true;

            public bool Validate(ConnectionLike item) => true;

            public void OnAcquire(ConnectionLike item) { }

            public void OnPassivate(ConnectionLike item) { }

            public void OnDestroy(ConnectionLike item) { }
        }

        private sealed class ParameterlessItem
        {
            public int Value { get; set; }
        }

        /// <summary>
        /// Carries the two default-policy behaviours a delegate-based policy would not reproduce: the
        /// release hook resets an <see cref="IHayateResettable"/>, and validity follows
        /// <see cref="IHayateValidatable"/>.
        /// </summary>
        private sealed class ResettableItem : IHayateResettable, IHayateValidatable
        {
            public int Value { get; set; } = 7;

            public int ResetCount { get; private set; }

            public bool Healthy { get; set; } = true;

            public void Reset()
            {
                Value = 0;
                ResetCount++;
            }

            public bool IsValid() => Healthy;
        }

        [Fact]
        public void Builder_WithoutParameterlessConstructor_WithPolicy_BorrowsAndReturns()
        {
            using var pool = new HayatePoolBuilder<ConnectionLike>()
                .WithPolicy(new ConnectionPolicy("Host=primary"))
                .Build();

            var item = pool.Acquire();
            Assert.Equal("Host=primary", item.ConnectionString);

            var pooledBefore = pool.GetStats().PooledCount;
            pool.Release(item);
            Assert.Equal(pooledBefore + 1, pool.GetStats().PooledCount);
        }

        [Fact]
        public void Builder_WithoutParameterlessConstructor_WithoutPolicy_ThrowsAnActionableMessage()
        {
            var builder = new HayatePoolBuilder<ConnectionLike>();

            var ex = Assert.Throws<InvalidOperationException>(() => builder.Build());

            Assert.Contains(typeof(ConnectionLike).FullName!, ex.Message);
            Assert.Contains("WithPolicy", ex.Message);
        }

        [Fact]
        public void Builder_WithParameterlessConstructor_StillUsesTheDefaultPolicy()
        {
            using var pool = new HayatePoolBuilder<ParameterlessItem>().Build();

            var item = pool.Acquire();
            Assert.NotNull(item);

            var pooledBefore = pool.GetStats().PooledCount;
            pool.Release(item);
            Assert.Equal(pooledBefore + 1, pool.GetStats().PooledCount);
        }

        [Fact]
        public void ObjectPolicies_Default_IsTheLibraryDefaultPolicy()
        {
            var policy = HayateObjectPolicies.Default<ResettableItem>();
            var item = policy.Create();

            item.Value = 42;
            Assert.True(policy.OnRelease(item));
            Assert.Equal(0, item.Value);
            Assert.Equal(1, item.ResetCount);
            Assert.True(policy.Validate(item));

            item.Healthy = false;
            Assert.False(policy.Validate(item));
        }

        [Fact]
        public void ObjectPolicies_Default_WithoutParameterlessConstructor_ThrowsAnActionableMessage()
        {
            var ex = Assert.Throws<InvalidOperationException>(() => HayateObjectPolicies.Default<ConnectionLike>());

            Assert.Contains(typeof(ConnectionLike).FullName!, ex.Message);
        }

        [Fact]
        public void UnboundedPool_WithoutParameterlessConstructor_ConvenienceOverloads_ThrowAnActionableMessage()
        {
            var ex = Assert.Throws<InvalidOperationException>(() => new HayateUnboundedPool<ConnectionLike>());
            Assert.Contains(typeof(ConnectionLike).FullName!, ex.Message);

            Assert.Throws<InvalidOperationException>(() => new HayateUnboundedPool<ConnectionLike>(8));
        }

        [Fact]
        public void UnboundedPool_WithoutParameterlessConstructor_WithFactory_BorrowsAndReturns()
        {
            using var pool = new HayateUnboundedPool<ConnectionLike>(8, () => new ConnectionLike("Host=replica"));

            var item = pool.Acquire();
            Assert.Equal("Host=replica", item.ConnectionString);

            pool.Release(item);
        }
    }
}
