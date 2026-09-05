// 测试套件级"围栏"（P0/R2, R3）：
// 本程序集以对象池并发/线程压测为主，多个类都会驱动 ThreadPool、GC、SpinLock、
// 后台驱逐/校验回调与扩缩容冷却。默认跨类并行会让重并发类与轻量确定性用例互相竞争 CPU，
// 既造成偶发超时/时序 flaky，也在宿主机器上叠加 CPU 空转。
//
// 因此在此**程序集级关闭测试并行化**：所有 collection 串行执行，作为最强的确定性围栏。
// 其代价是整套基本串行；换取的是：任一用例不与它类并发争抢，CPU 占用收敛、结果可复现。
// 若日后需要吞吐，可在并发类稳定后再改为按 collection 粒度精细并行，而把轻量类留在并行组。
//
// 双轨说明：本仓库测试项目 multi-target net6.0~net10.0。
//   - net6.0/net7.0 编译到 xunit v2（走 VSTest），程序集级关并行用 v2 的 CollectionBehavior；
//   - net8.0/net9.0/net10.0 编译到 xunit v3（走 MTP），xunit v3 已把 CollectionBehavior.
//     DisableTestParallelization 标记为过时(编译错误 CS0619)，改用 v3 的
//     Xunit.v3.Parallelization(Mode = Xunit.Sdk.ParallelMode.None) 表达同样的"全串行"语义。
//   - 分支由 csproj 定义的 XUNIT_V3 编译常量在编译期切换（见 HayateOP.Tests.csproj）。
#if XUNIT_V3
using Xunit.v3;

// v3：程序集级关闭所有并行（等价 v2 的 DisableTestParallelization）
[assembly: Parallelization(Mode = Xunit.Sdk.ParallelMode.None)]
#else
using Xunit;

// v2：程序集级关闭跨 collection 并行
[assembly: CollectionBehavior(DisableTestParallelization = true)]
#endif
