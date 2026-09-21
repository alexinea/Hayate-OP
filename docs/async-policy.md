# Asynchronous policy contract

This page is the **single source of truth** for the asynchronous surface introduced in 2.9
(A1 — asynchronous creation, A2 — asynchronous disposal, B5 — asynchronous return). It is written
**before** the code on purpose: the three items ship as one batch, because shipping them in two
releases would append members to `IHayateAsyncObjectPolicy<T>` after implementers already coded
against it, which is a second breaking change for exactly the people the interface is meant to
help.

**Implementation status: A1 (asynchronous creation) implemented, A2 and B5 pending.** The engine
dispatches `CreateAsync` on every creation path — general and lean, synchronous and asynchronous
entry points — so a synchronous caller waits on the asynchronous hook instead of calling the
synchronous one (rule 1 below). `OnReleaseAsync`, `OnPassivateAsync` and `OnDestroyAsync` are part
of the interface an implementer must supply, but the engine does not call them yet; A2 and B5 wire
them. Where this page and the code disagree, this page is the design and the code is the bug —
until a decision here is deliberately revised, in which case this page is updated first.

## 1. Why the synchronous contract is not enough

Creating a pooled connection is I/O: `TcpClient.ConnectAsync`, `DbConnection.OpenAsync`, an SMTP
`STARTTLS` handshake, a Redis `AUTH`. Today the only way to express that inside
`IHayateObjectPolicy<T>` is to call `GetAwaiter().GetResult()` inside the synchronous `Create()`.
On ASP.NET Core that occupies a request thread and raises thread-pool pressure; on a host with a
`SynchronizationContext` it can deadlock. For a library that positions itself on performance that
is a structural contradiction, not a missing optimisation.

The same holds on the way out. The correct teardown for `NetworkStream`, `SslStream` and
`DbConnection` is `DisposeAsync()`; the synchronous `Dispose()` either blocks on the network stack
or leaves a half-closed connection behind.

## 2. Target framework gating

| Target | Gets the asynchronous contract |
| :- | :- |
| `net6.0` and later | Yes — `IHayateAsyncObjectPolicy<T>`, `IHayateAsyncObjectPool<T>`, the asynchronous lease form |
| `netstandard2.0`, `net48` | **No** — the types are not produced, and the build stays error-free |

The gate exists because the core package keeps its zero-dependency policy (`src/HayateOP/dependency.props`
is empty, and `CHANGELOG.md` records it as a deliberate decision). `ValueTask` and `IAsyncDisposable`
are not available on the two legacy targets without taking a dependency, so those consumers keep
running the synchronous pool. That is a *synchronous* pool, not an asynchronous one wearing a
disguise: **no `Task.Run` pseudo-asynchrony on `net48`** — it would amplify the thread pool and
make every benchmark comparison meaningless.

## 3. `IHayateAsyncObjectPolicy<T>`

```csharp
public interface IHayateAsyncObjectPolicy<T> : IHayateObjectPolicy<T> where T : class
{
    ValueTask<T> CreateAsync(CancellationToken cancellationToken = default);
    ValueTask<bool> OnReleaseAsync(T item, CancellationToken cancellationToken = default);
    ValueTask OnPassivateAsync(T item, CancellationToken cancellationToken = default);
    ValueTask OnDestroyAsync(T item, CancellationToken cancellationToken = default);
}
```

It extends — and does not replace — `IHayateObjectPolicy<T>`, so an existing policy keeps compiling
unchanged. The members mirror four of the six synchronous hooks:

| Synchronous hook | Asynchronous counterpart (2.9) |
| :- | :- |
| `T Create()` | `ValueTask<T> CreateAsync(...)` |
| `bool OnRelease(T)` | `ValueTask<bool> OnReleaseAsync(...)` |
| `void OnPassivate(T)` | `ValueTask OnPassivateAsync(...)` |
| `void OnDestroy(T)` | `ValueTask OnDestroyAsync(...)` |
| `bool Validate(T)` | none — see §7 |
| `void OnAcquire(T)` | none — see §7 |

> **Design note (to confirm before coding).** `OnRelease` returns `bool` — `false` destroys the
> object instead of returning it to the pool. `OnReleaseAsync` must therefore return
> `ValueTask<bool>`; the 2.9 plan sketches it as `ValueTask`, which would silently lose the
> accept/reject decision. This page takes `ValueTask<bool>`.

## 4. Dispatch rules

1. **A policy that implements `IHayateAsyncObjectPolicy<T>` takes the asynchronous path.** The
   engine calls `CreateAsync` / `OnReleaseAsync` / `OnPassivateAsync` / `OnDestroyAsync`. A
   synchronous entry point (`Acquire()`, `Release()`) still routes through the asynchronous hooks
   and waits on them; the synchronous path degrades to `GetAwaiter().GetResult()` rather than
   being a second, divergent implementation.
