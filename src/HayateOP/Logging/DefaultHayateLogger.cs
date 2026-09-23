using System;
using System.Text;

namespace DotNetCore.HayateOP.Logging;

internal sealed class DefaultHayateLogger : HayateLoggerBase
{
#if DEBUG
    static DefaultHayateLogger()
    {
        Console.InputEncoding = Encoding.UTF8;
        Console.OutputEncoding = Encoding.UTF8;
    }

    /// <summary>
    /// The debug console logger writes every level, so it is enabled for all of them and the pool never
    /// skips building a message on its behalf.
    /// </summary>
    public override bool IsEnabled(HayateLogLevel level) => level != HayateLogLevel.None;

    public override void LogTrace(string message, params object[] args)
    {
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine($"[TRACE] {DateTime.Now:HH:mm:ss} - {RenderTemplate(message, args)}");
        Console.ResetColor();
    }

    public override void LogDebug(string message, params object[] args)
    {
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine($"[DEBUG] {DateTime.Now:HH:mm:ss} - {RenderTemplate(message, args)}");
        Console.ResetColor();
    }

    public override void LogInformation(string message, params object[] args)
    {
        Console.WriteLine($"[INFO] {DateTime.Now:HH:mm:ss} - {RenderTemplate(message, args)}");
    }

    public override void LogWarning(string message, params object[] args)
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine($"[WARN] {DateTime.Now:HH:mm:ss} - {RenderTemplate(message, args)}");
        Console.ResetColor();
    }

    public override void LogError(Exception ex, string message, params object[] args)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"[ERROR] {DateTime.Now:HH:mm:ss} - {RenderTemplate(message, args)}");
        if (ex != null)
        {
            Console.WriteLine($"[ERROR] {DateTime.Now:HH:mm:ss} - Exception: {ex.GetType().Name}\n{ex.StackTrace}");
        }
        Console.ResetColor();
    }

    public override void LogCritical(Exception ex, string message, params object[] args)
    {
        Console.ForegroundColor = ConsoleColor.Magenta;
        Console.WriteLine($"[CRITICAL] {DateTime.Now:HH:mm:ss} - {RenderTemplate(message, args)}");
        if (ex != null)
        {
            Console.WriteLine($"[CRITICAL] {DateTime.Now:HH:mm:ss} - Exception: {ex.GetType().Name}\n{ex.StackTrace}");
        }
        Console.ResetColor();
    }
#else
    /// <summary>
    /// A release build has no console sink: every level is discarded, so every level is disabled.
    /// Answering <c>false</c> is what lets the borrow and release paths skip building the message
    /// arguments — the entry was going to be dropped either way, and this way it is dropped before the
    /// params array and the boxed arguments are allocated.
    /// </summary>
    public override bool IsEnabled(HayateLogLevel level) => false;

    // The four members HayateLoggerBase declares abstract; LogTrace, LogCritical and BeginScope are
    // inherited from it and already do nothing.
    public override void LogDebug(string message, params object[] args) { }
    public override void LogInformation(string message, params object[] args) { }
    public override void LogWarning(string message, params object[] args) { }
    public override void LogError(Exception ex, string message, params object[] args) { }
#endif

    /// <summary>
    /// Replaces the original NormalizeFormatString regex rewrite. Substitutes arguments in placeholder
    /// order: {Name} -> argument value, {Name:F2} -> applies the format specifier. The in-pool template
    /// convention is that each named placeholder maps to one argument in order (matching the call site).
    /// For production structured logging use HayateMicrosoftLoggerAdapter{T} (MEL natively supports named templates).
    /// No regex, single-pass scan -- eliminates the per-call Regex overhead.
    /// </summary>
    /// <remarks>
    /// The result is written out as-is. It used to be handed to <see cref="string.Format(string, object[])"/>
    /// a second time, which could only do harm: the placeholders are already substituted by the time this
    /// returns, so the second pass had nothing to do — unless a substituted *value* happened to contain a
    /// brace, in which case it treated the value as a template and threw <see cref="FormatException"/> from
    /// inside a log call. A pool logging an object whose <c>ToString()</c> contains braces would therefore
    /// crash on the line that was only meant to report something. Removing the second pass changes nothing
    /// for a value without braces and turns that crash into the text it was trying to print.
    /// </remarks>
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
