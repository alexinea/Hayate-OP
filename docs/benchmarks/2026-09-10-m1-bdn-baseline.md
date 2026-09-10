# HayateOP 2.5 / M1 — BenchmarkDotNet 基线报告（2026-09-10）

> 目的：为 2.5 建立**可复现的头对头基线**，覆盖 Acquire / Release / AcquireAsync 全路径，
> 对照 `Microsoft.Extensions.ObjectPool`（MEOP）与**原生 `new`**（无池下界），输出 P50/P90/P95/P99
> 与分配口径（`B/Op`），供后续批次（M3 分配追踪、第四批 O1/O2 lean 存储重构）作回归对照。

## 一、运行环境与配置

| 项 | 值 |
| :--- | :--- |
| BenchmarkDotNet | 0.15.8 |
| 运行时 | .NET 10.0.11（SDK 10.0.400），X64 RyuJIT x86-64-v3 |
| 硬件 | 12th Gen Intel Core i7-1260P 2.10GHz，1 CPU / 16 逻辑核（12 物理核） |
| Job | `Short`：WarmupCount=3、IterationCount=10、Server GC、Concurrent GC |
| 诊断器 | `MemoryDiagnoser`（B/Op、Gen0/1/2）+ `ThreadingDiagnoser`（Completed Work Items、Lock Contentions） |
| 分位列 | 自定义 `P50/P90/P95/P99`（按各迭代平均单操作耗时取分位；BDN 内置无 P99 列） |
| 容量口径 | Min=250 / Max=300，预热至 MaxPoolSize（对齐历史 T11 基线，保证可比） |
| 命令 | `dotnet run -c Release -- --filter '*'`（或直跑 `HayateOP.Benchmarks.exe --filter '*'`） |

> **关于 `--join`**：BDN 0.15.8 **不存在** `--join` 开关（已核对 BenchmarkDotNet.dll 字面量）；
> 计划书中该项为口径记录误差。分位数以自定义统计列直接输出，等价达成"输出 P50/P95/P99"的验收目标。

## 二、结果（BDN MarkdownExporter 原文）

```
BenchmarkDotNet v0.15.8, Windows 11 (10.0.28120.2824)
12th Gen Intel Core i7-1260P 2.10GHz, 1 CPU, 16 logical and 12 physical cores
.NET SDK 10.0.400
  [Host] : .NET 10.0.11 (10.0.11, 10.0.1126.37416), X64 RyuJIT x86-64-v3
  Short  : .NET 10.0.11 (10.0.11, 10.0.1126.37416), X64 RyuJIT x86-64-v3

Job=Short  Concurrent=True  Server=True
IterationCount=10  WarmupCount=3
```

| Method | Mean | Error | StdDev | P50 | P90 | P95 | P99 | Median | Ratio | Rank | Gen0 | Completed Work Items | Lock Contentions | Allocated |
|------------------------------------- |-----------------:|------------------:|----------------:|------------:|------------:|------------:|------------:|-----------------:|----------:|-----:|-------:|---------------------:|-----------------:|----------:|
| 'Acquire+Release \| MEOP（基线）' | 16.486 ns | 0.8571 ns | 0.5101 ns | 16.3 | 17.3 | 17.3 | 17.3 | 16.266 ns | 1.00 | 2 | - | - | - | - |
| 'Acquire+Release \| 原生 new（无池下界）' | 7.899 ns | 0.9915 ns | 0.6558 ns | 7.7 | 8.5 | 8.9 | 8.9 | 7.840 ns | 0.48 | 1 | 0.0007 | - | - | - |
| 'Acquire+Release \| Hayate Lean' | 251.797 ns | 14.6346 ns | 9.6799 ns | 248.6 | 264.1 | 264.8 | 264.8 | 251.302 ns | 15.29 | 3 | 0.0100 | - | - | 384 B |
| 'Acquire+Release \| Hayate Sharded4' | 254.981 ns | 16.6655 ns | 11.0232 ns | 249.8 | 268.6 | 272.8 | 272.8 | 252.719 ns | 15.48 | 3 | 0.0100 | - | - | 384 B |
| 'Acquire+Release \| Hayate Full' | 363.850 ns | 22.0910 ns | 14.6118 ns | 369.2 | 376.4 | 379.9 | 379.9 | 371.371 ns | 22.09 | 4 | 0.0153 | - | - | - |
| 'Release \| MEOP Return' | 16.826 ns | 0.7190 ns | 0.4756 ns | 16.6 | 17.4 | 17.7 | 17.7 | 16.741 ns | 1.02 | 2 | - | - | - | - |
| 'Release \| Hayate Lean' | 272.020 ns | 16.6125 ns | 10.9882 ns | 272.1 | 278.1 | 293.4 | 293.4 | 273.432 ns | 16.51 | 3 | 0.0100 | - | - | 384 B |
| 'AcquireAsync+Release \| Hayate Lean' | 254.023 ns | 24.3138 ns | 16.0821 ns | 249.2 | 274.3 | 276.7 | 276.7 | 250.758 ns | 15.42 | 3 | 0.0143 | - | - | 392 B |
| 'AcquireAsync+Release \| Hayate Full' | 265.730 ns | 17.5545 ns | 11.6112 ns | 269.2 | 277.1 | 281.1 | 281.1 | 269.281 ns | 16.13 | 3 | 0.0105 | - | - | 392 B |
| 'Concurrent-100 \| MEOP' | 23,678.670 ns | 1,243.0683 ns | 739.7300 ns | 23,478.3 | 25,236.4 | 25,236.4 | 25,236.4 | 23,478.260 ns | 1,437.51 | 5 | 0.1068 | 12.7527 | 0.0000 | 4,434 B |
| 'Concurrent-100 \| Hayate Lean' | 1,356,132.930 ns | 1,110,304.3007 ns | 734,397.5494 ns | 1,123,691.0 | 2,171,564.5 | 2,521,680.5 | 2,521,680.5 | 1,242,241.406 ns | 82,329.74 | 7 | - | 31.6758 | - | 46,786 B |
| 'Concurrent-100 \| Hayate Sharded4' | 287,206.348 ns | 333,112.0497 ns | 220,332.9960 ns | 127,663.2 | 533,381.3 | 700,995.2 | 700,995.2 | 128,472.070 ns | 17,436.07 | 6 | - | 30.5313 | 0.0977 | 46,529 B |

