# Pool-name-scoped policies

This page is the **single source of truth** for the decision behind 3.0 item `B8` — resolving a
policy per named pool instead of one policy per element type. It records the **form decision**
(`B8-1`) and is written before the code, for the same reason
[`async-policy.md`](async-policy.md) was: the shape of a resolution contract is what consumers code
against, and settling it in the open is cheaper than settling it in a migration note.

**Implementation status: `B8-1` decided — form ①, the additive factory interface. `B8` not yet
implemented; this page describes the contract it will implement.** Where this page and the code
disagree, this page is the design and the code is the bug — until a decision here is deliberately
revised, in which case this page is updated first.

## 1. The gap

`IHayateObjectPolicy<T>` has no pool-name parameter, in any of its members:

```csharp
public interface IHayateObjectPolicy<T> where T : class
{
    T Create();
    bool OnRelease(T item);
    bool Validate(T item);
    void OnAcquire(T item);
    void OnPassivate(T item);
    void OnDestroy(T item);
}
```

Resolution has exactly one shape. In `BuildPool<T>` the pool name is in scope — it is the second
parameter — and the policy is still fetched by element type alone
(`ServiceCollectionExtensions.cs:275`):

```csharp
private static IHayateObjectPool<T> BuildPool<T>(IServiceProvider sp, string poolName)
    where T : class
{
    var options = sp.GetRequiredService<IOptionsSnapshot<HayatePoolOptions>>().Get(poolName);
    var policy = sp.GetRequiredService<IHayateObjectPolicy<T>>();
    ...
}
```

And `AddNamedPool<T>(name, configure)` seeds that single registration with a gap-filling
`TryAddSingleton` (`ServiceCollectionExtensions.cs:182`):

```csharp
services.Services.TryAddSingleton<IHayateObjectPolicy<T>>(sp => HayateObjectPolicies.Default<T>());
```

Two consequences follow, and they are the whole of the gap:

1. **Every named pool of one element type shares one policy instance.** `AddNamedPool<MyConnection>("primary")`
   and `AddNamedPool<MyConnection>("replica")` resolve the same `IHayateObjectPolicy<MyConnection>`,
   because `TryAddSingleton` keeps the first registration and silently ignores the rest. Declaring
   a second name does not give it a second policy — it gives it the first one.
2. **"primary uses connection string A, replica uses connection string B" is not expressible.**
   That is the ordinary shape of the request, and the current contract cannot state it. The
   `Func<IServiceProvider, IHayateObjectPolicy<T>>` overloads added by 2.9's `B2` do not close it
   either: the factory gets the container, from which it can read application configuration, but
   the pool name is `BuildPool`'s argument and never reaches policy resolution.

This is a **gap, not a defect**. `IHayateObjectPolicy<T>` behaves as documented, and two alternatives
already work today, both recorded in the 3.0 plan as deliberate design boundaries:

| Alternative | Shape |
| :-- | :-- |
| `ParameterizedHayatePool<TKey, TValue>` | A parent pool whose `subPoolFactory` gives each key its own policy — the right tool when the set of names is a runtime concern. |
| A distinct element type per policy | `PrimaryConnection` / `ReplicaConnection`; the type carries the policy identity, and DI resolves one policy per type. |

Neither is a substitute when the names are known at registration time and the element type is
genuinely the same — which is the case this item exists for.

## 2. The two candidate forms

| Form | Content | Breaking | Deferrable to 3.1 |
| :-: | :-- | :-- | :-- |
| **① Additive factory** | Introduce a pool-name-aware `IHayateObjectPolicyFactory<T>`; leave `IHayateObjectPolicy<T>` and its existing registration and resolution paths untouched. | **No** — purely additive (`P-2`) | Yes |
| **② Keyed services** | Turn `IHayateObjectPolicy<T>` itself into a keyed service, resolved with `AddKeyedSingleton` / `GetRequiredKeyedService`. | **Yes** — changes the registration and resolution shape | No |

## 3. Decision: form ①

**`B8` takes form ①.** The reasoning, in order of weight:

**It does not contradict a position this library already ships.** Named pools deliberately avoid
keyed services, and that decision is documented in both the README and the 2.9 changelog: keyed
registration "only exist[s] in version 8.0 and later of the abstractions package, so a keyed
registration would be unavailable to the net6.0 / net7.0 targets this library ships, and would
couple net48 behavior to the version of `Microsoft.Extensions.DependencyInjection` the application
happens to resolve". Form ② would take that same coupling and introduce it into the *policy*
resolution path, which is the one path a consumer's own type system plugs into. Whatever the
mechanism, the library should not answer "which policy?" and "which pool?" with two different
dependency shapes.

**It is additive, so it cannot break anyone.** Form ① adds a type and one branch. A consumer who
registers no factory gets the current path, byte for byte: `GetRequiredService<IHayateObjectPolicy<T>>()`
is still the only lookup, and it is still what runs. Form ② changes what an existing registration
*means*, which 2.9's plan already classified as a 3.0-bound breaking change, and which would need a
`docs/BREAKING-CHANGES.md` section and a `breaking_registry.json` entry — a migration note for
every existing named-pool user, in exchange for a shape the library has already argued against.

