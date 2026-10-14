# Object ownership and the return contract

**Returning an object transfers ownership.** Once `Release` has been called, the caller does not own
that reference any more: do not touch it, do not return it twice, and do not return it to another
pool.

That is the whole rule. This page is where it lives — it used to be spread across the XML
documentation of `Release`, the shard's append guard and the disposal helpers — and it states what
the pool actually does when a caller breaks it, because the three violations are **not equally
detectable** and the difference matters when you are reading a stack trace at 3 a.m.

## 1. The rule

| # | Rule | What it protects |
| :-: | :--- | :--- |
| 1 | Every `Acquire` is matched by **exactly one** `Release` | The pool's accounting: a lease that is never returned is invisible to the pool until leak detection reports it |
| 2 | Put the `Release` in a `finally` | The exception path — otherwise the object never comes back |
| 3 | Do not touch the reference after returning it | The object may be handed to another thread the moment it is back in the idle list |
| 4 | Do not return it twice | One reference must not become two leases |
| 5 | Do not return it to a different pool | The pool that receives it cannot tell whether the object is still in use by its real owner |

Rules 3–5 are the ownership-transfer rules; rules 1–2 are the accounting rules that make the transfer
happen at all. The standard skeleton that satisfies all five is in
[`docs/disposal.md`](disposal.md#6-the-rule-in-practice) and in the usage guide's borrow/return
chapter.

## 2. What the pool does when the rule is broken

The general-purpose engine verifies ownership on the return path. What it can see, and what it does
about it:

| Violation | Detected? | What happens |
| :--- | :--- | :--- |
| Touching the object after returning it (rule 3) | **No** | Nothing. The borrow path physically unlinks the object from the shard, so by the time the caller touches it the pool has no way to know. The write lands on an object another thread may already hold — a plain data race, and the one violation no library can catch for you |
| Returning `null` | Yes | `LogWarning("Returned null object to pool.")`, return value ignored. `Release(null)` does **not** throw |
| Returning an object that belongs to a different pool (rule 5) | Yes | `LogWarning("Returned object does not belong to pool. Disposing.")`, then the object is **destroyed** — not sent back to its real pool, which is not consulted. The release is recorded as unsuccessful |
| Returning an object that has already been returned and is sitting idle (rule 4) | Yes | The shard's append guard rejects it (an object that is in the pool is not in a state that may be appended), `LogWarning("Object rejected by shard on release. Removing from pool.")`, then the object is **destroyed** and unlinked from the store it was sitting in. The pool loses one object and the count drops with the refusal, so the next borrow creates a fresh one |
| Returning an object that has already been returned **and has since been borrowed again** (rule 4, the dangerous interleaving) | **No — and this is the one to fear** | The guard accepts it, because a borrowed object is in a state that may be appended. The same value is now both idle in the pool and held by another caller. A later borrow hands it out a second time: **two owners of one object**, with no exception anywhere |
| Returning after the pool was `Clear()`ed or disposed | Yes | The registry was emptied, so the object resolves to no wrapper and takes the "does not belong to pool" branch above: destroyed. See [`docs/disposal.md`](disposal.md#3-what-is-not-disposed) |
| `OnRelease` / `ValidateOnReturn` rejecting the object | Yes | `LogWarning(...)`, then destroyed — a rejected return is a destroy, not a re-queue |

Two consequences worth stating plainly:

* **By default `Release` is not a checked operation and does not throw for a misuse.** Every violation
  above is a warning plus a destroy. Nothing tells the caller that it made a mistake, so a double return
  shows up as "the pool keeps creating objects" or "the pool destroyed something I was still using",
  never as an exception at the call site. §3 is the opt-in that changes exactly that.
* **Rule 4's dangerous form is a race, not a typo.** If the second `Release` happens before the object
  is borrowed again, the pool rejects and destroys it — noisy, but safe. If it happens after, the pool
  accepts it and one object acquires two owners. The window is the time between the two returns.

Every destroy in the table is reported to a policy that implements `IHayateObjectLifecycleObserver<T>`,
with the `HayateDestroyReason` it went — a foreign object as `Misuse`, a second return rejected by the
shard as `Misuse`, a rejected return as `ReturnRejected`, a post-`Clear` return as `Misuse` (the registry
is gone, so it takes the foreign-object branch). This reports destroyed objects, not every misuse call:
for example, the known front-slot duplicate-return interleaving is not always detected by the shard.
See [`README.MD`](../README.MD#hearing-about-destruction) for the full set of reasons.

## 3. Making the mistake loud: `ErrorOnIncorrectUsage`

`ErrorOnIncorrectUsage` (the builder's `WithErrorOnIncorrectUsage()`, and the option of the same name)
moves the three detectable caller mistakes from the warning path to the exception path. Off by default,
so the table above keeps describing the shipped behaviour:

| Violation | With the switch on |
| :--- | :--- |
| Returning `null` | `InvalidOperationException` naming the pooled type |
| Returning an object from another pool | `InvalidOperationException`; the object is **left with the caller**, not destroyed — a return the pool refuses cannot also take ownership of it. The refused release is still counted |
| Returning an object the pool already holds | `InvalidOperationException`; nothing changes in the pool — not destroyed, not double-counted, the object stays where it was |

Three things it deliberately does **not** cover, because they are not the caller's mistake: a
`ValidateOnReturn` failure and an `OnRelease` rejection are verdicts about the object, and they stay
non-throwing; and rule 4's dangerous interleaving — the second return arriving after the object was
borrowed again — is indistinguishable from a legitimate return, so no switch can catch it.

The check costs the return path something, which is why the option is off by default and aimed at debug
and test builds: with the thread-local front slot on, the already-returned check enumerates the slot
tiers, because a parked object keeps its borrowed mark and only the tier knows it has been returned.
In lean mode only the `null` case is detectable, for the reason §4 gives.

After this section the numbering continues at §4.

## 4. Where the rule is *not* enforced: the lean fast path

`UseLeanProfile()` / `EnableLean` removes the per-object wrapper and the reverse lookup that the
ownership check needs. On that path the engine **cannot** tell where an object came from:

* An object from another pool is **accepted and pooled** — it is the same contract the reference
  zero-wrapper pool offers, and it is the deliberate price of removing the reverse lookup from the
  return path.
* A double return is likewise accepted, because there is no registry to notice.
* `null` is still rejected with a warning — and it is the one misuse even this path can detect, so it
  is the one that answers to `ErrorOnIncorrectUsage` here as well.

So the ownership rules are a **contract on the lean path** and an **enforced check on the general
engine**. If you are pooling objects you do not fully control — or you want the pool to catch a
mistake for you — do not use the lean profile for that pool. The trade is stated in
[`docs/hot-path-costs.md`](hot-path-costs.md): the lean path is roughly an order of magnitude
cheaper per round trip, and this is part of what is being bought.

## 5. Making the rule structural

The cheapest way to satisfy rules 1–4 is to stop writing them by hand. `AcquireScoped()` returns the
object together with its lease, so a `using` block is the whole borrow/return cycle and the compiler
owns the `finally`:

```csharp
using var lease = pool.AcquireScoped();
lease.Value.DoWork();
// leaving the block returns the object, exception or not
```

`AcquireScopeAsync(ct)` is the asynchronous twin. The lease type is a `sealed class`, so each
`AcquireScoped()` costs one small allocation — worth knowing on a hot path, and the reason a
zero-allocation lease is on the roadmap rather than in the box. Details in the usage guide's scoped
lease chapter.

## 6. How this compares with other pools

Not every pool checks. A zero-wrapper pool of the `DefaultObjectPool<T>` shape keeps no reverse
lookup and therefore accepts a foreign object — the same contract the lean path deliberately adopts.
A pool that stores objects behind a keyed/handle indirection can make misuse a **type-level** error
instead of a runtime one, at the cost of an allocation per borrow.

HayateOP's position is: check on the general engine, document the lean path's exception, and do not
add an allocation to the borrow path to buy a guarantee the general engine already provides.

## See also

* [`docs/disposal.md`](disposal.md) — who disposes what, and what `Clear()` / `Dispose()` do to
  objects that are still checked out.
* [`docs/hot-path-costs.md`](hot-path-costs.md) — what the ownership check costs, and what the lean
  profile removes to avoid it.
* [`docs/BREAKING-CHANGES.md`](BREAKING-CHANGES.md) — behavioural notes for the return path.
