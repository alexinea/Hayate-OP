using DotNetCore.HayateOP.Common;
using DotNetCore.HayateOP.Logging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace DotNetCore.HayateOP;

public partial class HayatePoolBasic<T>
{
    // The shard is the pool's stripe: one instance per shard, each with its own SpinLock, free list and
    // counters, and the pool allocates them back to back in a single loop (see HayatePoolBasic<T>._shards).
    // The cache-line padding below is what keeps two threads working on neighbouring shards out of each
    // other's cache line - the leading half in the base class, the trailing half in the field at the
    // bottom of this class. Layout only: no member, default value or behaviour changes.
    internal class Shard : CacheLinePadded
    {
        private readonly IHayateLogger _logger;

        // Diagnostics master switch (O11), snapshotted at construction like every other feature switch.
        // With diagnostics off the shard writes no debug entry at all, so a borrow or a return no longer
        // builds the params array (and boxes the counters) those traces would pass to the logger.
        private readonly bool _enableDiagnostics;

        /// <summary>
        /// The per-operation debug channel: the diagnostics master switch AND the logger's own filter.
        /// <see cref="IHayateLogger.IsEnabled"/> is a permission check, so asking it before the call is
        /// what keeps a logger that would discard the entry from being handed the params array and the
        /// boxed counters in the first place. Both halves are readonly field reads.
        /// </summary>
        private bool DebugTraceEnabled => _enableDiagnostics && _logger.IsEnabled(HayateLogLevel.Debug);

        // Free-object linked list, kept in return order: Add appends to the tail, and TryTake removes
        // the node the configured borrow strategy selects — the head for FIFO (the default, and what
        // every release before the switch did), the tail for LIFO, or, for LRU and Priority, whichever
        // node a walk of the list picks. The list is never reordered under any strategy, so the head is
        // the oldest-returned object in all four modes; the eviction sampler reads that end, which is
        // why switching the borrow order does not change what a policy sees.
        // Remove performs an O(1) unlink via HayateObject<T>.Node.
        // The old implementation rebuilt the queue with ConcurrentQueue + "ToList -> Remove -> Clear -> Enqueue";
        // during the rebuild window concurrent TryTake / Add could lose objects or produce duplicates, so it was replaced entirely with a linked list.
        private readonly LinkedList<HayateObject<T>> _list = new();

        // Borrow order, snapshotted at construction from HayatePoolOptions.BorrowStrategy. It only
        // decides which node of _list a borrow is served, so the borrow path pays one predicted
        // readonly read (and the dispatch folds away entirely for the FIFO default).
        private readonly bool _takeNewest;

        // G-21: the two strategies that pick a node by walking the list rather than by reading an end —
        // Lru (smallest LastBorrowedAt) and Priority (highest per the comparer). A separate flag from
        // _takeNewest so the Fifo/Lifo dispatch above stays exactly the two-way branch it always was.
        private readonly bool _selectFromIdleList;

        // The comparer behind HayateBorrowStrategy.Priority, supplied by the builder (HayatePoolOptions
        // cannot name IComparer<T>). Non-null exactly when _selectFromIdleList is on and the strategy is
        // Priority — a pool that asked for Priority without one is refused at build time, so this never
        // has to be re-checked on the borrow path.
        private readonly IComparer<T>? _borrowPriority;

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

        // ---------------------------------------------------------------------------------------------
        // G-9: the optional lock-free slot store.
        //
        // With EnableLockFreeStorage off (the default) every field below keeps its default value,
        // _useLockFreeStore is a readonly false, and the borrow / return / claim paths run exactly the
        // statements they always ran behind one predicted branch.
        //
        // With it on, _list and _lock stop being the storage: an idle wrapper sits in a slot it owns,
        // and a borrow or a claim exchanges it out of that slot with a single CompareExchange. The
        // fields below are the whole of that store. The shard's cache-line padding covers them like
        // every other field: the leading half is in CacheLinePadded, the trailing half in _trailingPad.
        // ---------------------------------------------------------------------------------------------

        /// <summary>Whether this shard holds idle objects in the lock-free slot store (G-9), snapshotted at construction.</summary>
        private readonly bool _useLockFreeStore;

