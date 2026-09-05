// 测试套件级"围栏"（P0/R2, R3）：
// 本程序集以对象池并发/线程压测为主，多个类都会驱动 ThreadPool、GC、SpinLock、
// 后台驱逐/校验回调与扩缩容冷却。默认跨类并行会让重并发类与轻量确定性用例互相竞争 CPU，
// 既造成偶发超时/时序 flaky，也在宿主机器上叠加 CPU 空转。
//
// 因此在此**程序集级关闭测试并行化**：所有 collection 串行执行，作为最强的确定性围栏。
// 其代价是全套 ~8min 内基本串行；换取的是：任一用例不与它类并发争抢，CPU 占用收敛、
// 结果可复现。若日后需要吞吐，可在并发类稳定后再改为按 collection 粒度精细并行，
// 而把轻量类留在并行组。
//
// 说明：xunit v2 (2.9.3) + v3 VSTest runner 组合下，逐用例 [Fact(Timeout)] 不可用，
// 故"超时即杀"交由运行层 --blame-hang-timeout / 外部看门狗实现（见 run 批次脚本）。
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]
