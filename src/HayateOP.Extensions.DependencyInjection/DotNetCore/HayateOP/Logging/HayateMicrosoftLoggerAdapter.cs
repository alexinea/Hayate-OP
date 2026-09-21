using System;
using Microsoft.Extensions.Logging;

namespace DotNetCore.HayateOP.Logging;

/// <summary>
/// Routes HayateOP's log surface onto a Microsoft.Extensions.Logging <see cref="ILogger"/>.
/// </summary>
/// <remarks>
/// The non-generic form is the one to use when the logger was created by category name
/// (<c>ILoggerFactory.CreateLogger(string)</c>) — which is what <see cref="HayateMicrosoftLoggerFactory"/>
/// does, so that every pool logs under its own category. The generic form routes onto
/// <c>CreateLogger&lt;T&gt;()</c> and is the one to use with an injected <see cref="ILogger{T}"/>.
/// A <c>null</c> logger is accepted and turns every call into a no-op, which is what a host with no
/// logging provider configured gets.
/// </remarks>
public class HayateMicrosoftLoggerAdapter : IHayateLogger
{
    private readonly ILogger? _logger;

    /// <summary>
    /// Wraps a logger obtained from Microsoft.Extensions.Logging.
    /// </summary>
    /// <param name="logger">The MEL logger, or <c>null</c> to discard everything written to it.</param>
    public HayateMicrosoftLoggerAdapter(ILogger? logger)
    {
        _logger = logger;
    }

    public void LogDebug(string message, params object[] args) => _logger?.LogDebug(message, args);
    public void LogInformation(string message, params object[] args) => _logger?.LogInformation(message, args);
    public void LogWarning(string message, params object[] args) => _logger?.LogWarning(message, args);
    public void LogError(Exception ex, string message, params object[] args) => _logger?.LogError(ex, message, args);
}

/// <summary>
/// Routes HayateOP's log surface onto the Microsoft.Extensions.Logging category of <typeparamref name="T"/>.
/// </summary>
/// <typeparam name="T">The type whose MEL category the logger writes under.</typeparam>
/// <remarks>
/// The category is the one <c>ILoggerFactory.CreateLogger&lt;T&gt;()</c> produces — the full display
/// name of <typeparamref name="T"/> (namespace included), which is Microsoft.Extensions.Logging's own
/// choice, not this library's. To write under a chosen category — the pool name, say — use the
/// non-generic <see cref="HayateMicrosoftLoggerAdapter"/> with
/// <c>loggerFactory.CreateLogger(categoryName)</c>, or <see cref="HayateMicrosoftLoggerFactory"/>,
/// which does exactly that.
/// </remarks>
public class HayateMicrosoftLoggerAdapter<T> : HayateMicrosoftLoggerAdapter
{
    /// <summary>
    /// Wraps the logger for the category of <typeparamref name="T"/>.
    /// </summary>
    /// <param name="logger">The MEL logger, or <c>null</c> to discard everything written to it.</param>
    public HayateMicrosoftLoggerAdapter(ILogger<T>? logger) : base(logger)
    {
    }
}