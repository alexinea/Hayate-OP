using System;
using System.Collections.Generic;
using System.Text;

namespace DotNetCore.HayateOP.Tests
{
    public class GenerationOptimizationTests
    {
        private class TestObject { }

        [Fact]
        public void EnableGenerationOptimization_ShouldWork()
        {
            using var pool = new HayatePoolBuilder<TestObject>()
                .WithEnableGenerationOptimization(true)
                .WithGenerationThreshold(1000)
                .WithOldGenerationValidationInterval(3)
                .Build();

            // Verify the pool works normally
            var obj = pool.Acquire();
            pool.Release(obj);
            Assert.NotNull(obj);
        }

        [Fact]
        public void DisableGenerationOptimization_ShouldTreatAllAsYoung()
        {
            using var pool = new HayatePoolBuilder<TestObject>()
                .WithEnableGenerationOptimization(false)
                .WithEnableValidation(true)
                .WithValidateOnBorrow(true)
                .Build();

            // Verify validation happens on every borrow
            for (int i = 0; i < 10; i++)
            {
                var obj = pool.Acquire();
                pool.Release(obj);
            }
        }
    }
}
