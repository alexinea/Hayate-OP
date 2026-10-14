namespace DotNetCore.HayateOP;

/// <summary>
/// Which idle object a borrow is served (2.8; extended by G-21).
/// The idle list is kept in return order — the tail is always the most recently returned object — and
/// no strategy ever reorders it. <see cref="Fifo"/> and <see cref="Lifo"/> therefore only choose an end
/// of that list, while <see cref="Lru"/> and <see cref="Priority"/> walk it and pick a node; either way
/// the list itself, and so the end the eviction scan samples, is left exactly as it was.
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
    Lifo = 1,

    /// <summary>
    /// Least recently used: the borrow takes the object that was borrowed longest ago — the smallest
    /// <see cref="HayateObject{T}.LastBorrowedAt"/>, with an object that has never been borrowed
    /// (timestamp zero) ranking lowest of all. This orders by <i>borrow</i> where <see cref="Fifo"/>
    /// orders by <i>return</i>, so an object borrowed early and returned late is served before one
    /// borrowed and returned quickly; the two agree only while every object is returned in the order
    /// it was borrowed.<br />
    /// The idle list is not reordered and the borrow walks it, so the eviction sampler keeps seeing
    /// the end it has always seen. Applies to the general-purpose engine only: the lean fast path
    /// keeps no ordered idle list, the lock-free store keeps no order at all, and the per-shard front
    /// tier is consulted before the shard so it cannot take part in an order either — a pool that
    /// combines this value with any of the three fails the build rather than honouring it in part.
    /// </summary>
    Lru = 2,

    /// <summary>
    /// Highest priority first: the borrow takes the object the
    /// <see cref="HayatePoolBuilder{T}.WithBorrowPriority"/> comparer ranks highest, where a positive
    /// <see cref="IComparer{T}.Compare"/> result means "borrow this one first". Objects the comparer
    /// calls equal are served oldest-returned first, the same end <see cref="Fifo"/> would have served,
    /// so the order is deterministic without the comparer having to be a total one.<br />
    /// The comparer is a builder-time argument rather than an option, because <see cref="HayatePoolOptions"/>
    /// is not generic: selecting this value without supplying one fails the build. The idle list is not
    /// reordered, so the eviction sampler is unaffected. Like <see cref="Lru"/> this is unavailable on
    /// the lean fast path, on the lock-free store and alongside the per-shard front tier.
    /// </summary>
    Priority = 3
}
