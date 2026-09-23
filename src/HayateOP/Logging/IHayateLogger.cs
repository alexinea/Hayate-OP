using System;

namespace DotNetCore.HayateOP.Logging;

/// <summary>
/// The logging surface a pool writes to.
/// </summary>
/// <remarks>
/// <para>
/// 3.0 added <see cref="LogTrace"/>, <see cref="LogCritical"/>, <see cref="IsEnabled"/> and
/// <see cref="BeginScope"/>. Adding members to an interface is a source-breaking change for every
/// implementation outside this repository, and the library cannot soften it with a default interface
/// method: the core package targets <c>netstandard2.0</c> and <c>net48</c>, and neither runtime
/// supports them. An implementation written against 2.x migrates by deriving from
/// <see cref="HayateLoggerBase"/> instead of implementing this interface directly — see the remarks
/// there for what that costs.
/// </para>
/// <para>
/// <see cref="IsEnabled"/> is a permission check, not a report. A caller may skip building the message
/// and its arguments when it answers <c>false</c>, which is what keeps the per-operation pool trace off
/// the allocation path of a logger that would discard the entry anyway. An implementation that does not
/// filter answers <c>true</c> for every level except <see cref="HayateLogLevel.None"/>.
/// </para>
/// <para>
/// The pool consults it for the per-operation borrow and release trace only — that is the one place it
/// would otherwise build a message on every operation. Lifecycle entries (construction, pre-warm,
/// scale-up, eviction, capacity alarms) are written unconditionally, so answering <c>false</c> never
/// silences them; they are rare enough that the argument array is not worth a gate.
/// </para>
/// <para>
/// The message and argument convention is the one <c>DefaultHayateLogger</c> established: named
/// placeholders in template order, each mapping to one argument, with an optional format specifier after
/// a colon (<c>"{Count:F2}"</c>). A Microsoft.Extensions.Logging bridge hands the template through
/// unchanged, so MEL keeps the named properties it would have had.
/// </para>
/// </remarks>
public interface IHayateLogger
{
    /// <summary>
    /// Writes a trace entry.
    /// </summary>
    /// <param name="message">The message template.</param>
    /// <param name="args">One argument per named placeholder, in template order.</param>
    void LogTrace(string message, params object[] args);

    /// <summary>
    /// Writes a debug entry. The pool's per-operation borrow and release trace uses this level.
    /// </summary>
    /// <param name="message">The message template.</param>
    /// <param name="args">One argument per named placeholder, in template order.</param>
    void LogDebug(string message, params object[] args);

    /// <summary>
    /// Writes an informational entry. Lifecycle events use this level.
    /// </summary>
    /// <param name="message">The message template.</param>
    /// <param name="args">One argument per named placeholder, in template order.</param>
    void LogInformation(string message, params object[] args);

    /// <summary>
    /// Writes a warning entry.
    /// </summary>
    /// <param name="message">The message template.</param>
    /// <param name="args">One argument per named placeholder, in template order.</param>
    void LogWarning(string message, params object[] args);

    /// <summary>
    /// Writes an error entry.
    /// </summary>
    /// <param name="ex">The failure, or <c>null</c> when there is nothing to attach.</param>
    /// <param name="message">The message template.</param>
    /// <param name="args">One argument per named placeholder, in template order.</param>
    void LogError(Exception ex, string message, params object[] args);

    /// <summary>
    /// Writes a critical entry: the pool or the process cannot continue.
    /// </summary>
    /// <param name="ex">The failure, or <c>null</c> when there is nothing to attach.</param>
    /// <param name="message">The message template.</param>
    /// <param name="args">One argument per named placeholder, in template order.</param>
    void LogCritical(Exception ex, string message, params object[] args);

    /// <summary>
    /// Answers whether an entry at <paramref name="level"/> would be written.
    /// </summary>
    /// <param name="level">The level to test.</param>
    /// <returns><c>true</c> when an entry at that level would be written, <c>false</c> otherwise --
    /// and always <c>false</c> for <see cref="HayateLogLevel.None"/>.</returns>
    bool IsEnabled(HayateLogLevel level);

    /// <summary>
    /// Opens a scope that groups the entries written until the returned handle is disposed.
    /// </summary>
    /// <param name="message">The scope description, in the same template form as the log methods.</param>
    /// <param name="args">One argument per named placeholder, in template order.</param>
    /// <returns>The handle that ends the scope. Must not be <c>null</c>; an implementation without scope
    /// support returns <see cref="HayateLoggerBase.NullScope"/>.</returns>
    IDisposable BeginScope(string message, params object[] args);
}