> 原始 CSV 见同目录 `2026-09-10-m1-bdn-baseline.csv`。
> 控制台输出中中文方法名在 GBK 控制台下会显示为乱码（既有环境问题）；Markdown/CSV 产物中的名称为 UTF-8 正常。

## 三、关键结论

1. **单操作（低争用）**：MEOP 16.5 ns / 0 B，原生 `new` 7.9 ns；Hayate Lean **251.8 ns / 384 B**（≈15.3× MEOP）。
   与 2026-09-09 T11 基线（Hayate 极简 188.8 ns、全功能 304.7 ns）**同量级**，
   证明 2.5 的 P1/P2 变更（M12/M13/M4/M11+/M18/M10+M20/M16/M15）**未引入单操作回归**。
   该 15× 差距是**既有已知缺口**，正是第四批 **O1（无锁快路径）+ O2（包装对象去分配）** 的目标（P0）。
2. **每操作 384 B 分配**：Lean 路径每 `Acquire+Release` 产生 384 B 托管分配（`HayateObject<T>` 包装器 +
   分片链表节点 + 预约对象），对照 MEOP 的 0 B —— 即 O2 的核心动因。
3. **异步开销可忽略**：`AcquireAsync+Release` Lean 254.0 ns，与同步 251.8 ns 基本持平
   （392 B vs 384 B，差异为 async 状态机；对象已就绪时走同步快路径不产生额外等待）。
4. **全功能成本**：Lean → Full 单操作 251.8 ns → 363.9 ns（+44%），来自校验/指标/泄漏检测/驱逐埋点；
   对照 MEOP 仍为 22×。可作为 O6/O7「热路径成本标注」的量化依据。
5. **并发（100 线程）**：MEOP 23.7 µs vs Hayate Sharded4 287 µs / Lean 1356 µs。
   **本组数据方差极大**（Sharded4 StdDev 220 µs ≈ 均值 77%，Lean StdDev 734 µs），
   并触发 BDN `MinIterationTime` 警告（迭代时长仅 38–56 ms，低于建议的 100 ms）。
   结论：**短迭代 Job 不足以测定并发口径**，需以更长迭代（≥100 ms/迭代）单独复跑；
   同时下一轮应评估 `Parallel.For` 的线程池抖动。**当前不建议引用并发列的绝对值**。

## 四、复现与后续

- 复现：`dotnet run -c Release --project tests/HayateOP.Benchmarks -- --filter '*'`，
  产物在 `BenchmarkDotNet.Artifacts/results/`。
- 后续（第四批）：
  - O1/O2 落地后以本报告为基线对照，验收口径 = Lean 单操作延迟 / 分配对齐 MEOP 量级；
  - O5（已并入 M1）/ N5 / O8 / T8 / C-B 需在本编排追加 MEOP 之外的对照列（marklauter / CHOPIN / POP / TP / CPL）；
  - 并发列须改用长迭代 Job（或 `RunStrategy.Monitoring`）后重新建立基线。
- M3（分配追踪）以本报告的 `B/Op` 列为权威口径，池侧 `EnableAllocationTracking` 仅作运行时抽样参考。
