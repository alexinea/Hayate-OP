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
/// everything below the <c>#if DEBUG</c> is the four <c>Console.WriteLine</c> bodies. The Release jobs
/// therefore cannot exercise them at all; CI's <c>test-debug</c> job builds this suite in Debug and runs
/// the produced executable, which is the only place these cases run. Locally, run it the same way
/// (<c>dotnet build -c Debug</c> then the executable) or it verifies nothing.
/// </para>
/// <para>
/// These cases capture the process-wide <c>Console.Out</c>, so they are only sound if nothing else writes
/// to <c>Console</c> while the buffer is installed — and in a Debug build every other class that creates a
/// pool writes to it. That is why this assembly disables test parallelization wholesale; see
/// <c>AssemblyInfo.cs</c> next to this file for the failure it prevents.
/// </para>
/// <para>
/// What it pins: the rendered text is produced by a single substitution pass. The logger used to hand the
/// already-substituted text to <c>string.Format</c> a second time, which could only do harm — the second
/// pass had nothing to substitute, unless a substituted value contained a brace, in which case it treated
/// the value as a template and threw <c>FormatException</c> from inside a log call.
/// </para>
/// </remarks>
// Inert while this assembly disables parallelization (AssemblyInfo.cs), kept so the intent survives if it
// is ever re-enabled: Console.SetOut is process-wide, so nothing else may write to Console while we capture.
[Collection("Console")]
public class DefaultHayateLoggerRenderingTests
{
    // A class's cases already run sequentially, so this only guards against a future change that runs them
    // in parallel; the assembly-level fence, not this lock, is what keeps other classes out of the window.
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
