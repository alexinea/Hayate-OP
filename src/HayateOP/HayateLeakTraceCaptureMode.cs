namespace DotNetCore.HayateOP;

/// <summary>
///  泄漏检测取证模式（PR-D L1：取证与检测解耦）。
///  泄漏检测本身（阈值判定 + LeakCount 计数）只依赖借出时间戳，零取证开销；
///  调用栈捕获由本模式独立控制，默认关闭。
/// </summary>
public enum HayateLeakTraceCaptureMode
{
    /// <summary>
    ///  不抓取调用栈（2.1 起默认）。
    ///  借出热路径零取证开销；LeakTraces 中对应条目为占位文案。
    ///  2.0 及之前默认每次借出抓取全栈（数十微秒 CPU / 10~40KB 分配），如需旧行为请显式选择 <see cref="EveryAcquire"/>。
    /// </summary>
    Off = 0,

    /// <summary>
    ///  采样抓栈：每 <see cref="HayatePoolOptions.LeakTraceSampleRate"/> 次借出抓取 1 次调用栈（第 1 次必抓）。
    ///  适合"既要发现泄漏、又要大概率定位"的场景，开销约为 <see cref="EveryAcquire"/> 的 1/N。
    /// </summary>
    Sampled = 1,

    /// <summary>
    ///  每次借出均抓取调用栈（2.0 及之前的旧行为）。
    ///  仅诊断场景显式开启：调用链深时单次开销 10~40KB 分配、微秒级 CPU。
    /// </summary>
    EveryAcquire = 2
}
