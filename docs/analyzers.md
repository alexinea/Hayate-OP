# Analyzers: moving four classes of pool misuse from run time to build time

Status: **designed 2026-10-13.** This page is the design source for `G-12` of the 3.1 capability line
(`33-O-1`). Per the B8-1 precedent the form is recorded before it is built, so the implementation is
measured against a stated contract rather than against what it happens to do. Section 11 records what
building it settled.

The package is `DotNetCore.HayateOP.Extensions.Analyzers` — a Roslyn analyzer + CodeFix package, not a
runtime library. It has no assembly that ships into the consumer's output: it is referenced with
`PrivateAssets="all"` and does all of its work inside the compiler.

The two blueprints are J2OPL's `ObjectPoolLinter` (the mechanism: rule set, CodeFix, configuration
diagnostics, `AnalyzerReleases`, and a written boundary statement) and S11ZXOP's `RefCounterAnalyzer`
(the evidence that an **Error**-level diagnostic is what actually forces a return). The mechanism
travels; the rule *content* does not, because J2OPL's rules are bound to Unity (`Update`,
`MonoBehaviour`, `Instantiate`, `GetComponentsInChildren`) and HayateOP has none of those.

## 1. The shape of the problem

HayateOP's contract is exclusive borrowing with an explicit return: `Acquire()` hands out an object
that must come back through `Release`, and the scoped forms (`AcquireScoped` → `HayatePoolScope<T>`,
`AcquireLeased` → `HayatePoolLease<T>`) return it from `Dispose`. Every one of those obligations is
enforced at **run time**, by the pool — and the run-time enforcement is deliberately forgiving: a
borrowed object that is never returned is simply counted as on loan forever, which is indistinguishable
from a long-lived borrow.

Four classes of misuse therefore reach production without a compile error today:

1. **A borrow is dropped on the floor.** The result of `Acquire` / `AcquireScoped` / `AcquireLeased` is
   discarded, or stored in a local nobody ever disposes. The object leaves the pool's available set
   permanently.
2. **A disposed pool is used again.** `Dispose` is terminal, but nothing stops a later `Acquire`.
3. **A blocking acquire is used where the async one belongs.** In an `async` method a synchronous
   `Acquire` blocks the caller's thread; held across an `await` it also removes an object from the pool
   for the duration.
4. **`OnRelease` tears the object down.** `OnRelease` answers "may this object be parked?" — disposing
   the item there destroys an object the pool still owns and will hand to the next borrower.

This package reports all four at build time. The rule that carries the most weight is the one that
S11ZXOP proved works: the lease rules are **Error**-level, so an unbalanced lease is a failed build, not
a warning to be scrolled past.

## 2. Scope: the six rules

| ID | Severity | Title | Fires when |
| :--- | :--- | :--- | :--- |
| <a id="hyo009"></a>`HYO009` | **Error** | Lease or scope acquired and never disposed | the result of `AcquireScoped` / `AcquireLeased` / `AcquireScopeAsync` / `AcquireLeaseAsync` is discarded, or assigned to a local whose only reference is its declaration |
| <a id="hyo001"></a>`HYO001` | Warning | Pooled object acquired and never released | the result of `Acquire` / `Acquire(TimeSpan)` / `AcquireAsync(...)` is discarded, or assigned to a local whose only reference is its declaration |
| <a id="hyo003"></a>`HYO003` | Warning | Disposed pool used again | within one method body, a pool on which `Dispose()` / `DisposeAsync()` was called is the receiver of a later acquire call |
| <a id="hyo004"></a>`HYO004` | Info | Synchronous acquire inside an async method | `Acquire` / `Acquire(TimeSpan)` is invoked from the body of an `async` method |
| <a id="hyo005"></a>`HYO005` | Info | `AcquireAsync` called without a cancellation token | `AcquireAsync` is invoked with no `CancellationToken` argument while the enclosing method has a `CancellationToken` parameter that could be forwarded |
| <a id="hyo006"></a>`HYO006` | Warning | `OnRelease` disposes the item | inside an `IHayateObjectPolicy<T>.OnRelease` implementation, the item parameter is disposed |

### 2.1 Why the lease rules are Error and the object rule is Warning

