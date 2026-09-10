namespace DotNetCore.HayateOP;

/// <summary>
/// The leak-detection capture mode (decoupling evidence capture from leak detection itself).
/// Leak detection (threshold check + LeakCount counter) relies only on the borrow timestamp and
/// incurs no capture overhead; stack-frame capture is controlled independently by this mode and is
/// off by default.
/// </summary>
public enum HayateLeakTraceCaptureMode
{
    /// <summary>
    /// Do not capture the call stack (default since 2.1).
    /// Zero capture overhead on the borrow hot path; the corresponding entry in LeakTraces is a placeholder.
    /// Before 2.0 the default was to capture the full stack on every borrow (tens of microseconds CPU / 10~40KB allocation); opt in explicitly via <see cref="EveryAcquire"/> for the old behavior.
    /// </summary>
    Off = 0,

    /// <summary>
    /// Sampled capture: capture the call stack on 1 of every <see cref="HayatePoolOptions.LeakTraceSampleRate"/> borrows (the first borrow is always captured).
    /// Suits scenarios that want both leak discovery and high-probability localization; the overhead is roughly 1/N of <see cref="EveryAcquire"/>.
    /// </summary>
    Sampled = 1,

    /// <summary>
    /// Capture the call stack on every borrow (the old behavior before 2.0).
    /// Enable only for diagnostics: when the call chain is deep, a single capture costs 10~40KB allocation and microseconds of CPU.
    /// </summary>
    EveryAcquire = 2
}
