using DotNetCore.HayateOP.Logging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace DotNetCore.HayateOP;

public partial class HayatePoolBasic<T>
{
    internal class Shard
    {
        private readonly IHayateLogger _logger;

        // Free-object linked list: Add appends to the tail and TryTake removes from the head, naturally preserving FIFO order.
        // Remove performs an O(1) unlink via HayateObject<T>.Node.
        // The old implementation rebuilt the queue with ConcurrentQueue + "ToList -> Remove -> Clear -> Enqueue";
        // during the rebuild window concurrent TryTake / Add could lose objects or produce duplicates, so it was replaced entirely with a linked list.
        private readonly LinkedList<HayateObject<T>> _list = new();

        // Per-shard registry (T -> wrapper object); previously a single pool-wide _objectMap.
        // Borrowed objects live in the pool for a long time, and all shards wrote to the same table, so the bucket array grew with concurrency peaks and never shrank back, leaving the memory peak unconverged.
        // After splitting per shard, registry entries are pinned to the shard where the object was created / destroyed, so capacity aligns with the shard capacity and truly reclaims on destruction.
        // Objects always round-trip back to their creating shard (Acquire records ShardIndex; Release returns to that shard),
        // so a given key appears in only one shard's registry -- the key space is mutually exclusive and needs no cross-shard synchronization.
        private readonly ConcurrentDictionary<T, HayateObject<T>> _objects = new();

        /// <summary>The number of live objects registered in this shard (idle + borrowed).</summary>
        internal int TrackedCount => _objects.Count;

        /// <summary>All wrapper objects registered in this shard (including those currently borrowed).</summary>
        internal IEnumerable<HayateObject<T>> TrackedValues => _objects.Values;

        internal bool TryTrack(T key, HayateObject<T> w) => _objects.TryAdd(key, w);

        internal bool TryGetTracked(T key, out HayateObject<T> w) => _objects.TryGetValue(key, out w);

        internal bool Untrack(T key) => _objects.TryRemove(key, out _);

        internal void ClearTracked() => _objects.Clear();

        // Protects _list and the object-location state transitions. The critical section only performs a few pointer operations and is extremely short.
        // Hard constraint: user code is never called back inside the critical section (Dispose / policy always run outside the lock) to avoid re-entrant deadlocks.
        private SpinLock _lock = new(enableThreadOwnerTracking: false);

        private int _maxSize;

        // Why the shard does not maintain a BorrowedCount:
        // TryTake first physically unlinks the object from this shard's list, then sets Borrowed, so the borrowed object is never in the list;
        // therefore the "in-list IsBorrowed count" is structurally always 0. And on return, if Release rejects it (OnRelease=false) the object is destroyed directly without going through this shard's Add, so increments and decrements cannot be paired one-to-one.
        // The pool-level borrowed count is derived by TakeSnapshot as "sum of each shard's registry TrackedCount" minus "sum of each shard's Count".

        public int Index { get; }

        public int Count
        {
            get
            {
                var taken = false;
                try
                {
                    _lock.Enter(ref taken);
                    return _list.Count;
                }
                finally { if (taken) _lock.Exit(); }
            }
        }

        public int MaxSize => Volatile.Read(ref _maxSize);

        public Shard(HayatePoolOptions options, int index, int maxSize, IHayateLogger logger)
        {
            Index = index;
            _maxSize = maxSize;
            _logger = logger;
        }

        public void UpdateMaxSize(int newMaxSize)
        {
            if (newMaxSize < 0)
                throw new ArgumentOutOfRangeException(nameof(newMaxSize));
            Interlocked.Exchange(ref _maxSize, newMaxSize);
        }

        /// <summary>
        /// Appends an object to the shard free list.
        /// </summary>
        /// <returns>
        /// <c>true</c> when the object was accepted; <c>false</c> when it was rejected —
        /// either the shard is at capacity (the object is disposed by this method), or the
        /// object was already claimed by eviction / validation / destroy and must not be
        /// handed out again.
        /// </returns>
        public bool Add(HayateObject<T> w)
        {
            if (w is null) throw new ArgumentNullException(nameof(w));

            var currentMax = Volatile.Read(ref _maxSize);
            HayateObject<T> overflow = null;
            var accepted = false;
            var size = 0;

            var taken = false;
            try
            {
                _lock.Enter(ref taken);

                // Only accept the two states "just created" or "returned after borrow".
                // Objects already claimed by eviction / validation / destroy (Removing / Destroyed) are rejected outright,
                // otherwise a disposed object could be placed back into the pool -- the direct root cause of a prior regression.
                var location = w.Location;
                if (location != HayateObjectLocation.None && location != HayateObjectLocation.Borrowed)
                    return false;

                // Fallback: in theory Node is always null when an object is returned; if an abnormal path leaves a stale node, unlink it first then append to the tail,
                // guaranteeing the same object appears at most once in the list.
                var stale = w.Node;
                if (stale != null && ReferenceEquals(stale.List, _list))
                    _list.Remove(stale);

                size = _list.Count;
                if (size >= currentMax)
                {
                    overflow = w;
                    return false;
                }

                w.Node = _list.AddLast(w);
                w.Location = HayateObjectLocation.InPool;
                size++;
                accepted = true;
            }
            finally
            {
                if (taken) _lock.Exit();
            }

            // Fix: on overflow this method does not dispose the object itself (a bare Dispose would skip _policy.OnDestroy,
            // and setting Destroyed=1 early would make the caller's full Destroy(w) return immediately via the idempotent CAS, so OnDestroy would never fire).
            // So here we only log and return false, handing the full destruction to the sole real caller (Release -> Destroy(w),
            // which triggers _policy.OnDestroy -> Dispose -> registry Untrack, with idempotent protection).
            // PreWarm / ForceScaleUp both clamp by capacity / call UpdateShardMaxSizes first, so a real overflow never happens here.
            if (overflow != null)
            {
                _logger.LogWarning("[Shard {Index}] capacity exceeded, object rejected and will be destroyed by caller (shard size: {Size}, max: {Max})",
                    Index, size, currentMax);
            }
            else if (accepted)
            {
                _logger.LogDebug("[Shard {Index}] Object added to shard (current size: {Size})", Index, size);
            }

            return accepted;
        }