`HayatePoolScope<T>` is a **sealed class with no finalizer** and `HayatePoolLease<T>` is a **struct**.
Neither returns the object on its own: only `Dispose` / `DisposeAsync` does. A dropped lease therefore
loses the object for the life of the process, and no later code can recover it — there is no handle
left. That is the Error case.

A dropped `Acquire()` is the same loss in principle, but the diagnostic cannot distinguish it from an
object the caller intends to keep and release through a path the analyzer does not follow, and a
deliberate long-lived borrow is legitimate. It is reported as a Warning.

### 2.2 What "the result is discarded" means precisely

Two shapes, both decided by syntax, not by control flow:

```csharp
pool.AcquireScoped();                 // (a) the invocation is the whole expression statement
var lease = pool.AcquireLeased();     // (b) a local whose only reference is this declaration
```

Anything else is treated as handled: `using` / `await using` (statement or declaration), an assignment
to a field or property, a `return`, an argument to another call, or a later reference of any kind. The
analyzer does not attempt to prove that the disposal *happens* — only that the value is not thrown away
where it is created. Section 3 is explicit about what that costs.

## 3. What it does not do

The boundary is stated here rather than left to be discovered, because a clean run must not be read as
a stronger claim than it is.

- **No call-graph analysis.** Only calls written directly in the analysed method body are seen. A helper
  that acquires and drops is not reported at its call site; it is reported in the helper if at all.
- **No interprocedural disposal tracking.** A lease returned to a helper, stored in a field, or captured
  by a lambda counts as handled. The analyzer cannot prove the disposal happens elsewhere, and it does
  not guess.
- **The unused-local half is a reference count, not a control-flow analysis.** A lease disposed on one
  branch of an `if` and leaked on the other counts as disposed. Only a local with *no* other reference
  is reported.
- **`HYO003` follows explicit `Dispose()` / `DisposeAsync()` calls only.** The end of a `using` scope is
  not tracked — a variable cannot outlive its own `using` scope within one body, so there is nothing to
  follow there anyway.
- **`HYO004` is advisory and deliberately `Info`.** A synchronous acquire that is released before the
  first `await` is legitimate, and the rule cannot see whether the borrow crosses an `await`.
- **A clean run is not a claim that the program is free of pool misuse.** It is a claim that none of the
  six shapes above is written in the analysed method bodies. The pool's own run-time guards
  (`ValidateOnReturn`, the active-object ceiling, leak detection, the abandoned-object recovery) remain
  the enforcement; this package only moves *some* of the feedback earlier.

## 4. Host baseline

`netstandard2.0` + `Microsoft.CodeAnalysis.CSharp` **3.8.0**, `PrivateAssets="all"`,
`EnforceExtendedAnalyzerRules=true`. An analyzer must be a `netstandard2.0` assembly because that is
what the compiler host loads it as.

3.8 is the oldest Roslyn the current host line-up shares — VS2019 16.8+, .NET SDK 5.0.100+, Unity
2021.3+, Rider, VS Code — and it matches this repository's TFM-breadth discipline (the core ships
`net48` alongside `net6.0`…`net10.0`). Choosing the oldest workable Roslyn is the same decision as
choosing the oldest workable TFM, and it is why the analyzer will load in an environment that never
builds the core for `net10.0`.

The cost is concrete and is paid in scope: `AnalyzerConfigOptions.Keys` does not exist at 3.8, so the
one rule that would have read `.editorconfig` / MSBuild configuration (`HYO008`, unknown configuration
key) is out of scope for 3.1 (section 9). Nothing else in the six rules needs an API newer than 3.8.

## 5. Dangerous inputs

The plan requires four defences because "an analyzer runs inside the user's build, and anything that
runs away becomes *your package broke my CI*". Three of the four apply here, and the fourth does not —
which this page states rather than pretending otherwise.

- **No regular expressions, and no timeout to set.** None of the six rules matches text. Two of the
  defences the plan names (a regex timeout and a compiled-regex cache) are therefore **inapplicable**,
  not merely absent: there is no pattern to time out and none to cache. If a later rule needs one — the
  configuration-key rule would — it must carry both.