**Form ② is not even free to implement.** `AddKeyedSingleton` arrived in
`Microsoft.Extensions.DependencyInjection` 8.0. A probe that compiles the same source against each
target framework's pinned abstractions version shows where it exists:

| Target | `M.E.DI.Abstractions` | `AddKeyedSingleton` |
| :-- | :-- | :-- |
| `net48` | 8.0.2 | compiles |
| `net6.0` | 6.0.0 | `error CS1061` |
| `net7.0` | 7.0.0 | `error CS1061` |
| `net8.0` | 8.0.2 | compiles |

So form ② needs `#if` forking across the two lowest target frameworks — or raising their dependency
versions, which is a separate decision about the dependency matrix (3.0 item `L9`), not a free
consequence of wanting per-name policies. Note also what the probe corrects: the constraint is
`net6.0` / `net7.0`, **not** `netstandard2.0` or `net48` as the plan originally assumed. The
DependencyInjection package does not target `netstandard2.0` at all, and its `net48` leg resolves
the 8.x abstractions, which do carry the API.

**Priority is unchanged.** Form ① is `P-2`, so `B8` stays **P2**; had form ② been chosen the plan
required re-rating it to `P1` and binding a migration note to it.

## 4. The intended contract

```csharp
namespace DotNetCore.HayateOP.DependencyInjection;

public interface IHayateObjectPolicyFactory<T> where T : class
{
    IHayateObjectPolicy<T> Create(string poolName);
}
```

Three properties make this the shape it is:

- **One factory, dispatching on name.** The factory is a single service of a single service type;
  it receives the pool name and decides. Per-name registration is therefore not needed, which is
  what keeps resolution off keyed services. The alternative — a factory *per* name — would need
  keyed resolution to find them, which is form ② wearing a different hat.
- **It lives in the DependencyInjection package**, in `DotNetCore.HayateOP.DependencyInjection`,
  alongside the container-facing surface and under the namespace convention 3.0's `L8` established
  for types this package owns. The core package keeps its zero-dependency policy.
- **It is optional at every point.** No factory registered ⇒ the existing lookup runs and nothing
  observable changes.

`BuildPool<T>` becomes a preference rather than a replacement:

```csharp
var policyFactory = sp.GetService<IHayateObjectPolicyFactory<T>>();
var policy = policyFactory is not null
    ? policyFactory.Create(poolName)
    : sp.GetRequiredService<IHayateObjectPolicy<T>>();
```

The factory is consulted **once per pool build**, on the cold path, next to the options and scaling
strategy lookups that are already there. It is not consulted per borrow, and it is not consulted on
the hot path.

## 5. What does not change

- `IHayateObjectPolicy<T>` — same members, same signatures, same semantics.
- Its registration and resolution: `AddSingleton` / `TryAddSingleton` and
  `GetRequiredService<IHayateObjectPolicy<T>>()` keep working exactly as they do today, for the
  unnamed pool and for every named pool.
- `AddNamedPool<T>` / `AddPool<T, TClient>` / `RegisterHayatePool<T>` / `RegisterNamedHayatePool<T>`
  — same signatures, same behaviour when no factory is present.
- `HayatePoolBuilder<T>.WithPolicy` and the core package's public surface — untouched.
- Target frameworks — the factory is a plain interface, so it is produced on every target the
  DependencyInjection package ships, with no `#if`.

## 6. Deliberately out of scope

- **A policy per name without a factory**, i.e. `AddNamedPool<T>(name, policy)`. A per-name policy
  argument would have to be found per name at resolution time, which needs the keyed lookup form ②
  was rejected for. The factory covers the same ground with one service type.
- **Changing `IHayateObjectPolicy<T>`**, in any way, including adding an optional name parameter.
  Its implementers are the people this item exists to help.
- **`ParameterizedHayatePool<TKey, TValue>` and the per-element-type alternative** — both stay as
  they are; this item adds a third route, it does not deprecate the first two.
- **Keyed services anywhere in this library.** The README's position stands and this decision
  reaffirms it.

## 7. Acceptance anchors

`B8` is done when:

1. With a factory registered, `AddNamedPool<MyConnection>("primary")` and
   `AddNamedPool<MyConnection>("replica")` resolve **different** policy instances, and the policy
   each pool uses is the one the factory returned for that name.
2. With no factory registered, the existing `IHayateObjectPolicy<T>` registration path behaves
   **bit for bit** as before — covered by targeted cases, not by inspection.
3. `src` 10 projects, Release and Debug, `--no-incremental`: **0 warnings, 0 errors**.
4. No `docs/BREAKING-CHANGES.md` section and no `breaking_registry.json` entry: form ① is additive,
   so there is nothing to migrate.
