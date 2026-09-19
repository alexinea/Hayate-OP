namespace DotNetCore.HayateOP;

/// <summary>
/// The end of a shard's idle list a borrow is served from (2.8).
/// The idle list is kept in return order — the tail is always the most recently returned object —
/// so this switch only chooses which end is handed out next. It never reorders the list, and it
/// does not change what the eviction scan samples.
/// </summary>
public enum HayateBorrowStrategy
{
    /// <summary>
    /// First in, first out (default): the borrow takes the oldest returned object, that is the head
    /// of the idle list. This reproduces the behaviour of every release before the switch existed.
    /// </summary>
    Fifo = 0,

    /// <summary>
    /// Last in, first out: the borrow takes the most recently returned object, that is the tail of
    /// the idle list. Handing back the object that came in last keeps a small working set hot in the
    /// CPU caches, and it is the order the reference CHOPIN pool hard-codes. Objects that are never
    /// picked stay at the head and age out through the ordinary eviction rules.<br />
    /// Applies to the general-purpose engine only. The lean fast path keeps no ordered idle list, so
    /// a lean pool rejects this value at build time rather than accepting a knob it cannot honour.
    /// </summary>
    Lifo = 1
}