        /// <summary>The slot array: one entry per slot, holding the parked wrapper that owns it.</summary>
        private readonly HayateObject<T>?[] _slots;

        /// <summary>
        /// Intrusive free-slot stack: <c>_freeNext[i]</c> is the next free index after slot <c>i</c>,
        /// threaded through this array so a push and a pop cost no allocation. Only the <b>head</b>
        /// needs a version. Indices are genuinely recycled here — a popped index can legitimately
        /// return to the head between a reader's load and its compare-exchange — so the version is
        /// what tells the two apart. Slot ownership needs none: a slot's writers are only "the
        /// wrapper that owns it" and "the thread that just exchanged it out", never a third party
        /// arriving through a followed link, so there is no ABA to defend against there.
        /// </summary>
        private readonly int[] _freeNext;

        /// <summary>The packed (version, index) head of the free-slot stack; an index of -1 means empty.</summary>
        private long _freeHead;

        /// <summary>
        /// The round-robin scan cursor. A <i>shared</i> starting point, not an affinity: its job is to
        /// spread concurrent scanners across the shard's slots, not to claim that a thread owns one.
        /// The pool already routes a borrow through a ring scan from an affinity start, and a second
        /// private affinity here would be a second mechanism for the same job.
        /// </summary>
        private int _scanCursor;

        /// <summary>
        /// The number of idle wrappers the store holds. A counter rather than a walk, because a slot
        /// array has no cheap way to be counted, and it is the input to the capacity decision on the
        /// return path. Reads are volatile: <see cref="Count"/> and the pool's idle derivation both
        /// go through it.
        /// </summary>
        private int _idleCount;

        /// <summary>
        /// How many takes the lock-free store served without entering the shard lock. It exists so
        /// "the lock is gone" is asserted through a counter rather than through a wall-clock reading,
        /// which is the only way that claim can be pinned in a test.
        /// </summary>
        private int _lockFreeTakes;

        /// <summary>The number of takes served by the lock-free store without entering the shard lock (diagnostic use).</summary>
        internal int LockFreeTakes => Volatile.Read(ref _lockFreeTakes);

        // A2 (docs/async-policy.md §5): the destroy sites prefer IAsyncDisposable for a pool whose
        // policy implements the asynchronous contract. Constant false for every other pool, so the
        // SafeDispose branch that reads it is dead code the JIT removes.
        private readonly bool _prefersAsyncDisposal;

        // Why the shard does not maintain a BorrowedCount:
        // TryTake first physically unlinks the object from this shard's list, then sets Borrowed, so the borrowed object is never in the list;
        // therefore the "in-list IsBorrowed count" is structurally always 0. And on return, if Release rejects it (OnRelease=false) the object is destroyed directly without going through this shard's Add, so increments and decrements cannot be paired one-to-one.
        // The pool-level borrowed count is derived by TakeSnapshot as "sum of each shard's registry TrackedCount" minus "sum of each shard's Count".

        public int Index { get; }