        public bool TryTake(out HayateObject<T> w)
        {
            w = null;

            var taken = false;
            try
            {
                _lock.Enter(ref taken);

                var node = _list.First;
                if (node == null) return false;

                w = node.Value;
                _list.Remove(node);
                w.Node = null;

                // Critical ordering: unlink first, then set Borrowed.
                // After this, Remove can no longer claim the object (its Node no longer belongs to any list),
                // so the eviction thread can never destroy an object already handed to the caller.
                w.Location = HayateObjectLocation.Borrowed;

                _logger.LogDebug("[Shard {Index}] Object taken from shard (current size: {Size})", Index, _list.Count);
                return true;
            }
            finally { if (taken) _lock.Exit(); }
        }

        /// <summary>
        /// Claims an idle object for destruction and physically unlinks it from the shard.
        /// </summary>
        /// <returns>
        /// <c>true</c> only for the single caller that won the claim — that caller owns the
        /// object and is responsible for destroying it. <c>false</c> means the object is
        /// currently borrowed, lives in another shard, or has already been claimed, and the
        /// caller MUST NOT destroy it.
        /// </returns>
        public bool Remove(HayateObject<T> w)
        {
            if (w == null) return false;

            var claimed = false;
            var size = 0;

            var taken = false;
            try
            {
                _lock.Enter(ref taken);

                // Double-check: the state is InPool and the node is actually attached to this shard's list.
                // If either is missing, the object is not in a "safe to destroy" position right now (borrowed / already claimed / in another shard).
                if (w.Location != HayateObjectLocation.InPool) return false;

                var node = w.Node;
                if (node == null || !ReferenceEquals(node.List, _list)) return false;

                _list.Remove(node);
                w.Node = null;
                w.Location = HayateObjectLocation.Removing;
                size = _list.Count;
                claimed = true;
            }
            finally
            {
                if (taken) _lock.Exit();
            }

            if (claimed)
            {
                _logger.LogDebug("[Shard {Index}] Object removed from shard (current size: {Size})", Index, size);
            }

            return claimed;
        }

        public IEnumerable<HayateObject<T>> GetAll()
        {
            var taken = false;
            try
            {
                _lock.Enter(ref taken);
                return _list.ToArray();
            }
            finally { if (taken) _lock.Exit(); }
        }
        /// <summary>
        /// Copies up to <paramref name="count"/> idle objects from the head of the shard free
        /// list into <paramref name="buffer"/> while holding the shard lock, returning the number
        /// copied. The caller may reuse the same buffer across shards / invocations to avoid
        /// per-call array allocations. The buffer is overwritten from index 0 each call.
        /// </summary>
        public int SnapshotHead(HayateObject<T>[] buffer, int count)
        {
            if (buffer is null) throw new ArgumentNullException(nameof(buffer));
            if (count < 0) throw new ArgumentOutOfRangeException(nameof(count));

            var taken = false;
            try
            {
                _lock.Enter(ref taken);
                var limit = Math.Min(count, buffer.Length);
                var copied = 0;
                var node = _list.First;
                while (node != null && copied < limit)
                {
                    buffer[copied++] = node.Value;
                    node = node.Next;
                }
                return copied;
            }
            finally { if (taken) _lock.Exit(); }
        }

        public void Clear()
        {
            HayateObject<T>[] drained;

            var taken = false;
            try
            {
                _lock.Enter(ref taken);
                drained = _list.ToArray();
                _list.Clear();
                foreach (var w in drained)
                {
                    w.Node = null;
                    Interlocked.Exchange(ref w.Destroyed, 1);
                    w.Location = HayateObjectLocation.Destroyed;
                }
            }
            finally { if (taken) _lock.Exit(); }

            var clearedCount = 0;
            foreach (var w in drained)
            {
                if (SafeDispose(w, "shard clear")) clearedCount++;
            }

            _logger.LogInformation("[Shard {Index}] Shard cleared, {Count} objects destroyed", Index, clearedCount);
        }

        /// <summary>
        /// Safely releases the object outside the lock, swallowing any exception thrown by the user's
        /// Dispose and reporting whether the release succeeded.
        /// </summary>
        private bool SafeDispose(HayateObject<T> w, string reason)
        {
            try
            {
                if (w.Value is IDisposable d) d.Dispose();
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to dispose object when [{Reason}]", reason);
                return false;
            }
        }
    }
}
