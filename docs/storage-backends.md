# Storage backends: the lock-free shard store and the thread-local front slot

Status: **implemented 2026-10-13** (`G-9`, `G-10`), **extended 2026-10-15** (`G-23`, the
capacity-adaptive slot layout of §3.5). This page is the design source for `G-9` and `G-10` of the 3.1
performance line (`33-R-5` / `33-R-6`) and for `G-23` (`33-R-9`). Per the B8-1 precedent the form was
recorded before it was built, so the implementation is measured against a stated contract rather than
against what it happens to do. Section 8 records the two places where building it settled a question
this page had left open.

Both backends are **off by default**. With them off, neither the shard free list nor the borrow /
return path changes a single instruction.

## 1. What the two items are

| Item | What it replaces | Knob |
| :--- | :--- | :--- |
| `G-9` — lock-free shard store | The per-shard `SpinLock` + `LinkedList<HayateObject<T>>` free list | `EnableLockFreeStorage` |
| `G-10` — thread-local front slot | The first hop of the borrow scan, and the destination of a return | `EnableThreadLocalFrontSlot` |

They are **evaluated as a pair and switched independently**. Alone, each loses part of its value: a
front slot with no lock-free store still falls back to a spin lock on a miss, and a lock-free store
with no front slot still pays one compare-exchange on every hit. Independently switched, because a
pairwise *measurement* must not force a pairwise *configuration* — the two gains have to be
attributable one at a time.

The source form is TRTOP's `StripedObjectStore<T>` and
`TrackedInstanceThreadLocalFrontTier<T>`. What travels across is the **concurrency protocol**; the
**storage layout is redesigned for this repository's shard model** and is not copied. Section 3.3 is
where that redesign actually bites.

## 2. What the current shape is, and why the blueprint could not be copied literally

The free list is one `LinkedList<HayateObject<T>>` guarded by one `SpinLock` per shard
(`HayateObjectPool.Shard.cs:44`, `:98`). The list keeps **return order**; `BorrowStrategy` only
decides which end a borrow is served from, and the eviction sampler reads the oldest end under
either strategy (`Shard.cs:35-42`, `:1111`). Three facts have to be on the table:

**(a) A version-packed head cannot hold a pointer.** TRTOP packs `(version, index)` into one `long`
because its nodes live in a private `Node[]` array, so the head only has to hold a 32-bit **index**.
Here the nodes are intrusive — they are `LinkedListNode`s hanging off the wrappers — so a head that
pointed straight at a node would need all 64 bits for the pointer and have none left for a version.
.NET has no 128-bit CAS on any of this repository's target frameworks. So there are exactly two
ways forward: move the nodes into an index-addressed array so the head can be packed, or choose a
shape in which the ABA hazard does not arise at all.

**(b) Return order is a documented contract, and a lock-free structure cannot keep both ends.**
Honouring `Fifo` *and* `Lifo` *and* arbitrary removal means a doubly linked list, and a lock-free
doubly linked list with arbitrary unlink is not a thing anyone should ship into a borrow path. The
form decision therefore has to give something up, and section 3.4 says exactly what.

**(c) A lock-free structure cannot physically unlink an arbitrary interior node.** `Remove` is
called from three cold paths — `Evict` (`HayateObjectPool.cs:2451`), the eviction sampler (`:3663`)
and the idle validator (`:3925`) — and each one wants "claim this idle object and take it out of the
pool, and tell me if you could not". The textbook answer is a tombstone plus a skip on pop, which
drags seven cold-path methods (`Remove` ×3, `Count` ×10 call sites, `GetAll`, `SnapshotHead`,
`DrainForClear`) into a new identity model and retires the two `node.List` checks that currently
prove a node belongs to this shard (`Shard.cs:505`, `:974`).

This repository has already solved the same shape of problem once. The lean fast path stores values
in a bounded buffer through per-slot compare-exchange (`HayateObjectPool.Lean.cs:240-314`), and the
O-D backend made that array growable (`:65-71`, `:329-359`). The chosen form is that protocol moved
into the shard, which is why (a) and (c) both stop being obstacles.

## 3. G-9 — the lock-free shard store

### 3.1 Layout