- **No I/O.** Nothing reads a file, an environment variable, the clock, or `AdditionalFiles`. Every rule
  works from the syntax tree and the semantic model of the compilation the compiler has already built.
  There is no manifest to parse and therefore no BOM/encoding surface.
- **No exception escapes a callback.** An analyzer that throws is reported by the host as `AD0001` — a
  warning — and in a repository whose gate is zero warnings that is a broken build for the consumer.
  Every rule therefore invokes its body through a `try/catch` and reports nothing on an unexpected
  shape rather than failing the user's compilation.
- **Generated code is skipped**, via `ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None)`,
  and `EnableConcurrentExecution` is declared so the rules may run concurrently (they keep no state
  between callbacks).

Symbol tests are null-guarded and based on symbol identity (namespace + type), never on a display
string that a `using` alias could change, and diagnostics are reported in syntax order so a rebuild
produces the same list.

## 6. Which calls the rules recognise

A call is an acquire call when its **containing namespace** starts with `DotNetCore.HayateOP` and its
name is one of the acquire names. The namespace gate is what keeps the rules off a user type that
happens to have an `Acquire` method; it also covers the extension methods
(`HayatePoolScopeExtensions` / `HayatePoolLeaseExtensions` live in `DotNetCore.HayateOP`), which is why
the check is on the method's containing type rather than on the receiver.

| Group | Names | Result type |
| :--- | :--- | :--- |
| Scoped | `AcquireScoped`, `AcquireScoped(TimeSpan)`, `AcquireScopeAsync(…)` | `HayatePoolScope<T>` |
| Leased | `AcquireLeased`, `AcquireLeased(TimeSpan)`, `AcquireLeaseAsync(…)` | `HayatePoolLease<T>` |
| Plain | `Acquire`, `Acquire(TimeSpan)`, `AcquireAsync(…)` | `T` |

`HYO003` keys on the receiver's type implementing `IHayateObjectPool<T>` (or being
`ParameterizedHayatePool<,>` / `HayateSharedPoolRegistry`), so that a `Dispose()` on an unrelated
`IDisposable` followed by an `Acquire` on something else is not confused for the pattern.

## 7. Packaging

The assembly must land at `analyzers/dotnet/cs/<AssemblyName>.dll` inside the package, with
`IncludeBuildOutput=false` so no runtime asset is produced. Consumers reference it with
`PrivateAssets="all"`, which keeps it out of *their* transitive dependencies.

`publish-nuget.yml` packs **every** `src/*/*.csproj`, and CI builds `HayateOP.slnx`, so adding the
project to the solution is the whole of the pipeline change: no workflow edit, no package list to
maintain.

The analyzer must not be added as a `ProjectReference` by the library it analyses. The rules are
exercised by a dedicated test project (section 10); the core, the extensions and the samples are
unaffected, and a consumer that does not reference the package sees no diagnostic at all.

### 7.1 The symbol package is off for this project, and why

Every other `src` project sets `<IncludeSymbols>true</IncludeSymbols>` through `asset/props/package.props`
and publishes a `.snupkg` next to it. This one sets it back to `false`.

A `.snupkg` carries pdbs, nothing else. This package has no `lib/` output at all — the whole of its
content is `analyzers/dotnet/cs` — so the symbol package would have neither assemblies nor
dependencies, and NuGet refuses to create it: **`NU5017`, "cannot create a package that has no
dependencies nor content"**. The message names the *main* package even though the main package packs
fine, which makes it easy to misread as "the analyzer file is missing" and start editing the `None`
item that already works. It is the empty symbol package.

This was confirmed against SDK 10.0.400 with a scratch project: identical properties pack cleanly
until `IncludeSymbols` is added, and then fail with exactly this error. The pdb instead ships inside
the main package, next to the assembly it belongs to, which is where the compiler host looks for it
when it has to report an analyzer failure.

## 8. Rule release tracking

`AnalyzerReleases.Unshipped.md` carries the six rules from the moment they exist, wired in as an
`AdditionalFiles` item. This is the only correct answer to `RS2008`: a new `DiagnosticDescriptor`
without a release-tracking entry is a build warning, and this repository has an ID-collision history
(`O8`), which is exactly the failure the file exists to prevent. Suppressing `RS2008` with `NoWarn`
would hide the next collision instead of preventing it.

