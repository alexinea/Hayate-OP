using DotNetCore.HayateOP.Metrics;

namespace DotNetCore.HayateOP.Samples.Scaling;

/// <summary>
/// Captures scaling events so the sample can prove the pool grew and shrank on its own.
/// </summary>
/// <remarks>
/// This implements <see cref="IHayateMetrics"/>. Only <see cref="RecordPoolScaled"/> is meaningful here;
/// the acquire/release/miss hooks are intentionally empty because the sample only wants to observe scaling.
/// <para>
/// IMPORTANT: a custom <see cref="IHayateMetrics"/> only receives events when metrics collection is enabled.
/// The pool MUST be built with <c>WithEnableMetrics(true)</c> (and then <c>WithMetrics(this)</c>), or
/// <c>WithMetrics(...)</c> throws at build time. We honor that contract in <c>Program.cs</c>.
/// </para>
/// </remarks>
internal sealed class ScalingCollector : IHayateMetrics
{
    /// <summary>The recorded "<c>action old -> new</c>" scaling strings, in the order they fired.</summary>
    public List<string> ScaledEvents { get; } = new();

    public void RecordObjectAcquired(string poolName, object item, double elapsedMilliseconds) { }
    public void RecordObjectReleased(string poolName, object item, bool isValid) { }
    public void RecordObjectMiss(string poolName) { }

    /// <summary>
    /// Called by the pool whenever it scales up or down; records "<c>action oldSize -> newSize</c>".
    /// </summary>
    /// <param name="poolName">The pool that scaled.</param>
    /// <param name="action">The scaling action, e.g. <c>"scale-up"</c> / <c>"scale-down"</c>.</param>
    /// <param name="oldSize">The pool size before scaling.</param>
    /// <param name="newSize">The pool size after scaling.</param>
    public void RecordPoolScaled(string poolName, string action, int oldSize, int newSize)
        => ScaledEvents.Add($"{action} {oldSize} -> {newSize}");
}