2. **A policy that does not implement it behaves exactly as it does today** — byte for byte, hook
   for hook. No new allocation, no new branch with an observable effect, no behavioural drift.
   This is what makes the batch additive rather than breaking.
3. **The two can coexist and rule 1 wins where they overlap.** A policy may implement both
   interfaces; when it does, the asynchronous hooks are the ones the engine calls and the
   synchronous hooks are only reached by code paths that never had an asynchronous counterpart in
   2.9 (validation and acquisition notification, §7). Implementing both is legal, but the two
   implementations must agree — a pool that accepts an object in `OnRelease` and rejects it in
   `OnReleaseAsync` gets rule 1's behaviour, and that is the caller's bug, not the engine's.

These three rules are the whole contract. Anything not stated here is not decided, and code
comments must not invent a fourth rule — if a case is missing, amend this page first.

## 5. Asynchronous disposal (A2, α form)

2.9 adds a **new** interface rather than changing the existing one:

```csharp
public interface IHayateAsyncObjectPool<T> : IHayateObjectPool<T>, IAsyncDisposable where T : class
{
}
```

`IHayateObjectPool<T>` is untouched. The alternative — β, adding `IAsyncDisposable` straight to
`IHayateObjectPool<T>` — would force every third-party implementer to add a `DisposeAsync`
member, which is a source-level break and belongs in a major release. α costs callers one
type test:

```csharp
if (pool is IHayateAsyncObjectPool<T> asyncPool)
{
    await asyncPool.DisposeAsync();
}
else
{
    pool.Dispose();
}
```

That cost is paid down by converging the shape in the facade (`HayatePool`) and in what
`AcquireAsync` hands back, so that most callers never write the test themselves.

`DisposeAsync` drains: it disposes the objects the pool owns, using `IAsyncDisposable` where the
object implements it and `IDisposable` otherwise, and it awaits the asynchronous destroy hook when
the policy provides one. The five destroy sites gain the `IAsyncDisposable` test —
`HayateObjectPool.cs` (three), `HayateObjectPool.Shard.cs` and `HayateObjectPool.Lean.cs` — and the
synchronous `Dispose()` keeps its current semantics: graceful shutdown does not change meaning for
anyone who does not opt in.

## 6. The return path and leases (B5)

`OnReleaseAsync` and `OnPassivateAsync` belong to the same interface as `CreateAsync` (§3), so a
policy expresses "roll the transaction back / send `RESET` / validate asynchronously" in one place
instead of blocking inside the synchronous hook.

`HayatePoolScope<T>` gains an asynchronous form on `net6.0` and later, so a lease can
`await using` where the underlying object needs an asynchronous return. The lease's guarantees do
not change: disposal stays idempotent, the object is returned exactly once, and the claim stays
atomic. On the legacy targets the lease keeps its synchronous `Dispose()` — those consumers never
had an asynchronous object model to return to.

## 7. Deliberately out of scope

* **No protocol or connection packages.** The 2.9 window keeps the Q5 boundary: a specialization
  that needs a protocol SDK or the network stack belongs in the downstream connection-pool
  library, not here. `Extensions.Specialized` stays BCL-only.
* **No asynchronous validation hook.** `Validate` keeps its synchronous shape in 2.9. An
  asynchronous `ValidateAsync` is a plausible follow-up — a health probe is I/O too — but adding
  it means deciding how a validation failure interacts with the borrow retry loop, and that is a
  semantic change with hot-path consequences, i.e. a major-version item. Callers who need it today
  do the probe in `OnReleaseAsync` / `OnPassivateAsync`.
* **No asynchronous acquisition notification.** `OnAcquire` stays synchronous; it is bookkeeping,
  not I/O.

## 8. Acceptance anchors

| Item | Must hold |
| :- | :- |
| A1 | An asynchronous creation path is exercised end to end; a policy that does not implement the interface behaves identically to today; `netstandard2.0` / `net48` build with zero errors and no new types |
| A2 | `DisposeAsync` drains the pool (rolling restart / graceful shutdown); `Dispose()` semantics unchanged; no source-level break for existing implementers |
| B5 | An asynchronous return is observable; the legacy lease behaviour is unchanged; the `G × R` and default-off cases stay behaviourally identical |

Builds are verified across `net8.0` / `net9.0` / `net10.0` for the future suites and `net48` /
`net6.0` / `net7.0` for the legacy suites, with "legacy compiles and behaves unchanged" as a hard
gate — the `#if` fork has to be compilable, not merely absent.
