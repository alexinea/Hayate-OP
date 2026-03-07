using Microsoft.Extensions.Logging;

namespace DotNetCore.HayateOP.Logging;

public class HayateMicrosoftLoggerAdapter<T> : IHayateLogger
{
    private readonly ILogger<T> _logger;

    public HayateMicrosoftLoggerAdapter(ILogger<T> logger)
    {
        _logger = logger;
    }

    public void LogDebug(string message, params object[] args) => _logger?.LogDebug(message, args);
    public void LogInformation(string message, params object[] args) => _logger?.LogInformation(message, args);
    public void LogWarning(string message, params object[] args) => _logger?.LogWarning(message, args);
    public void LogError(Exception ex, string message, params object[] args) => _logger?.LogError(ex, message, args);
}