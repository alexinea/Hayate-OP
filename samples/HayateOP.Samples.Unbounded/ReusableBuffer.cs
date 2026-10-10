namespace DotNetCore.HayateOP.Samples.Unbounded;

/// <summary>
/// A reusable, array-like text buffer used by the Unbounded sample to show ownership-style pooling:
/// the pool creates one on demand and the caller fills it, then optionally returns it for reuse.
/// </summary>
internal sealed class ReusableBuffer
{
    // The backing builder is cleared on return so a reused buffer never leaks the previous contents.
    private System.Text.StringBuilder _sb = new();

    /// <summary>
    /// A caller-assigned label (e.g. "factory-made") carried through the buffer's lifetime and shown
    /// in <see cref="ToString"/>, handy for telling pooled instances apart in the sample output.
    /// </summary>
    public string Tag { get; set; } = "";

    /// <summary>
    /// Empties the buffer so it can be reused for a new message.
    /// </summary>
    public void Reset() => _sb.Clear();

    /// <summary>
    /// Appends <paramref name="s"/> to the buffer.
    /// </summary>
    /// <param name="s">The text to append.</param>
    public void Append(string s) => _sb.Append(s);

    /// <summary>
    /// Renders the buffer as <c>[Tag] contents</c>.
    /// </summary>
    /// <returns>The tag followed by the accumulated text.</returns>
    public override string ToString() => $"[{Tag}] {_sb}";
}
