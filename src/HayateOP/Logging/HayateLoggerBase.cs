using System;

namespace DotNetCore.HayateOP.Logging;

/// <summary>
/// An implementation of <see cref="IHayateLogger"/> in which the members 3.0 added already have bodies,
/// so a class written against 2.x only has to mark the methods it already has as <c>override</c>.
/// </summary>
/// <remarks>
/// <para>
/// This class exists because 3.0 added four members to <see cref="IHayateLogger"/> and the library cannot
/// add them as default interface methods: the core package targets <c>netstandard2.0</c> and
/// <c>net48</c>, and neither runtime supports them.
/// </para>
/// <para>
/// <b>Migrating an implementation written against 2.x.</b> Change the class clause from
/// <c>: IHayateLogger</c> to <c>: HayateLoggerBase</c>. The four members that version had are
/// <c>abstract</c> here, so the compiler reports each of them as unimplemented and the fix is to mark the
/// existing method <c>override</c> — <b>no member added by 3.0 has to be implemented</b>, which is the
/// point of this class.
/// </para>
/// <para>
/// They are abstract rather than virtual-with-a-body deliberately. A virtual member would let the
/// one-clause change compile, and the class's own methods would then <i>hide</i> the inherited ones
/// instead of overriding them. An interface map is built from the class that declares the implementation,
/// so every call through <see cref="IHayateLogger"/> would reach the empty base method and the
/// implementation's logging would stop — silently, apart from a CS0114 warning. That shape was measured
/// on 2026-09-23: the migrated class compiled, and recorded nothing. An abstract member cannot be hidden,
/// so the compiler has to be answered instead of the behaviour quietly changing.
/// </para>
/// <para>
/// The members 3.0 added need no attention: <see cref="LogTrace"/> and <see cref="LogCritical"/> are
/// dropped, <see cref="IsEnabled"/> answers <c>true</c> for every level except
/// <see cref="HayateLogLevel.None"/>, and <see cref="BeginScope"/> hands back <see cref="NullScope"/>.
/// Answering <c>true</c> by default is deliberate: an implementation that never asked to filter must not
/// start dropping entries because it was rebuilt against 3.0, and the pool only skips building a message
/// when <see cref="IsEnabled"/> says the entry would be discarded.
/// </para>
/// </remarks>
public abstract class HayateLoggerBase : IHayateLogger
{
    /// <summary>
    /// A scope that does nothing, for an implementation with no scope support of its own.
    /// </summary>
    /// <remarks>
    /// Returned by the default <see cref="BeginScope"/>; also the value to return from an override that
    /// does not implement scopes, since <see cref="IHayateLogger.BeginScope"/> must not return
    /// <c>null</c>.
    /// </remarks>
    public static readonly IDisposable NullScope = new NullLogScope();

    /// <inheritdoc />
    public abstract void LogDebug(string message, params object[] args);

    /// <inheritdoc />
    public abstract void LogInformation(string message, params object[] args);

    /// <inheritdoc />
    public abstract void LogWarning(string message, params object[] args);

    /// <inheritdoc />
    public abstract void LogError(Exception ex, string message, params object[] args);

    /// <inheritdoc />
    /// <remarks>Does nothing. An implementation that wants trace entries overrides it.</remarks>
    public virtual void LogTrace(string message, params object[] args) { }

    /// <inheritdoc />
    /// <remarks>Does nothing. An implementation that wants critical entries overrides it.</remarks>
    public virtual void LogCritical(Exception ex, string message, params object[] args) { }

    /// <inheritdoc />
    /// <remarks>
    /// Defaults to <c>true</c> for every level except <see cref="HayateLogLevel.None"/>. Override it only
    /// to <em>suppress</em> entries: a <c>false</c> answer lets the pool skip building the message
    /// arguments for the per-operation debug trace, so an override that answers <c>false</c> while still
    /// writing that entry would lose it.
    /// </remarks>
    public virtual bool IsEnabled(HayateLogLevel level) => level != HayateLogLevel.None;

    /// <inheritdoc />
    public virtual IDisposable BeginScope(string message, params object[] args) => NullScope;

    private sealed class NullLogScope : IDisposable
    {
        public void Dispose() { }
    }
}