`AnalyzerReleases.Shipped.md` is created empty alongside it (the format requires the file to exist) and
receives the six rules at the release that first ships them.

## 9. Out of scope for 3.1

| Item | Why not now |
| :--- | :--- |
| `HYO002` — a double `Release`, or a `Release` to another pool | needs dataflow across a method (which value came from which pool); the precision is not reachable with a syntax-and-symbol rule, and a noisy Error-level rule is worse than none |
| `HYO007` — illegal option combinations (`Lean` + a time-dimension feature, …) | needs builder-chain analysis; the run-time guard already fails fast at `Build()` |
| `HYO008` — an unknown configuration key | needs `AnalyzerConfigOptions.Keys`, absent at the 3.8 floor (section 4) |
| `[HayatePooled]` source generator (`33-O-2` / `G-15`) | a different mechanism, deferred to 3.2+ by A2 |
| a sample project | `samples/` is organised by feature and every sample must build, but the one diagnostic worth showing is `HYO009` — an Error by design. A sample could only show the Warning and Info rules, which is the half that matters least; the rule table in the README and the test suite cover the whole surface |

## 10. Acceptance anchors

1. Every rule has a positive case that asserts the **id, severity and location**, and at least two
   negative cases that differ only in the property the rule keys on (discarded vs `using`; `Acquire`
   vs `AcquireAsync`; lease vs plain; `OnRelease` vs `OnDestroy`).
2. The severity asserted for each rule is read from the shipped `DiagnosticDescriptor`, so a later
   severity edit cannot pass unnoticed — in particular `HYO009` must be `Error`.
3. A type outside the `DotNetCore.HayateOP` namespace with an identically named `Acquire` method does
   not fire (the namespace gate).
4. A compilation that references no HayateOP type produces no diagnostic from any rule (the package is
   inert for a project that does not use the library).
5. Every CodeFix round-trips: the fixed source is asserted verbatim, it compiles, and re-analysing it
   reports nothing.
6. Each rule is exercised through a mutation of its own key condition, and the mutation must be caught
   by exactly the cases that are supposed to catch it.

### 10.1 What the anchors do not cover

Stated so that nobody reads anchor 5 as more than it is. The CodeFix tests call `Rewrite` directly and
then assert the text, that it compiles, and that re-analysing reports nothing. `EveryFix_DeclaresOnlyThe
IdItFixes` covers the advertised ids. Nothing in the suite drives the IDE's own path — diagnostic
produced, lightbulb shown, action applied — because reaching it needs a workspace host the test
project does not have, so the wiring in `RegisterCodeFixesAsync` rests on the id assertions plus review
rather than on a test. That gap is the reason the fixers were written to one rule each with a single
equivalence key, which keeps the untested part as small as it can be.

The mutation arms behind anchor 6 are nine, and one of them is worth naming because it constrains how
the fixers may be tested in future: closing the namespace gate entirely (arm C) fails the ten rule
positives and **not** the three CodeFix tests, since those never consult the gate. An end-to-end fix
test would change that, and would then have to be added to arm C's expectation as well.

## 11. Revision history

- **2026-10-13** — designed, before building. Decisions taken with the maintainer: the six-rule scope
  above (the nine-rule draft minus the three that need dataflow or a newer Roslyn), the `netstandard2.0`
  + Roslyn 3.8 floor, and the inclusion of CodeFix. The plan's `HYO009`-at-Error requirement (A2) is
  honoured; `HYO002`, `HYO007` and `HYO008` are deferred rather than dropped, and the reasons are in
  section 9.
- **2026-10-13** — built and verified. Three findings during the build are kept here because each one
  is a trap that produced a green result: the four fixtures for `HYO006` never implemented the full
  `IHayateObjectPolicy<T>` (`CS0535`), so the rule was being decided on an error-recovered tree while
  the tests still passed — fixed by supplying `OnDestroy` unless the fixture brings its own, and by
  asserting in `TestCompiler` that every fixture compiles; the fixtures used file-scoped namespaces,
  which Roslyn 3.8 cannot parse, for the same reason; and `GC.SuppressFinalize(item)` as an *argument*
  was missing from `HYO006`, which only matched receivers.