Per shard, when `EnableLockFreeStorage` is on:

- A **slot array** `HayateObject<T>[]` sized to the shard's current maximum. A wrapper occupies one
  slot and keeps it; the slot index travels on the wrapper.
- A **free-slot stack** of indices — a Treiber stack whose head is a **version-packed** `long`,
  `PackHead(version, index)`.
- A **round-robin cursor** advanced with `Interlocked`, giving a shared starting point for the scan.
- The shard's existing cache-line padding (3.0 `G-4`: the leading half in `CacheLinePadded`, the
  trailing half in `Shard._trailingPad`) still covers the new fields.

The free-slot stack is where the version-packed head belongs, and it is not decoration: indices are
recycled, so a popped index can legitimately return to the head between a reader's load and its
compare-exchange, and a version is the only thing that tells the two apart. The slot claim itself
needs no version, see below.

### 3.2 The protocol

Take, over the slot the cursor points at, walking round-robin:

    var slot = slots[i];
    if (slot is not null && ReferenceEquals(slot, Interlocked.CompareExchange(ref slots[i], null, slot)))
        => this thread won the object

Return:

    if (Interlocked.CompareExchange(ref slots[i], item, null) is null) => parked

Claim-and-remove (the cold `Remove`): the same compare-exchange, which is why it keeps its current
meaning — the single thread that exchanges the wrapper out of its slot owns the destruction, and
everyone else observes a non-null slot and returns false.

**Why no ABA here.** A slot holds `null` or the one wrapper that owns it. A stale reader that loads
wrapper `W` and then finds its compare-exchange to succeed has taken `W`, and `W` genuinely was
available — the only two writers are "this slot became empty" and "`W` came back". There is no link
being followed, so there is no link to be stale. That is the whole reason the form survives without
packing pointers.

### 3.3 Divergence from the blueprint, stated plainly

Two blueprint elements are deliberately **not** ported, and this is the redesign the plan's
migration boundary calls for rather than a departure from it:

- **The index-addressed node array.** Moving nodes off the wrappers buys a packable head for a
  linked structure, and costs `Remove` its physical unlink plus the seven-method identity migration
  in section 2(c). Slot ownership buys a packable head *only where a head is needed* and keeps
  `Remove` meaning what it means today. The array this design does keep is the slot array, which is
  the wrapper store rather than a node pool.
- **`[ThreadStatic]` stripe affinity.** The shard *is* the stripe here, and the pool already routes
  borrows through a ring scan from an affinity start (`HayateObjectPool.cs:1350`, `:1796`)
  with an optional golden-ratio thread hash (`:3329-3345`). A second, private affinity would be a second
  mechanism for the same job. The store's cursor is a *scan hint*, not an affinity: it spreads
  concurrent scanners across the shard without claiming that a thread owns a slot.

### 3.4 What is given up — and how it is refused rather than ignored

**Retrieval order is no longer return order.** A slot array has no order to keep: a wrapper owns a
slot for as long as it lives, so the sequence a borrow walks is the slot layout, not the sequence the
returns happened in.

That is not something to leave for a user to discover, so the combination is **refused at build
time**, exactly as the lean fast path already refuses a borrow order it cannot honour
(`HayatePoolBuilder.cs:2083-2092`):

> `EnableLockFreeStorage` with `BorrowStrategy != Fifo` throws from `Build()`. The default `Fifo` is
> what a pool that never asked for anything holds, so a pool that simply turns the backend on
> builds; a pool that asked for `Lifo`, `Lru` or `Priority` and then turned the backend on is told
> the two cannot both hold.

**`SnapshotHead` stops being the coldest end.** The eviction sampler uses it to look at the oldest
idle objects first (`Shard.cs:1111`), which is what makes `MaxIdleTime` and `MaxLifeTime`
retirement fair. On this backend the sample is a contiguous run of the shard's slots — a fair sample,
but not an age-ordered one. Consequence, to be stated in the eviction documentation: an object past
its idle limit is reclaimed when it is sampled, and sampling no longer guarantees that the oldest
objects are the ones sampled. Correctness of the limits is unchanged; the ordering preference is
gone.