        public int Count
        {
            get
            {
                // G-9: the store keeps its own idle count, so the lock-free backend answers this with a
                // volatile read instead of a walk (and without taking the lock the backend exists to
                // avoid). The clamped read is defensive: the count is only ever moved atomically, but a
                // negative value would break the pool's idle derivation if it ever appeared.
                if (_useLockFreeStore)
                {
                    var idle = Volatile.Read(ref _idleCount);
                    return idle > 0 ? idle : 0;
                }

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

        public Shard(HayatePoolOptions options, int index, int maxSize, IHayateLogger logger, bool prefersAsyncDisposal = false, IComparer<T>? borrowPriority = null)
        {
            Index = index;
            _maxSize = maxSize;
            _logger = logger;
            // A2 (docs/async-policy.md §5): snapshotted from the pool, which derives it from the
            // policy's asynchronous contract. False everywhere except net6.0+ with an asynchronous
            // policy, so the SafeDispose branch below folds away for every other pool.
            _prefersAsyncDisposal = prefersAsyncDisposal;
            // The borrowed list exists only to serve abandoned recovery; with both toggles off the
            // shard never maintains it (options are normalized by IsValid before the shards are built).
            _trackBorrowed = options.RemoveAbandonedOnBorrow || options.RemoveAbandonedOnMaintenance;
            _enableDiagnostics = options.EnableDiagnostics;
            // Options are normalized before the shards are built, so an unrecognised value has already
            // fallen back to Fifo. G-21 splits the remaining three into the two cheap end reads
            // (_takeNewest) and the two that walk the list (_selectFromIdleList); a pool that asked for
            // either of the latter two without a comparer never gets this far, because Build refuses it.
            _takeNewest = options.BorrowStrategy == HayateBorrowStrategy.Lifo;
            _selectFromIdleList = options.BorrowStrategy == HayateBorrowStrategy.Lru ||
                                  options.BorrowStrategy == HayateBorrowStrategy.Priority;
            _borrowPriority = borrowPriority;

            // G-9: the lock-free store. The slot array is sized here — one slot per idle object the
            // shard may hold — and is never replaced, which is what keeps a slot index valid for the
            // wrapper's whole life and removes every array-swap race from the store. A pool with the
            // backend on therefore refuses a ReloadConfig that would raise MaxPoolSize past this size,
            // the same rejection lean makes for its own construction-sized retention buffer.
            _useLockFreeStore = options.EnableLockFreeStorage;
            if (_useLockFreeStore && maxSize > 0)
            {
                _slots = new HayateObject<T>?[maxSize];
                _freeNext = new int[maxSize];
                for (var i = 0; i < maxSize - 1; i++) _freeNext[i] = i + 1;
                _freeNext[maxSize - 1] = -1;
                _freeHead = PackHead(0, 0);
            }
            else
            {
                _slots = Array.Empty<HayateObject<T>>();
                _freeNext = Array.Empty<int>();
                _freeHead = PackHead(0, -1);
            }
        }

        /// <summary>
        /// Packs a version and a slot index into the free-stack head. An index of -1 marks the empty
        /// stack; the version occupies the high half and is what makes a recycled index
        /// distinguishable from the one a reader loaded.
        /// </summary>
        private static long PackHead(int version, int index) => ((long)version << 32) | (uint)index;

        /// <summary>
        /// Pops a free slot index off the intrusive stack, or returns <c>false</c> when every slot is
        /// owned by a wrapper.
        /// </summary>
        /// <remarks>
        /// The classic Treiber stack, with the head carrying a version: the link is read before the
        /// exchange, and the version is what makes a stale read of that link harmless when the same
        /// index is recycled in between. Without the version, a reader whose compare-exchange lands
        /// after the index left and returned to the head would take the wrong successor.
        /// </remarks>
        private bool TryPopSlot(out int index)
        {
            while (true)
            {
                var head = Volatile.Read(ref _freeHead);
                var candidate = (int)head;
                if (candidate < 0)
                {
                    index = -1;
                    return false;
                }

                var next = _freeNext[candidate];
                if (Interlocked.CompareExchange(ref _freeHead, PackHead((int)(head >> 32) + 1, next), head) == head)
                {
                    index = candidate;
                    return true;
                }
            }
        }

        /// <summary>
        /// Pushes a slot index back onto the free stack.
        /// </summary>
        /// <remarks>
        /// The caller owns the index — it was popped for a wrapper that is now being destroyed, or it
        /// belonged to a wrapper the shard has just exchanged out — so nothing else can be writing this
        /// link while the push is in flight, and a retry after a lost compare-exchange simply rewrites
        /// it for the new head.
        /// </remarks>
        private void PushSlot(int index)
        {
            while (true)
            {
                var head = Volatile.Read(ref _freeHead);
                _freeNext[index] = (int)head;
                if (Interlocked.CompareExchange(ref _freeHead, PackHead((int)(head >> 32) + 1, index), head) == head) return;
            }
        }

        /// <summary>
        /// Hands a wrapper's slot index back to the shard, or does nothing when the wrapper owns no
        /// slot. Called from the pool's destroy path (the single place a wrapper is torn down) and from
        /// the store's own claim path.
        /// </summary>
        /// <remarks>
        /// The index is cleared with a compare-exchange rather than a plain store, so the release is
        /// idempotent: destruction is already guarded by <see cref="HayateObject{T}.Destroyed"/>, and
        /// this closes the same door for the slot — an index returns to the free stack exactly once,
        /// however many destroy attempts race for the wrapper.
        /// </remarks>
        internal void ReleaseSlot(HayateObject<T> w)
        {
            if (!_useLockFreeStore || w is null) return;

            var index = w.SlotIndex;
            if (index < 0) return;
            if (Interlocked.CompareExchange(ref w.SlotIndex, -1, index) != index) return;

            PushSlot(index);
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

            // G-9: with the lock-free store on, the shard's storage is the slot array and the free list
            // below is never touched.
            if (_useLockFreeStore) return AddLockFree(w);

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
                {
                    // G-14-A: an object that is already idle in this shard's list can only be here
                    // through a second return of the same object. Its node is still linked, so the
                    // caller's destroy would leave a destroyed wrapper reachable from the list — the
                    // next take popped that node and handed out its null value. Unlink it here, so
                    // the destroy the refusal triggers leaves nothing behind.
                    if (location == HayateObjectLocation.InPool)
                    {
                        var parked = w.Node;
                        if (parked != null && ReferenceEquals(parked.List, _list))
                        {
                            _list.Remove(parked);
                            w.Node = null;
                        }
                    }

                    return false;
                }

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
            else if (accepted && DebugTraceEnabled)
            {
                _logger.LogDebug("[Shard {Index}] Object added to shard (current size: {Size})", Index, size);
            }

            return accepted;
        }

        /// <summary>
        /// The lock-free store's half of <see cref="Add"/>: reserves one idle slot against the shard's
        /// capacity and writes the wrapper into the slot it owns.
        /// </summary>
        /// <returns>
        /// The same two answers <see cref="Add"/> gives — <c>true</c> when the object was parked;
        /// <c>false</c> when it must not be handed out again (it was already claimed by eviction /
        /// validation / destroy) or when the shard is at capacity. As on the locked path the object is
        /// not disposed here: the single real caller (<c>Release</c> -&gt; <c>Destroy</c>) runs the
        /// full destruction, so <c>OnDestroy</c> still fires exactly once.
        /// </returns>
        /// <remarks>
        /// The reservation is a compare-exchange loop rather than a bare increment, so concurrent
        /// returns cannot overshoot the shard's capacity — the same shape the lean buffer uses for its
        /// own ceiling. The store into the slot is a plain write, not an exchange: the index was handed
        /// to this wrapper alone (popped fresh for a create, or kept from the wrapper's last park), so
        /// there is no competing writer to lose to.
        /// </remarks>
        private bool AddLockFree(HayateObject<T> w)
        {
            // Only accept the two states "just created" or "returned after borrow", exactly as the
            // locked path does: an object already claimed by eviction / validation / destroy
            // (Removing / Destroyed) must never be placed back into the pool.
            var location = w.Location;
            if (location != HayateObjectLocation.None && location != HayateObjectLocation.Borrowed)
            {
                // G-14-A: the lock-free mirror of the unlink in the locked path. A wrapper already
                // parked in a slot of this store can only be here through a second return of the
                // same object; its slot still holds it, so the caller's destroy would leave a
                // destroyed wrapper reachable from the store — the next take would exchange it out
                // and hand out its null value. Claim the slot out under the same identity check a
                // take performs: a lost race leaves the store untouched.
                if (location == HayateObjectLocation.InPool)
                {
                    var parkedSlots = _slots;
                    var parkedIndex = w.SlotIndex;
                    if ((uint)parkedIndex < (uint)parkedSlots.Length &&
                        ReferenceEquals(w, Interlocked.CompareExchange(ref parkedSlots[parkedIndex], null, w)))
                    {
                        Interlocked.Decrement(ref _idleCount);
                    }
                }

                return false;
            }

            var slots = _slots;
            var currentMax = Volatile.Read(ref _maxSize);
            if (currentMax > slots.Length) currentMax = slots.Length;

            var size = 0;
            while (true)
            {
                var idle = Volatile.Read(ref _idleCount);
                if (idle >= currentMax)
                {
                    size = idle;
                    _logger.LogWarning("[Shard {Index}] capacity exceeded, object rejected and will be destroyed by caller (shard size: {Size}, max: {Max})",
                        Index, size, currentMax);
                    return false;
                }

                if (Interlocked.CompareExchange(ref _idleCount, idle + 1, idle) == idle)
                {
                    size = idle + 1;
                    break;
                }
            }

            var index = w.SlotIndex;
            if (index < 0 || index >= slots.Length)
            {
                if (!TryPopSlot(out index))
                {
                    // Unreachable while every reserved slot belongs to a live wrapper — the reservation
                    // above already proved there is room — but a lost index must never turn into a
                    // silent capacity leak, so the reservation is rolled back and the object is refused.
                    Interlocked.Decrement(ref _idleCount);
                    _logger.LogWarning("[Shard {Index}] lock-free store has no free slot although capacity is not reached (shard size: {Size}, max: {Max}); object rejected and will be destroyed by caller",
                        Index, size, currentMax);
                    return false;
                }

                w.SlotIndex = index;
            }

            slots[index] = w;
            w.Location = HayateObjectLocation.InPool;

            // The object is no longer borrowed: unlink it from the borrowed list (K2). That list is
            // lock-guarded on every backend, and the toggles that maintain it are off by default, so a
            // pool that does not use abandoned recovery never takes the shard lock here.
            if (_trackBorrowed) UnmarkBorrowed(w);

            if (DebugTraceEnabled)
            {
                _logger.LogDebug("[Shard {Index}] Object added to shard (current size: {Size})", Index, size);
            }

            return true;
        }

        /// <summary>
        /// The G-21 half of <see cref="TryTake"/>: walks the idle list and returns the node the LRU or
        /// Priority order selects. Called only while holding the shard lock, and only when one of those
        /// two strategies is configured — the default Fifo/Lifo path never reaches it.
        /// </summary>
        /// <remarks>
        /// LRU takes the smallest <see cref="HayateObject{T}.LastBorrowedAt"/>. An object that has never
        /// been borrowed carries zero and is therefore the least recently used of all, which is what
        /// makes a freshly created instance eligible straight away rather than only after one round
        /// trip.<br />
        /// Priority takes the node the comparer ranks highest: a positive <c>Compare</c> result means
        /// "borrow this one first".<br />
        /// Both walk from the head and keep the first winner, so a tie — every object the comparer calls
        /// equal, and the all-zero case of LRU — resolves towards the oldest-returned object, the same
        /// end FIFO would have served. That keeps the result deterministic without requiring the
        /// comparer to be a total order, and it needs no extra bookkeeping.<br />
        /// The list is not reordered, which is what keeps <see cref="SnapshotHead"/> — and so the
        /// eviction scan — reading the same end it reads under Fifo.
        /// </remarks>
        private LinkedListNode<HayateObject<T>>? SelectFromIdleList()
        {
            var best = _list.First;
            if (best == null) return null;

            if (_borrowPriority is not null)
            {
                for (var node = best.Next; node != null; node = node.Next)
                {
                    if (_borrowPriority.Compare(node.Value.Value, best.Value.Value) > 0) best = node;
                }

                return best;
            }

            var bestStamp = best.Value.LastBorrowedAt;
            for (var node = best.Next; node != null; node = node.Next)
            {
                var stamp = node.Value.LastBorrowedAt;
                if (stamp < bestStamp)
                {
                    best = node;
                    bestStamp = stamp;
                }
            }

            return best;
        }

        public bool TryTake(out HayateObject<T> w)
        {
            // G-9: the lock-free store serves a take by exchanging a wrapper out of a slot.
            if (_useLockFreeStore) return TryTakeLockFree(out w);

            w = null!;

            var taken = false;
            try
            {
                _lock.Enter(ref taken);

                // FIFO (default) serves the head, the oldest returned object; LIFO serves the tail,
                // the object that came back last. Both ends are read through the same node walk, so
                // the strategy costs the borrow path one predicted branch and nothing else.
                // G-21: LRU and Priority cannot be answered by an end, so they walk the list once and
                // are dispatched here rather than duplicating the unlink/claim sequence below.
                var node = _selectFromIdleList
                    ? SelectFromIdleList()
                    : (_takeNewest ? _list.Last : _list.First);
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

                // Suppressed by the diagnostics master switch and by the logger's own filter. Gating it
                // also keeps the trace's params array — and the interpolation work behind it — out of the
                // owned spin lock.
                if (DebugTraceEnabled)
                {
                    _logger.LogDebug("[Shard {Index}] Object taken from shard (current size: {Size})", Index, _list.Count);
                }

                return true;
            }
            finally { if (taken) _lock.Exit(); }
        }

        /// <summary>
        /// The lock-free store's half of <see cref="TryTake"/>: scans the slots from a shared round-robin
        /// start and exchanges the first idle wrapper it finds out of its slot. Pure <c>Interlocked</c> —
        /// no lock, no allocation, no list walk.
        /// </summary>
        /// <remarks>
        /// The claim is the same pattern the lean fast path uses: read the slot, then compare-exchange it
        /// to <c>null</c>. The exchange returns the previous value, so a reference match proves this
        /// thread was the one that won the object. Nothing is followed through a link — the slot names
        /// the wrapper directly — so there is nothing that can have gone stale between the read and the
        /// exchange, which is why this form needs no version on the slot itself.
        /// </remarks>
        private bool TryTakeLockFree(out HayateObject<T> w)
        {
            // The idle count is a cheap pre-filter, not a reservation: it can only be stale by the width
            // of a concurrent take, and the exchange below is the real claim. Reading it first keeps a
            // take on an empty store from scanning the slots at all.
            if (Volatile.Read(ref _idleCount) <= 0)
            {
                w = null!;
                return false;
            }

            var slots = _slots;
            var limit = slots.Length;
            var start = limit == 0 ? 0 : (int)((uint)Interlocked.Increment(ref _scanCursor) - 1) % limit;

            for (var n = 0; n < limit; n++)
            {
                var i = start + n;
                if (i >= limit) i -= limit;

                var slot = slots[i];
                if (slot is not null &&
                    ReferenceEquals(slot, Interlocked.CompareExchange(ref slots[i], null, slot)))
                {
                    Interlocked.Decrement(ref _idleCount);
                    Interlocked.Increment(ref _lockFreeTakes);

                    w = slot;
                    // The wrapper keeps its slot index on purpose: the return goes back to the same slot,
                    // so no index has to be allocated on the way in.
                    w.Location = HayateObjectLocation.Borrowed;

                    // Appended at the tail, so the borrowed list stays FIFO: the head is always the
                    // oldest borrow — the first candidate the abandoned scan (K2) examines.
                    if (_trackBorrowed) MarkBorrowed(w);

                    if (DebugTraceEnabled)
                    {
                        _logger.LogDebug("[Shard {Index}] Object taken from shard (current size: {Size})", Index, Volatile.Read(ref _idleCount));
                    }

                    return true;
                }
            }

            w = null!;
            return false;
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

            if (claimed && DebugTraceEnabled)
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

            // G-9: the same exchange a take performs, which is what keeps this method's meaning intact.
            if (_useLockFreeStore) return RemoveLockFree(w);

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

            if (claimed && DebugTraceEnabled)
            {
                _logger.LogDebug("[Shard {Index}] Object removed from shard (current size: {Size})", Index, size);
            }

            return claimed;
        }

        /// <summary>
        /// The lock-free store's half of <see cref="Remove"/>: claims an idle object for destruction by
        /// exchanging it out of the slot its index names, and hands the slot index back to the shard.
        /// </summary>
        /// <returns>
        /// The same single-winner contract: <c>true</c> only for the caller that won the exchange, which
        /// then owns the destruction. <c>false</c> means the object is borrowed, has already been
        /// claimed, or no longer sits in the slot its index names — and the caller MUST NOT destroy it.
        /// </returns>
        /// <remarks>
        /// This is the reason the slot form was chosen over a linked one. A lock-free structure cannot
        /// physically unlink an arbitrary interior node without a tombstone and a new identity model for
        /// every cold path that walks the storage; exchanging the wrapper out of a slot it names itself
        /// keeps "claim and unlink" exactly what it always was, so the three <c>Remove</c> call sites and
        /// the six other cold paths that read the store need no changes at all.
        /// </remarks>
        private bool RemoveLockFree(HayateObject<T> w)
        {
            // Double-check, as the locked path does: the object is only in a "safe to destroy" position
            // while it is InPool. A borrowed or already-claimed object fails here.
            if (w.Location != HayateObjectLocation.InPool) return false;

            var slots = _slots;
            var index = w.SlotIndex;
            if (index < 0 || index >= slots.Length) return false;

            if (!ReferenceEquals(w, Interlocked.CompareExchange(ref slots[index], null, w))) return false;

            Interlocked.Decrement(ref _idleCount);
            w.Location = HayateObjectLocation.Removing;

            // Unlike a take, this one does hand the index back: the wrapper is on its way out and would
            // otherwise take the slot with it, shrinking the shard a little on every eviction.
            ReleaseSlot(w);

            if (DebugTraceEnabled)
            {
                _logger.LogDebug("[Shard {Index}] Object removed from shard (current size: {Size})", Index, Volatile.Read(ref _idleCount));
            }

            return true;
        }

        public IEnumerable<HayateObject<T>> GetAll()
        {
            // G-9: a sweep of the slots. The array is not a list, so this is a snapshot with exactly the
            // guarantee its callers need rather than an atomic copy: every entry it reports is only a
            // hint, and the caller's claim (Remove) exchanges the wrapper out of its slot, so a wrapper
            // taken from under the sweep makes that claim fail instead of destroying a borrowed object.
            if (_useLockFreeStore)
            {
                var slots = _slots;
                var found = new List<HayateObject<T>>(slots.Length);
                for (var i = 0; i < slots.Length; i++)
                {
                    var slot = Volatile.Read(ref slots[i]);
                    if (slot is not null) found.Add(slot);
                }

                return found;
            }

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
        /// <remarks>
        /// "Head" is the oldest-returned end of the list. That end does not follow
        /// <see cref="HayatePoolOptions.BorrowStrategy"/>: LIFO changes which object is served next,
        /// not the order the list is kept in, so a sampler that wants the coldest objects keeps
        /// getting them under either strategy.<br />
        /// On the lock-free store (G-9) there is no age order to name: a wrapper owns its slot, so the
        /// run this copies is a contiguous run of slots rather than the coldest objects. The sample is
        /// still a fair sample and the retirement criteria are unchanged — an object past its idle or
        /// lifetime limit is reclaimed whenever it is sampled — but "the oldest are sampled first" is
        /// gone, which is one of the two things that backend gives up.
        /// </remarks>
        public int SnapshotHead(HayateObject<T>[] buffer, int count)
        {
            if (buffer is null) throw new ArgumentNullException(nameof(buffer));
            if (count < 0) throw new ArgumentOutOfRangeException(nameof(count));

            if (_useLockFreeStore)
            {
                var slots = _slots;
                var slotLimit = Math.Min(count, buffer.Length);
                var copiedFromSlots = 0;
                for (var i = 0; i < slots.Length && copiedFromSlots < slotLimit; i++)
                {
                    var slot = Volatile.Read(ref slots[i]);
                    if (slot is not null) buffer[copiedFromSlots++] = slot;
                }

                return copiedFromSlots;
            }

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
            var drained = DrainForClear();

            var clearedCount = 0;
            foreach (var w in drained)
            {
                if (SafeDispose(w, "shard clear")) clearedCount++;
            }

            _logger.LogInformation("[Shard {Index}] Shard cleared, {Count} objects destroyed", Index, clearedCount);
        }

        /// <summary>
        /// The lock-protected half of <see cref="Clear"/>: unlinks every idle wrapper, marks it
        /// destroyed, and empties the borrowed links. Shared with the pool's asynchronous drain
        /// (<c>HayatePoolBasic&lt;T&gt;.ClearAsync</c>), which disposes the drained objects outside this
        /// lock through the awaited destroy path. No user code runs inside the critical section —
        /// the same hard constraint Clear has always kept.
        /// </summary>
        internal HayateObject<T>[] DrainForClear()
        {
            // G-9: the lock-free store drains by exchanging each slot out one at a time.
            if (_useLockFreeStore) return DrainForClearLockFree();

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

            return drained;
        }

        /// <summary>
        /// The lock-free store's half of <see cref="DrainForClear"/>: exchanges every occupied slot out
        /// and returns the wrappers it won, with each wrapper marked destroyed exactly as the locked
        /// drain does.
        /// </summary>
        /// <remarks>
        /// The per-slot exchange is what keeps this correct without a lock. A wrapper the drain wins
        /// belongs to the drain and is destroyed; a wrapper a concurrent take wins is borrowed and
        /// correctly left alone — and no wrapper can fall to both, because the exchange is the same
        /// single-winner claim <see cref="RemoveLockFree"/> makes. A wrapper lost this way is not a leak:
        /// it is out on loan, not idle, so the drain was never going to see it. The slot index goes back
        /// to the shard with each wrapper it retires, so a cleared shard can fill its slots again.
        /// </remarks>
        private HayateObject<T>[] DrainForClearLockFree()
        {
            var slots = _slots;
            var drained = new List<HayateObject<T>>(slots.Length);

            for (var i = 0; i < slots.Length; i++)
            {
                var slot = slots[i];
                if (slot is null) continue;
                if (!ReferenceEquals(slot, Interlocked.CompareExchange(ref slots[i], null, slot))) continue;

                Interlocked.Decrement(ref _idleCount);
                slot.Node = null;
                Interlocked.Exchange(ref slot.Destroyed, 1);
                slot.Location = HayateObjectLocation.Destroyed;
                ReleaseSlot(slot);
                drained.Add(slot);
            }

            // Drain the borrowed list too (K2): the pool clears the registry right after this call, so a
            // stale borrowed link would let a later abandoned scan claim a wrapper whose registry entry is
            // already gone. Borrowed values are not disposed here — consistent with the registry-clear
            // semantics documented at the Clear call site. That list is lock-guarded on every backend.
            if (_trackBorrowed)
            {
                var taken = false;
                try
                {
                    _lock.Enter(ref taken);
                    var node = _borrowed.First;
                    while (node != null)
                    {
                        var borrowed = node.Value;
                        var next = node.Next;
                        _borrowed.Remove(node);
                        borrowed.BorrowedNode = null;
                        node = next;
                    }
                }
                finally { if (taken) _lock.Exit(); }
            }

            return drained.ToArray();
        }

        /// <summary>
        /// Safely releases the object outside the lock, swallowing any exception thrown by the user's
        /// Dispose and reporting whether the release succeeded.
        /// </summary>
        private bool SafeDispose(HayateObject<T> w, string reason)
        {
            try
            {
#if NET6_0_OR_GREATER
                // A2 (docs/async-policy.md §5): a pool whose policy implements the asynchronous
                // contract tears its objects down through IAsyncDisposable even on this synchronous
                // drain — the synchronous caller waits — while every other pool keeps the exact
                // statement it always ran.
                if (_prefersAsyncDisposal && w.Value is IAsyncDisposable ad)
                {
                    ad.DisposeAsync().GetAwaiter().GetResult();
                    return true;
                }
#endif
                if (w.Value is IDisposable d) d.Dispose();
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to dispose object when [{Reason}]", reason);
                return false;
            }
        }

        // Trailing half of the cache-line padding (see CacheLinePadded). It is declared here, by the most
        // derived class, because that is the only place the runtime is willing to put it: the leading half
        // is a field of the non-generic base, and a generic type - which this nested class is - is refused
        // explicit layout. Declaring it is the whole point; nothing ever reads or writes it.
#pragma warning disable CS0169, CS0649 // The field is there to occupy space, never to be read or written.
        private readonly CacheLinePad _trailingPad;
#pragma warning restore CS0169, CS0649
    }
}
