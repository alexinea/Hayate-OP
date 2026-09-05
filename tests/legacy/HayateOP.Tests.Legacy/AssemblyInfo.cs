// 测试套件级"围栏"（P0/R2, R3）—— 与 tests/HayateOP.Tests/AssemblyInfo.cs 语义一致。
// 本程序集以对象池并发/线程压测为主，多个类都会驱动 ThreadPool、GC、SpinLock、
// 后台驱逐/校验回调与扩缩容冷却。默认跨类并行会让重并发类与轻量确定性用例互相竞争 CPU，
// 既造成偶发超时/时序 flaky，也在宿主机器上叠加 CPU 空转。
//
// 因此在此**程序集级关闭测试并行化**：所有 collection 串行执行，作为最强的确定性围栏。
// 其代价是整套基本串行；换取的是：任一用例不与它类并发争抢，CPU 占用收敛、结果可复现。
// 若日后需要吞吐，可在并发类稳定后再改为按 collection 粒度精细并行，而把轻量类留在并行组。
//
// 世代说明（LEGACY）：本文件属于 tests/legacy/HayateOP.Tests.Legacy（legacy 世代，
// xunit v2 + VSTest，net6/net7）。程序集级关闭并行沿用 v2 的 CollectionBehavior。
// net8/net9/net10(xunit v3 + MTP) 的版本见 tests/HayateOP.Tests/AssemblyInfo.cs，
// 那里改用 Xunit.v3.Parallelization(Mode = ParallelMode.None)。两代各自独立，无需条件编译。
using Xunit;

// v2：程序集级关闭跨 collection 并行
[assembly: CollectionBehavior(DisableTestParallelization = true)]
