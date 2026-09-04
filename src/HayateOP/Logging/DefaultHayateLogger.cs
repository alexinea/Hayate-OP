using System;
using System.Text;
using System.Text.RegularExpressions;

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
        var processedMessage = NormalizeFormatString(message);
        Console.WriteLine($"[INFO] {DateTime.Now:HH:mm:ss} - {string.Format(processedMessage, args)}");
    }

    public void LogWarning(string message, params object[] args)
    {
        var processedMessage = NormalizeFormatString(message);
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine($"[WARN] {DateTime.Now:HH:mm:ss} - {string.Format(processedMessage, args)}");
        Console.ResetColor();
    }

    public void LogError(Exception ex, string message, params object[] args)
    {
        var processedMessage = NormalizeFormatString(message);
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
        var processedMessage = NormalizeFormatString(message);
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine($"[DEBUG] {DateTime.Now:HH:mm:ss} - {string.Format(processedMessage, args)}");
        Console.ResetColor();
    }

#endif

    private static string NormalizeFormatString(string message)
    {
        int index = 0;
        // 正则最终版说明：
        // \{          -> 匹配左花括号
        // ([@$]?[a-zA-Z0-9_]+) -> 捕获组1：匹配可选的 @ 或 $ 开头，后跟名称
        // (?::        -> 非捕获组：匹配冒号（如果存在）
        //   ([^}]+)   -> 捕获组2：匹配格式说明符 (F2)
        // )?          -> 格式说明符是可选的
        // \}          -> 匹配右花括号
        return Regex.Replace(message, @"\{([@$]?[a-zA-Z0-9_]+)(?::([^}]+))?\}", m =>
        {
            // 注意：这里我们直接丢弃了 @ 或 $ 前缀，因为 string.Format 不需要它们
            // 如果是真正的结构化日志库，会利用 @ 符号决定是否序列化，但在这个简单控制台实现中，我们只需要ToString()
            string format = m.Groups[2].Success ? $":{m.Groups[2].Value}" : "";

            return $"{{{index++}{format}}}";
        });
    }
}