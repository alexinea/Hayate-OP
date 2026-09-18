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

        // Diagnostics master switch (O11), snapshotted at construction like every other feature switch.
        // With diagnostics off the shard writes no debug entry at all, so a borrow or a return no longer
        // builds the params array (and boxes the counters) those traces would pass to the logger.
        private readonly bool _enableDiagnostics;

        // Free-object linked list: Add appends to the tail and TryTake removes from the head, naturally preserving FIFO order.
        // Remove performs an O(1) unlink via HayateObject<T>.Node.
        // The old implementation rebuilt the queue with ConcurrentQueue + "ToList -> Remove -> Clear -> Enqueue";
        // during the rebuild window concurrent TryTake / Add could lose objects or produce duplicates, so it was replaced entirely with a linked list.
        private readonly LinkedList<HayateObject<T>> _list = new();

        // Borrowed-object linked list (K2, abandoned recovery). Maintained only when abandoned recovery
        // is enabled (_trackBorrowed): TryTake appends on borrow (FIFO — the head is the oldest borrow),
        // Add unlinks on return, ClaimBorrowed unlinks on reclamation, and Destroy unlinks via
        // UnmarkBorrowed on every destroy path. When the feature is off the list stays empty and the
        // borrow/return hot paths pay a single predicted branch, exactly like the other feature switches.
        private readonly LinkedList<HayateObject<T>> _borrowed = new();
        private readonly bool _trackBorrowed;

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

        internal bool TryGetTracked(T key, out HayateObject<T> w)
        {
            var found = _objects.TryGetValue(key, out var value);
            w = value!;
            return found;
        }

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

        // Spare-wrapper stack: an intrusive singly-linked list threaded through HayateObject<T>.SpareNext.
        // Destroyed wrappers are parked here (bounded by the shard's current max size) and reused by the
        // next object creation, removing the per-wrapper allocation from create/destroy churn. The stack
        // is consumed only by the pool's create path, which fully resets a taken wrapper before its new
        // value becomes observable, so a parked wrapper never leaks stale state.
        // The stack is guarded by its own SpinLock (two pointer operations per side, no user code inside):
        // a lock-free Treiber stack would be exposed to the classic ABA reuse hazard under concurrent
        // destroy/create churn, and this lock is never held on the borrow/return hot path.
        private SpinLock _spareLock = new(enableThreadOwnerTracking: false);
        private HayateObject<T>? _spareHead;
        private int _spareCount;

        /// <summary>The number of wrappers currently parked on the spare stack (diagnostic use).</summary>
        internal int SpareCount => Volatile.Read(ref _spareCount);

        /// <summary>
        /// Parks a fully destroyed wrapper on the spare stack for future reuse. The wrapper must have
        /// gone through the pool's destroy path (value disposed and untracked, value reference cleared).
        /// The stack is bounded by the shard's current max size: when full, the wrapper is simply dropped
        /// for the garbage collector, so the stack can never exceed the shard capacity.
        /// </summary>
        public void ReturnSpare(HayateObject<T> w)
        {
            var taken = false;
            try
            {
                _spareLock.Enter(ref taken);
                if (_spareCount >= Volatile.Read(ref _maxSize)) return;
                w.SpareNext = _spareHead;
                _spareHead = w;
                _spareCount++;
            }
            finally { if (taken) _spareLock.Exit(); }
        }

        /// <summary>
        /// Takes a wrapper from the spare stack, or returns <c>null</c> when the stack is empty.
        /// The caller (the pool's create path only) must fully reset the wrapper before use.
        /// </summary>
        public HayateObject<T>? TryTakeSpare()
        {
            var taken = false;
            try
            {
                _spareLock.Enter(ref taken);
                var head = _spareHead;
                if (head is null) return null;
                _spareHead = head.SpareNext;
                _spareCount--;
                head.SpareNext = null;
                return head;
            }
            finally { if (taken) _spareLock.Exit(); }
        }

        public Shard(HayatePoolOptions options, int index, int maxSize, IHayateLogger logger)
        {
            Index = index;
            _maxSize = maxSize;
            _logger = logger;
            // The borrowed list exists only to serve abandoned recovery; with both toggles off the
            // shard never maintains it (options are normalized by IsValid before the shards are built).
            _trackBorrowed = options.RemoveAbandonedOnBorrow || options.RemoveAbandonedOnMaintenance;
            _enableDiagnostics = options.EnableDiagnostics;
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
            HayateObject<T>? overflow = null;
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
                // The object is no longer borrowed: unlink it from the borrowed list (K2). On the
                // overflow/reject path it stays linked until the caller's Destroy unmarks it.
                if (_trackBorrowed) UnlinkBorrowedLocked(w);
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
            else if (accepted && _enableDiagnostics)
            {
                _logger.LogDebug("[Shard {Index}] Object added to shard (current size: {Size})", Index, size);
            }

            return accepted;
        }

        public bool TryTake(out HayateObject<T> w)
        {
            w = null!;

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

                // Appended at the tail, so the borrowed list stays FIFO: the head is always the oldest
                // borrow — the first candidate the abandoned scan (K2) examines.
                if (_trackBorrowed) w.BorrowedNode = _borrowed.AddLast(w);

                // Suppressed by the diagnostics master switch. Gating it also keeps the trace's params
                // array — and the interpolation work behind it — out of the owned spin lock.
                if (_enableDiagnostics)
                {
                    _logger.LogDebug("[Shard {Index}] Object taken from shard (current size: {Size})", Index, _list.Count);
                }

                return true;
            }
            finally { if (taken) _lock.Exit(); }
        }

        /// <summary>
        /// Registers a freshly created object that is handed out already borrowed (the create-on-miss /
        /// cold-boot path, which does not go through <see cref="TryTake"/>). Takes the shard lock itself
        /// because the wrapper is brand-new and exclusively owned — the lock is uncontended and only
        /// protects the borrowed list against concurrent TryTake appends.
        /// </summary>
        public void MarkBorrowed(HayateObject<T> w)
        {
            if (!_trackBorrowed) return;

            var taken = false;
            try
            {
                _lock.Enter(ref taken);
                w.BorrowedNode = _borrowed.AddLast(w);
            }
            finally { if (taken) _lock.Exit(); }
        }

        /// <summary>
        /// Unlinks the object from the borrowed list. Called by the pool's destroy path (K2): a wrapper
        /// being destroyed must never stay listed as borrowed, whatever the destroy reason (reclamation,
        /// return rejection, validation failure, overflow, Clear). Takes the shard lock itself because
        /// the destroy path runs outside it.
        /// </summary>
        public void UnmarkBorrowed(HayateObject<T> w)
        {
            if (!_trackBorrowed || w is null) return;

            var taken = false;
            try
            {
                _lock.Enter(ref taken);
                UnlinkBorrowedLocked(w);
            }
            finally { if (taken) _lock.Exit(); }
        }

        /// <summary>
        /// Unlinks the borrowed-list node (the caller must hold the shard lock). A node whose list no
        /// longer matches is a stale reference from an earlier Clear — the list has been drained and the
        /// node dropped, so nothing to remove.
        /// </summary>
        private void UnlinkBorrowedLocked(HayateObject<T> w)
        {
            var node = w.BorrowedNode;
            if (node != null && ReferenceEquals(node.List, _borrowed)) _borrowed.Remove(node);
            w.BorrowedNode = null;
        }

        /// <summary>
        /// Claims a <em>borrowed</em> object for destruction (abandoned recovery, K2 — CHOPIN's
        /// <c>RemoveAbandonedOnBorrow/OnMaintenance</c> claim). Only the single caller that wins the
        /// <c>Borrowed → Removing</c> transition returns <c>true</c> and owns the destruction; concurrent
        /// reclamation and the borrower's own <see cref="Add"/> both lose and must not destroy.
        /// </summary>
        /// <returns>
        /// <c>true</c> for the winning caller only. <c>false</c> means the object was returned to the
        /// pool in the meantime, or another thread already claimed it.
        /// </returns>
        public bool ClaimBorrowed(HayateObject<T> w)
        {
            if (w == null) return false;

            var claimed = false;

            var taken = false;
            try
            {
                _lock.Enter(ref taken);

                if (w.Location != HayateObjectLocation.Borrowed) return false;

                UnlinkBorrowedLocked(w);
                w.Location = HayateObjectLocation.Removing;
                claimed = true;
            }
            finally
            {
                if (taken) _lock.Exit();
            }

            if (claimed && _enableDiagnostics)
            {
                _logger.LogDebug("[Shard {Index}] Borrowed object claimed for abandoned recovery (current borrowed: {Count})", Index, _borrowed.Count);
            }

            return claimed;
        }

        /// <summary>
        /// Copies up to <paramref name="max"/> borrowed wrappers from the head of the borrowed list
        /// (oldest borrow first) into <paramref name="buffer"/> while holding the shard lock, returning
        /// the number copied. The abandoned scan (K2) walks this snapshot from the head and stops at the
        /// first entry not past the timeout — because the list is FIFO by borrow time, nothing after it
        /// can be abandoned either.
        /// </summary>
        public int SnapshotBorrowed(HayateObject<T>[] buffer, int max)
        {
            if (buffer is null) throw new ArgumentNullException(nameof(buffer));
            if (max < 0) throw new ArgumentOutOfRangeException(nameof(max));
            if (!_trackBorrowed) return 0;

            var taken = false;
            try
            {
                _lock.Enter(ref taken);
                var limit = Math.Min(max, buffer.Length);
                var copied = 0;
                var node = _borrowed.First;
                while (node != null && copied < limit)
                {
                    buffer[copied++] = node.Value;
                    node = node.Next;
                }
                return copied;
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

            if (claimed && _enableDiagnostics)
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
                // Drain the borrowed list too (K2): the pool clears the registry right after this
                // call, so a stale borrowed link would let a later abandoned scan claim a wrapper
                // whose registry entry is already gone. Borrowed values are not disposed here —
                // consistent with the registry-clear semantics documented at the Clear call site.
                if (_trackBorrowed)
                {
                    var node = _borrowed.First;
                    while (node != null)
                    {
                        var w = node.Value;
                        var next = node.Next;
                        _borrowed.Remove(node);
                        w.BorrowedNode = null;
                        node = next;
                    }
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
