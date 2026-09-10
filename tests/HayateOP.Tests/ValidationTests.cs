using System;
using System.Collections.Generic;
using System.Text;

namespace DotNetCore.HayateOP.Tests
{
    public class ValidationTests
    {
        private class TestObject : IHayateValidatable
        {
            public bool IsValidReturn { get; set; } = true;
            public bool IsValid() => IsValidReturn;
        }

        [Fact]
        public void EnableValidation_ShouldValidateOnBorrow()
        {
            using var pool = new HayatePoolBuilder<TestObject>()
                .WithEnableValidation(true)
                .WithValidateOnBorrow(true)
                .Build();

            var obj = pool.Acquire();
            obj.IsValidReturn = false;
            pool.Release(obj);

            // The next borrow should fail validation and destroy the object
            var newObj = pool.Acquire();
            Assert.NotSame(obj, newObj);
        }

        [Fact]
        public void DisableValidation_ShouldSkipAllValidation()
        {
            var options = new HayatePoolOptions
            {
                EnableValidation = false,
                ValidateOnBorrow = true,
                ValidateOnReturn = true,
                ValidateWhileIdle = true
            };
            options.ApplyFeatureSwitches();

            Assert.False(options.ValidateOnBorrow);
            Assert.False(options.ValidateOnReturn);
            Assert.False(options.ValidateWhileIdle);
        }

        [Fact]
        public void ValidateOnReturn_ShouldRejectInvalidObject()
        {
            using var pool = new HayatePoolBuilder<TestObject>()
                .WithEnableValidation(true)
                .WithValidateOnReturn(true)
                .WithMinSize(5)
                .Build();

            var obj = pool.Acquire();
            obj.IsValidReturn = false;
            pool.Release(obj);

            // The object is destroyed on validation; the pool size decreases
            Assert.Equal(4, pool.GetStats().PooledCount);
        }
    }
}
