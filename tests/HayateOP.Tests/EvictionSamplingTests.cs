using System;
using System.Threading;

namespace DotNetCore.HayateOP.Tests
{
    /// <summary>
    /// PR-D A2：驱逐采样由「每次 GetALL().ToArray()」改为复用缓冲的 SnapshotHead(buffer, limit)，
    /// 避免每次对整条空闲链表做全量数组分发（长尾分配）。本用例验证有界采样窗口下，
    /// 驱逐仍跨周期正确收敛并清空过期对象，覆盖 SnapshotHead 的有界复制与缓冲复用路径。
    /// </summary>
    public class EvictionSamplingTests
    {
        private class TestObject { }

        [Fact(Timeout = 60000)]
        public void Eviction_WithBoundedSampleWindow_EventuallyEvictsExpired()
        {
            using var pool = new HayatePoolBuilder<TestObject>()
                .WithEnableEviction(true)
                .WithEvictionInterval(1000)
                .WithMaxLifeTime(TimeSpan.FromMilliseconds(100))
                .WithMinSize(12)
                .WithNumTestsPerEvictionRun(3)
                .Build();

            Assert.Equal(12, pool.GetStats().PooledCount);

            // 采样窗口为 3，远小于空闲对象数，驱逐必须跨多个周期才覆盖全部；
            // 等待足够窗口后所有过期的空闲对象应被清空，验证缓冲复用 + 有界复制逻辑。
            Thread.Sleep(6000);

            Assert.Equal(0, pool.GetStats().PooledCount);
        }
    }
}