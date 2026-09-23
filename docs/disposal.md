# Disposal and object ownership

`IHayateObjectPool<T>` derives from `IDisposable`, and disposing the pool is the normal
way to release the objects inside it. This page states exactly what the pool does with
those objects, what it deliberately leaves alone, and how the behaviour lines up with
`Microsoft.Extensions.ObjectPool.DisposableObjectPool<T>` — so that a host does not
dispose every pooled object by hand, and does not assume the pool will clean up more than
it actually does.

## 1. Ownership

* The pool owns an object **while the object sits idle in it**.
* A **checked-out** object belongs to the caller for the duration of the lease. The pool
  cannot reach it — the borrow path physically unlinks it from the shard's free list.
* `using var pool = new HayatePoolBuilder<MyResource>().Build();` is the intended teardown
  idiom.

## 2. What `Clear()` and `Dispose()` do

| Call | Effect |
| :--- | :--- |
| `Clear()` | Drains and disposes the objects the pool is holding **right now**. The pool stays usable: a later `Acquire` creates new objects as if the pool had just started |
| `Dispose()` | `Clear()`, then releases the shared background timer and the return-signal gate |

The drain itself, on the general-purpose engine:

1. Per shard, under the shard lock, the idle free list is copied out and emptied, and each
   wrapper is marked destroyed.
2. Outside the lock, every drained value goes through `Shard.SafeDispose`, which calls
   `IDisposable.Dispose()` when the value implements it. **An exception thrown by a user's
   `Dispose` is caught and logged**, so one badly-behaved object cannot abort the drain —
   the remaining objects are still released.
3. The shard registry is cleared.

On the lean fast path, `ClearLean()` releases every value the buffer retains through the
same destroy helper the normal path uses: `IHayateObjectPolicy.OnDestroy(value)`, then
`IDisposable.Dispose(value)`, then the slot reservation. Borrowed objects are untouched and
re-enter the buffer normally when returned.

## 3. What is *not* disposed

* **Objects currently checked out.** They are the caller's until returned. A caller who
  never returns an `IDisposable` object leaves it to the finalizer/GC, and the pool cannot
  help.
* **Borrowed objects after `Clear()` / `Dispose()`.** On the general-purpose engine the
  registry is emptied, so a later `Release` of such an object no longer resolves to a
  wrapper: it is treated as an object from another pool and disposed on the spot (logging a
  warning). The lean path keeps no registry at all, so it simply accepts the return into
  its buffer.

## 4. Alignment with `DisposableObjectPool<T>`

`DisposableObjectPool<T>` is the MEOP type to use when the pooled object implements
`IDisposable`. Its contract, read from the reference source:

* `Dispose()` disposes the object in the fast slot and drains the retained queue, calling
  `IDisposable.Dispose()` on every item that implements it.
* A checked-out object is left alone.
* After disposal the pool is **sealed**: `Get()` throws `ObjectDisposedException`, and
  `Return(obj)` disposes the object instead of pooling it.
* There is no policy destroy hook to invoke — MEOP's `IPooledObjectPolicy<T>` has only
  `Create` and `Return`.

HayateOP lines up with that contract on the points that matter:

| Aspect | `DisposableObjectPool<T>` | HayateOP |
| :--- | :--- | :--- |
| Who disposes the objects the pool holds | the pool, on `Dispose()` | the pool, on `Dispose()` (and on `Clear()`) |
| Checked-out objects | left alone | left alone |
| A user `Dispose` that throws | propagates out of `Dispose()` | caught and logged; the drain continues |
| Policy destroy hook on the drain | none exists | not called on the general engine; called on the lean path (see §5) |
| Using the pool after `Dispose()` | rejected: `Get()` throws `ObjectDisposedException`, `Return()` disposes | not rejected — see §5 |
| Releasing objects without tearing the pool down | drain the pool's internal queue only | `Clear()`, a public API |

**The practical consequence is the one worth stating plainly: a host does not have to
dispose the pooled objects itself.** Disposing the pool releases everything the pool is
holding, exactly as `DisposableObjectPool<T>` does. Disposing each object before returning
it is still correct — the pool tolerates it — but it is not required, and for a pool of
`IDisposable` objects it only costs the next borrow a re-creation.

## 5. Two differences to be aware of

**1. The `OnDestroy` policy hook does not run on a pool drain (general engine).**
`Shard.Clear()` disposes the value but does not call `IHayateObjectPolicy.OnDestroy`,
whereas every other destroy path — `Destroy(HayateObject<T>)`, `Destroy(T)`, the return
rejection path, the eviction path, and the lean drain — does. A policy that relies on
`OnDestroy` for bookkeeping (releasing a handle it manages separately, decrementing an
external counter) will not see objects that were drained by `Clear()` / `Dispose()`.

**2. There is no disposed-state guard.** MEOP's `DisposableObjectPool<T>` refuses further
use with `ObjectDisposedException`. HayateOP's `Dispose()` releases the timer and the
wake-up gate but keeps no disposed flag: the shard structures and the options remain
allocated, so a post-dispose `Acquire` is not rejected — on a drained pool the cold-boot
path sees an empty pool and hands out a freshly created object. Treat a disposed pool as
unusable and do not call into it; the type does not enforce it for you.

## 6. The rule in practice

```csharp
using var pool = new HayatePoolBuilder<MyResource>()
    .WithMinSize(4)
    .WithMaxSize(32)
    .Build();

var resource = pool.Acquire();
try
{
    // use resource
}
finally
{
    pool.Release(resource);   // always return what you borrow
}
// leaving the using block disposes the pool and releases everything still in it
```

* Dispose the pool; do not dispose each pooled object.
* Call `Clear()` when the objects must be released before the pool goes away.
* Always return what you borrow — the pool cannot dispose an object it does not hold.
* Never use the pool, or an object obtained from it and since returned, after `Dispose()`.

## See also

* [`docs/hot-path-costs.md`](hot-path-costs.md) — what eviction and the other background
  features cost, including the destroy path they share with `Clear()`.
* [`docs/BREAKING-CHANGES.md`](BREAKING-CHANGES.md) — the behavioural notes for
  `CreateNew`, lean mode's blocking wake-up granularity and `MinPoolSize = 0` cold pools.