**Nothing else moves.** `Count`, `GetAll`, `DrainForClear`, the registry, the borrowed list (K2) and
the spare-wrapper stack keep their semantics, because `Remove` kept its own.

### 3.5 G-23 — the capacity-adaptive slot layout

The slot array as §3.1 leaves it puts **eight adjacent slots in one cache line** on a 64-bit runtime.
The compare-exchange on each slot is independent; the line is not. Two threads exchanging two different
slots ping-pong that one line between their cores, so a CAS that should have been independent pays the
coherence traffic anyway. `33-R-9` is that observation, and the layout below is its answer.

Per shard, decided at construction from that shard's own capacity (`Shard.cs:347-352`):

| | compact | spaced |
| :--- | :--- | :--- |
| taken when | capacity > 64 | capacity ≤ 64 |
| `_slotStride` | `1` | `CacheLineSize / IntPtr.Size` — 8 on a 64-bit runtime, 16 on a 32-bit one (`Shard.cs:191`, `Common/CacheLinePadding.cs:30`) |
| `_slotFirst` (physical position of logical slot 0) | `0` | one stride, which is what keeps slot 0 off the array header's line — the line every bounds check reads |
| array length | `capacity` | `first + capacity × stride` |

64 is TRTOP Reservoir's `MaximumCacheLineSlotCapacity`, taken over as the boundary at which the spaced
form stops paying for itself, and the memory arithmetic is the whole of the reason. The spacing costs
one reference per stride position: a shard of 64 slots holds 8 + 64 × 8 = 520 references instead of 64
— about 4 KB instead of 0.5 KB on a 64-bit runtime, which is a fine price for spreading apart the eight
slots that share a line. Above the boundary the array keeps growing linearly while what it removes does
not: the number of threads that can contend for one line is bounded by the pool's concurrency, not by
the array's size, so a shard of 512 slots would hold 4 104 references — 32 KB — to de-contend a line
that a handful of threads are exchanging.

**The two layouts are one protocol.** They differ in a single mapping, applied at every slot access:

    private int SlotPosition(int logicalSlot) => _slotFirst + logicalSlot * _slotStride;   (Shard.cs:383)

A wrapper's `SlotIndex` stays a **logical** slot, so the free-slot stack, the wrapper's identity and the
`G-14-A` second-return guard keep their meaning unchanged; only the physical position moves. Seven paths
apply the mapping — the second-return guard (`:595`), the return (`:609`, `:631`, `:647`), the take scan
(`:783`, `:795`), `RemoveLockFree` (`:1019`), `GetAll` (`:1048`), `SnapshotHead` (`:1096`) and
`DrainForClearLockFree` (`:1208`) — and each walks *logical* slots and maps as it goes, because walking
array positions would spend seven of every eight probes on the spacing, every one of them on `null`.

The take scan is the one place the mapping is not written as a call, because it runs once per probe on
the hot path and the compact layout is the one a large shard uses:

    var position = _slotStride == 1 ? i : SlotPosition(i);   (Shard.cs:795)

So the compact layout pays one predicted branch per probe instead of a multiply, and the spaced layout
pays the multiply it needs.

**The layout is frozen at construction, and the pool already guarantees that.** The array is sized once,
so the mapping cannot change afterwards; `_lockFreeSlotCeiling` (`HayateObjectPool.cs:862`, `:4202`)
already refuses a `ReloadConfig` that would raise `MaxPoolSize` past the array, which is why this needs
no new guard and no new option.

**Per shard, not per pool.** The decision reads the shard's own capacity, so a pool whose shards straddle
the boundary holds both layouts at once: 257 over four shards is 65 / 64 / 64 / 64 — one compact shard
and three spaced ones in the same pool. The asymmetry is harmless because the layouts share the protocol;
it would not be if they were two code paths.

