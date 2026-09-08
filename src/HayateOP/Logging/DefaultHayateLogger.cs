using System;
using System.Text;

namespace DotNetCore.HayateOP.Logging;

internal class DefaultHayateLogger : IHayateLogger
{
#if DEBUG
    static DefaultHayateLogger()
    {
        Console.InputEncoding = Encoding.UTF8;
        Console.OutputEncoding = Encoding.UTF8;
    }
#endif

#if !DEBUG
        public void LogInformation(string message, params object[] args){ }

        public void LogWarning(string message, params object[] args){ }

        public void LogError(Exception ex, string message, params object[] args){ }

        public void LogDebug(string message, params object[] args){ }
#endif

#if DEBUG
    public void LogInformation(string message, params object[] args)
    {
        var processedMessage = RenderTemplate(message, args);
        Console.WriteLine($"[INFO] {DateTime.Now:HH:mm:ss} - {string.Format(processedMessage, args)}");
    }

    public void LogWarning(string message, params object[] args)
    {
        var processedMessage = RenderTemplate(message, args);
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine($"[WARN] {DateTime.Now:HH:mm:ss} - {string.Format(processedMessage, args)}");
        Console.ResetColor();
    }

    public void LogError(Exception ex, string message, params object[] args)
    {
        var processedMessage = RenderTemplate(message, args);
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"[ERROR] {DateTime.Now:HH:mm:ss} - {string.Format(processedMessage, args)}");
        if (ex != null)
        {
            Console.WriteLine($"[ERROR] {DateTime.Now:HH:mm:ss} - Exception: {ex.GetType().Name}\n{ex.StackTrace}");
        }
        Console.ResetColor();
    }

    public void LogDebug(string message, params object[] args)
    {
        var processedMessage = RenderTemplate(message, args);
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine($"[DEBUG] {DateTime.Now:HH:mm:ss} - {string.Format(processedMessage, args)}");
        Console.ResetColor();
    }

#endif

    /// <summary>
    /// T13：取代原 NormalizeFormatString 正则改写。
    /// 按占位符出现顺序把参数依次代入：{Name} → 参数值，{Name:F2} → 应用格式说明符。
    /// 池内模板约定：每个命名占位符按出现顺序对应一个参数（与调用点一一对应），
    /// 生产结构化日志请使用 HayateMicrosoftLoggerAdapter{T}（MEL 原生支持命名模板）。
    /// 无正则、单趟扫描，消除每次日志调用的 Regex 开销。
    /// </summary>
    private static string RenderTemplate(string message, object[] args)
    {
        if (args is null || args.Length == 0) return message;

        var sb = new StringBuilder(message.Length + 32);
        var argIndex = 0;

        for (var i = 0; i < message.Length; i++)
        {
            if (message[i] == '{' && argIndex < args.Length)
            {
                var close = message.IndexOf('}', i + 1);
                if (close > i + 1)
                {
                    var placeholder = message.Substring(i + 1, close - i - 1);
                    var colon = placeholder.IndexOf(':');
                    if (colon >= 0)
                    {
                        sb.AppendFormat($"{{0{placeholder.Substring(colon)}}}", args[argIndex++]);
                    }
                    else
                    {
                        sb.Append(args[argIndex++]);
                    }

                    i = close;
                    continue;
                }
            }

            sb.Append(message[i]);
        }

        return sb.ToString();
    }
}