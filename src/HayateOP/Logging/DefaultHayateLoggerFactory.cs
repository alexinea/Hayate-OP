using System;

namespace DotNetCore.HayateOP.Logging;

/// <summary>
/// 默认日志工厂（M19）：忽略分类名，返回与 2.4 及之前等价的内建日志器。
/// </summary>
public sealed class DefaultHayateLoggerFactory : IHayateLoggerFactory
{
    /// <summary>共享实例（无状态，可安全复用）。</summary>
    public static readonly DefaultHayateLoggerFactory Instance = new();

    /// <inheritdoc />
    public IHayateLogger CreateLogger(string categoryName) => new DefaultHayateLogger();
}

/// <summary>
/// 委托式日志工厂（M19）：把分类名交给用户提供的委托，便于把池日志桥接到
/// MEL <c>ILoggerFactory</c>、Serilog、NLog 等任意后端。
/// </summary>
public sealed class DelegateHayateLoggerFactory : IHayateLoggerFactory
{
    private readonly Func<string, IHayateLogger> _factory;

    /// <summary>
    /// 创建委托式工厂。
    /// </summary>
    /// <param name="factory">分类名 → 日志器 的映射委托。</param>
    public DelegateHayateLoggerFactory(Func<string, IHayateLogger> factory)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
    }

    /// <inheritdoc />
    public IHayateLogger CreateLogger(string categoryName)
        => _factory(categoryName) ?? throw new InvalidOperationException(
            "IHayateLoggerFactory returned null; a non-null IHayateLogger is required.");
}