**The striped half of the item is deliberately not ported, for the reason §3.3 already gives.** `33-R-9`
proposes a second, striped store above the boundary (the blueprint's TRTOP/Reservoir pair). The shard
*is* the stripe here: the pool routes borrows through a ring scan from an affinity start with an
optional golden-ratio thread hash (`HayateObjectPool.cs:1350`, `:1796`, `:3329-3345`), so a striped
store would be a second mechanism for a job the sharding layer already does — the same argument that
refused `[ThreadStatic]` affinity and the index-addressed node array. What the item is left with, once
that half is accounted for, is the layout, and that is what landed.

**What pins it, and why the layout facts are not enough on their own.** `CacheLineSlotLayoutTests`
states the cache line independently (64) and measures the layout through the four internal diagnostics —
`SlotStride`, `SlotFirst`, `SlotCapacity`, `SlotArrayLength` (`Shard.cs:208-217`), the same
"diagnostic read by reflection" shape the `G-9` take counter uses — so a boundary that moved, a stride
that changed or a first offset that was dropped all go red. Those numbers do not pin the mapping: a
mapping that ignored the stride would still leave every wrapper *somewhere*, the store would still work,
and only the benefit would be gone. So one case reads the array itself and asserts where the wrappers
land — position 0 stays empty, the first wrapper sits at `stride`, the second at `2 × stride`, and a take
clears exactly the position it read. The `G-9` behaviour cases run unchanged; because their pools sit
under the boundary, they now exercise the spaced layout, which is also why the compact layout needed its
own coverage rather than borrowing theirs, and why the hand-written control in `CacheLinePaddingTests`
had to gain the three new fields.

## 4. G-10 — the per-pool thread-local front slot

### 4.1 Layout and lifetime

One `ThreadLocal<Slot>` field **on the pool instance** — per pool, not per type, so named pools of
one element type do not share a front tier. `trackAllValues: true`, which is what makes the slots
enumerable and therefore clearable; `Clear` and `Dispose` both dispose it.

Depth is **one slot**: the front tier holds at most one idle object per thread, which is the shape
the lean fast path already proves out. A thread cycling through several objects in sequence will not
hit it every time; that is the accepted cost of a tier that needs no `Count`, no CAS loop and no
ordering.

Borrow: check the front slot first; on a hit take it without touching any shard. On a miss, fall
through to the shard path unchanged — which is `G-9` when it is on, and the spin-lock list when it
is not.

Return: park the object in the front slot when the thread may (see 4.2) and the slot is empty;
otherwise take the existing shard path.

Both statements cover the asynchronous borrow and release pair as well, and they have to. `CompleteReturn`
is shared by the two release paths, so an asynchronous pool parks like any other; an asynchronous borrow
that did not read the slot back would strand every object such a pool parked, in a slot nothing reads.
The tier itself stays per *thread*, so an asynchronous flow that resumes on a different pool thread
simply misses the slot — that is a property of a thread-local tier rather than an exception to it.

### 4.2 The `Rents` rule

A slot records how many objects its thread has borrowed. **Only a thread that has borrowed may park
a return in its own slot.** Without that rule a thread that never borrows — an I/O completion
callback, a hand-off worker — parks objects in a slot nobody will ever read, and the objects are
unreachable to every other borrower while still counted as live: the shared layer starves with
objects sitting in it. This is the non-obvious part of the blueprint and it is ported as-is.

Where the count is taken follows from what the rule means. It is incremented where a borrow is *handed
over* — both borrow loops and the create-on-miss hand-out — rather than where the object was taken, so a
take that the active-object ceiling refuses and the loop retries does not count as a borrow this thread
made. It must not be a slot hit that increments it: the first park is the park that needs the count, so a
rule that only a slot hit could satisfy would leave the slot permanently empty.

### 4.3 Clear, Dispose and the parking race

A generation counter on the pool plus a process-wide memory barrier reconcile "a clear is draining
the front tiers" with "a borrower is about to park". The direction is fixed: **the clear wins**. An
object that loses the race is dropped and destroyed, never left parked past the clear.

### 4.4 Visibility and accounting

An object parked in a front slot is idle, but it is not in any shard's list, so it is invisible to
everything that measures or sweeps shards. Four consequences, all of which the implementation has
to answer rather than document away:

- **Idle accounting.** The pool derives its idle and borrowed counts from the shards
  (`HayateObjectPool.cs:154`, `:177`, `:1051`, `:3446`). A parked object would be reported as
  borrowed. The pool therefore keeps a front-tier count, maintained where parking and unparking
  happen, and folds it into those derivations.
- **Eviction.** A parked object would never be sampled, so it would never retire on idle or lifetime
  limits. The eviction sweep reads `ThreadLocal.Values` — this is the second thing `trackAllValues:
  true` buys — on the cold path only.
- **Clear.** `Clear` and `Dispose` sweep `Values` and destroy what they find, on the same terms the
  shard drain uses.
- **The borrowed list (`K2`).** A parked object stops being in flight, so the park unlinks it from the
  borrowed list and the slot hit links it back. Answering only one half would be worse than answering
  neither: an object borrowed out of the slot and then abandoned would be invisible to the abandoned
  scan, so the loan would fall outside `K2` silently rather than loudly.

The front-tier count costs one atomic per park and per hit. That is one more than a pure front tier
would need; it is bought so that the pool's own numbers stay true, and it is still far short of the
lock the shard path pays.

## 5. Wiring checklist

Every storage-shaped switch in this repository travels the same seven wiring points; the O-D
`EnableArrayPoolStorage` precedent is the template, and dropping one of them is a silent
configuration loss rather than a smaller feature.

| # | Point | `G-9` | `G-10` |
| :-: | :--- | :--- | :--- |
| 1 | `HayatePoolOptions` property (default `false`) | `EnableLockFreeStorage` | `EnableThreadLocalFrontSlot` |
| 2 | `CopyTo` | travels with the execution-mode block, like `EnableArrayPoolStorage` (`HayatePoolOptions.cs:1377`) | same |
| 3 | `ApplyFeatureSwitches` | forced `false` by `EnableLean` (`:1514`) | same |
| 4 | `IsValid` | nothing new: the non-`Fifo` conflict is a `Build()` refusal, matching the lean guard | nothing new |
| 5 | builder writer | `WithLockFreeStorage(bool = true)` | `WithThreadLocalFrontSlot(bool = true)` |
| 6 | `Build()` refusal | `EnableLockFreeStorage && BorrowStrategy != Fifo` throws | `EnableThreadLocalFrontSlot && BorrowStrategy != Fifo` throws — added with G-21, because a tier hit is served before the shard is asked, so the order could only hold on a tier miss |
| 7 | docs | this page, `README.MD`, `docs/hot-path-costs.md` | same |

Presets do not touch either switch. The lean profile normalizes them off, because lean has its own
storage and a switch that is on but cannot apply is exactly the failure mode this list exists to
prevent.

## 6. Acceptance anchors

1. **Off means off.** With both switches off the borrow and return paths gain a predicted branch and
   nothing else; the existing hot-path numbers and allocations do not move.
2. **The lock is gone, observably.** With `G-9` on, the take path must not enter the shard spin lock.
   Asserted through a diagnostic counter on the store, not through a faster wall-clock reading.
3. **Counter-proof with discriminating power.** Mutate the concurrency protocol at more than one
   point (the rule that a double guard makes a single mutation undetectable applies here), and
   assert the *exact* set of cases that turns red. Mutation arms must still compile.
4. **The cold paths keep their meaning.** `Remove` at its three call sites, `SnapshotHead`,
   `GetAll`, `DrainForClear` and `Count` at its ten call sites pass unchanged with `G-9` on. This
   anchor is the reason the slot form was chosen over a linked one.
5. **All target frameworks.** Seven frameworks build with zero warnings and zero errors; frameworks
   without `ArrayPool<T>` and without the newer `Interlocked` overloads take their documented
   downgrade.
6. **The `Rents` rule is observable.** A thread that never borrowed must be unable to park; the
   object must still be borrowable from the shared layer afterwards.
7. **A clear leaves nothing parked.** After `Clear`, no front tier holds an object.
8. **Benchmarks.** Both switches are off by default, so a hot row that can turn them on must exist
   before a re-capture could measure anything. The 2026-10-13 re-capture already cleared the gate's
   calibration caveat, so landing these two items does **not** oblige another one; a re-capture is
   only worth doing if the gains are to be recorded in the baseline.
9. **The layout is measurable, and the mapping is observable.** (`G-23`) The two numbers that describe
   the array — the stride and the position of logical slot 0 — are stated by the test rather than copied
   from the constant, and the mapping is pinned by where a wrapper actually lands in the array rather
   than by the store still working. Both halves are needed: the numbers alone cannot see a mapping that
   is consistently wrong, and behaviour alone cannot see a layout that stopped spacing.

## 7. Deliberately out of scope

- The lean fast path and its O-D backend. Lean and the general engine dispatch to each other by a
  constant branch at every entry point (`HayateObjectPool.cs:1030`, `:1250`, `:2034`, `:4228`), so
  neither of these backends is reachable from a lean pool.
- The borrowed list (K2), the per-shard registry and the spare-wrapper stack. None is on the borrow
  or return path, and the spare stack's own comment (`Shard.cs:257-264`) already argues why a
  Treiber stack would be wrong there.
- The **striped** half of `33-R-9` (`G-23`): a second store chosen by the capacity boundary. The shard
  is already the stripe, so what that half asks for is the sharding layer's job. See §3.5.
- 128-bit compare-exchange, `cmpxchg16b` intrinsics and hazard pointers: not portable to
  `netstandard2.0` / `net48`.
- `G-11` (struct policy specialization), which waits on the `33-R-1` data.
- Any change to the gate, the baseline, or the benchmark project.

## 8. Revision history

- **2026-10-13** — first version. Form decided: slot-owned compare-exchange for `G-9` (the "slot"
  form, with the free-slot stack as the only version-packed head), single slot for `G-10`.
- **2026-10-13** — built. Two questions this page had left open were settled while building, both
  because the tests written against the page caught the page being incomplete rather than the code
  being wrong:
  - **§4.1 covers the asynchronous borrow as well.** The page said "borrow" without qualifying it, and
    the shared `CompleteReturn` means an asynchronous pool parks. Reading the slot back only on the
    synchronous path would have stranded every object an asynchronous pool parked.
  - **§4.2 pins where the count is taken.** "How many objects its thread has borrowed" is the hand-out,
    not a slot hit — a slot-hit-only rule can never fill the slot, because the first park is the park
    that needs the count.
  Also settled in the same pass, and visible in §4.4 rather than as a new section: the park unlinks the
  wrapper from the borrowed list (`K2`) because a parked object is idle, and the slot hit links it back;
  without the pair, an object borrowed out of the slot and then abandoned would be invisible to the
  abandoned scan.
- **2026-10-13** — gated. A third consequence, this one outside the page's scope but inside the change's
  blast radius: the shard gained seven fields, and the hand-written control in `CacheLinePaddingTests`
  (`UnpaddedShardShape`) mirrors the shard's field set by name and by storage size deliberately, so that
  a field added to the shard breaks the test instead of silently weakening it. It did, at the count
  (21 against 14). The control was extended to match; the lock-free arrays are the shared empty-array
  singletons on the off path, so the two mirrored fields cost the control no bytes and the paired
  difference still measures the padding alone.
- **2026-10-15** — `G-23` (`33-R-9`) built. The capacity-adaptive layout of §3.5, and two things that
  settled while building it:
  - **The take scan keeps a compact fast path.** Writing every access as `SlotPosition(i)` was the
    obvious shape, and it is the wrong one for the scan: it runs once per probe, and the layout that
    would pay a multiply for nothing is the compact one. The scan asks `_slotStride == 1` once per
    probe instead — one predicted branch on the layout a large shard uses.
  - **The layout facts are not the whole of the test.** A mapping that ignored the stride would leave
    the store working and only the benefit gone, so a case had to observe the array rather than the
    numbers — see §3.5 and the ninth acceptance anchor. The reverse comparison agreed, and added a
    limit worth knowing: the arm that breaks the scan's mapping makes two cases *block* rather than
    fail, so it can only be run against the future tree. The legacy copies are bare `[Fact]` — xUnit v2
    cannot bound a synchronous case — and the arm ran for six hours before that was understood.
  - **The page's older citations were re-anchored.** Every `File.cs:line` reference on this page was
    re-checked against the tree on 2026-10-15, and ten of them — plus the bare `:line` continuations
    that follow them — no longer resolved to the statement they were attached to: the 3.1 items landed
    between this page being written and `G-23` moved the code under them. They now point at the current
    sites, which is the only kind of citation worth having.
