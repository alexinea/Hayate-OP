#if DEBUG

using System;
using System.IO;
using DotNetCore.HayateOP.Logging;

namespace HayateOP.Tests.DI;

/// <summary>
/// The built-in console logger, on the only build where it has an implementation (L6).
/// </summary>
/// <remarks>
/// <para>
/// These cases compile to nothing on a Release build, because <c>DefaultHayateLogger</c> itself does:
/// everything below the <c>#if DEBUG</c> is the four <c>Console.WriteLine</c> bodies. CI runs Release,
/// so this is the one file in the suite that CI does not execute — run it with a Debug build
/// (<c>dotnet build -c Debug</c> then the produced executable) or it verifies nothing.
/// </para>
/// <para>
/// What it pins: the rendered text is produced by a single substitution pass. The logger used to hand the
/// already-substituted text to <c>string.Format</c> a second time, which could only do harm — the second
/// pass had nothing to substitute, unless a substituted value contained a brace, in which case it treated
/// the value as a template and threw <c>FormatException</c> from inside a log call.
/// </para>
/// </remarks>
[Collection("Console")]
public class DefaultHayateLoggerRenderingTests
{
    // Console.SetOut is process-wide, so these must not run alongside anything else that captures it.
    private static readonly object Gate = new();

    private static string Render(Action<IHayateLogger> write)
    {
        lock (Gate)
        {
            var previous = Console.Out;
            try
            {
                var buffer = new StringWriter();
                Console.SetOut(buffer);
                write(DefaultHayateLoggerFactory.Instance.CreateLogger("test"));
                return buffer.ToString();
            }
            finally
            {
                Console.SetOut(previous);
            }
        }
    }

    /// <summary>Strips the <c>[LEVEL] hh:mm:ss - </c> prefix so the assertion is on the message itself.</summary>
    private static string MessageOf(string line)
    {
        var marker = " - ";
        return line.Substring(line.IndexOf(marker, StringComparison.Ordinal) + marker.Length).TrimEnd('\r', '\n');
    }

    [Fact]
    public void Placeholder_ShouldBeSubstitutedOnce()
    {
        var line = Render(l => l.LogInformation("Pool {Name} created", "primary"));

        Assert.Equal("Pool primary created", MessageOf(line));
    }

    [Fact]
    public void FormatSpecifier_ShouldStillBeApplied()
    {
        // The one that a naive removal of the second pass would break: {Size:D3} is formatted by
        // RenderTemplate's own AppendFormat, not by the outer string.Format that used to follow it.
        var line = Render(l => l.LogInformation("size {Size:D3}", 7));

        Assert.Equal("size 007", MessageOf(line));
    }

    [Fact]
    public void ValueContainingBraces_ShouldBePrintedRatherThanReformatted()
    {
        // The crash this removes: after substitution the text is "payload {oops}", and the old second
        // string.Format pass read "{oops}" as a placeholder with no argument -> FormatException, thrown
        // from the line that only meant to report something.
        var line = Render(l => l.LogInformation("payload {Body}", "{oops}"));

        Assert.Equal("payload {oops}", MessageOf(line));
    }

    [Fact]
    public void MoreArgumentsThanPlaceholders_ShouldLeaveTheTextAlone()
    {
        var line = Render(l => l.LogInformation("nothing to substitute", 1, 2));

        Assert.Equal("nothing to substitute", MessageOf(line));
    }

    [Fact]
    public void EveryLevel_ShouldRenderThroughTheSameSinglePass()
    {
        foreach (var (write, expected) in new (Action<IHayateLogger>, string)[]
                 {
                     (l => l.LogDebug("d {A}", 1), "d 1"),
                     (l => l.LogInformation("i {A}", 1), "i 1"),
                     (l => l.LogWarning("w {A}", 1), "w 1"),
                     (l => l.LogError(new InvalidOperationException("x"), "e {A}", 1), "e 1")
                 })
        {
            Assert.Equal(expected, MessageOf(Render(write).Split('\n')[0]));
        }
    }
}

#endif
