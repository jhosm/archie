# Tutorial: Author your first family schema

In this tutorial we stand up a brand-new **product family** in the engine and
watch its pure event handlers fold a lifecycle to exact state — all on our own
machine, no Docker, no registry. A *family* is the engine's name for one product
domain it event-sources: its events, the pure folds that turn those events into
state, the lifecycle legality table, and its projections.

We will not build a family from a blank page — that is a lot of moving parts to
get right at once. Instead we do what every family author actually does: we
**study the one real family in the tree, `term_deposit`, and copy its shape**.
By the end we will have run a focused fold test and seen it go green:

```sh
mise exec -- dotnet test families/term-deposit/tests/Babelstone.Families.TermDeposit.Tests/ --nologo -v q
```

That green run — pure folds replaying a lifecycle to byte-identical state — is
our destination. It is the smallest loop that proves a family's core is sound.

This is a learning path: one route, no detours. We will not explain *why* folds
must be pure or *why* the lifecycle legality lives in one table — those live in
the explanation page and the engine ADRs we link to at the end.

> **Who this is for.** You are a developer defining a new product family — its
> event records, pure fold handlers, the `IFamilyModule` binding, the lifecycle
> table, and projections. You write the engine-side C#; you are not the config
> author who writes pack YAML (that reader has [their own
> set](./author-your-first-pack.md)).

---

## Before we start

Work from the repository root for every command:

```sh
cd babelstone
```

Install the pinned toolchain once (this brings in the pinned .NET 10 the engine
builds against; a system `dotnet` triggers Roslyn analyser version-mismatch
errors):

```sh
make bootstrap
make doctor   # confirms the pinned versions are active
```

Always prefix `dotnet` with `mise exec --` so the pinned SDK is used. That is
the only setup. We are ready.

---

## The shape of a family, in one breath

A family is a self-contained .NET layer that contributes four things the engine
dispatches and folds. Before we touch code, hold this map — the reference family
[`term_deposit`](../../../families/term-deposit/) has one file per row, and we
will read each in turn:

| Piece | File in `term_deposit` | What it is |
|---|---|---|
| **Events** | [`Events.cs`](../../../families/term-deposit/src/Babelstone.Families.TermDeposit/Events.cs) | `record <Entity><PastParticipleVerb> : DomainEvent` — past facts, computed, no PII |
| **Folded state** | [`DepositPosition.cs`](../../../families/term-deposit/src/Babelstone.Families.TermDeposit/DepositPosition.cs) | the aggregate's state record + an `Empty` seed + a lifecycle enum |
| **Folds (handlers)** | [`Handlers.cs`](../../../families/term-deposit/src/Babelstone.Families.TermDeposit/Handlers.cs) | one pure `(state, event) → state` per event |
| **Lifecycle table** | [`LifecycleTransitions.cs`](../../../families/term-deposit/src/Babelstone.Families.TermDeposit/LifecycleTransitions.cs) | the one legality table: which states a transition may fire from |
| **Module** | [`TermDepositFamilyModule.cs`](../../../families/term-deposit/src/Babelstone.Families.TermDeposit/TermDepositFamilyModule.cs) | binds each `event_type` string → its fold |
| **Projections** | [`TermDepositProjectionModule.cs`](../../../families/term-deposit/src/Babelstone.Families.TermDeposit/TermDepositProjectionModule.cs) | the family's read-side folds |

The full, ordered scaffolding procedure — every project, reference arrow, and
host-wiring step — is the **[`new-family-schema`
skill](../../../plugins/babelstone-engine/skills/new-family-schema/SKILL.md)**.
This tutorial is the *lived narrative* that pairs with it: we walk the reference
family so the skill's ten steps land on something you have already seen work.

---

## Step 1 — Read one event, one fold, side by side

Open [`Events.cs`](../../../families/term-deposit/src/Babelstone.Families.TermDeposit/Events.cs)
and find `DepositConstituted` — the event that opens a deposit. It is a `sealed
record` of already-computed facts: a principal as `Money` (integer cents), a
rate in basis points, dates, the interest variant. No name, no NIF, no IBAN —
the events are **structural only**.

Now open its fold in
[`Handlers.cs`](../../../families/term-deposit/src/Babelstone.Families.TermDeposit/Handlers.cs).
Every handler is one pure `(state, event) → state`, and the simplest ones are a
single `state with { … }`:

```csharp
public sealed class InterestAccruedHandler : IEventHandler<DepositPosition, InterestAccrued>
{
    public HandlerResult<DepositPosition> Apply(DepositPosition state, InterestAccrued @event)
        => HandlerResult<DepositPosition>.From(state with
        {
            AccruedGrossInterest = state.AccruedGrossInterest + @event.GrossInterest,
        });
}
```

Three things to notice, because they are the whole discipline:

- It **accumulates** (`state.X + event.Y`), not overwrites — so replaying the
  same event type many times stays correct.
- It does **no arithmetic that could round**: the money sum uses `Money`'s own
  checked `+`. The financial math ran *before* the event existed, on the command
  side; the fold only records the computed fact.
- It reads **no clock, no database, no randomness**. This is enforced by a build
  analyser, which we will meet in Step 4.

That is the core idea of the engine in one method: **state is a left-fold of
past events, and the fold is pure.**

---

## Step 2 — See the folded state and its seed

Open [`DepositPosition.cs`](../../../families/term-deposit/src/Babelstone.Families.TermDeposit/DepositPosition.cs).
This is the `sealed record` the folds build up. Two parts make it a valid fold
target:

- A `public static DepositPosition Empty { get; }` — the seed every fold starts
  from before any event. All money fields seed to `Money.Zero`, the lifecycle to
  `Pending`.
- A `DepositLifecycle` enum: `Pending` (the seed), `Active` (the live state),
  and the terminal states (`Matured`, `Failed`, `Renewed`, …).

The fold only ever *labels* the lifecycle — `DepositConstituted`'s handler sets
`Lifecycle = DepositLifecycle.Active`. Which transitions are *legal* is a
separate concern, and a separate file, which we read in Step 3.

> **A subtlety worth seeing once.** `DepositPosition` overrides `Equals` and
> `GetHashCode` by hand. That is not noise: the record holds one collection field
> (`PrincipalTimeline`), and the compiler's default record equality compares a
> list by *reference*, which would make two independently-folded-but-identical
> states unequal and break the byte-identical replay guarantee. A family whose
> state holds a collection must compare it element-wise. You will not hit this in
> a scalar-only family — but when you do, this is where the pattern is.

---

## Step 3 — Read the lifecycle legality table

Open [`LifecycleTransitions.cs`](../../../families/term-deposit/src/Babelstone.Families.TermDeposit/LifecycleTransitions.cs).
This is the **one explicit, auditable legality table** for the aggregate. It is
*not* a fold and *not* an `IEventHandler` — it is pure data plus a pure
predicate:

```csharp
public static bool IsLegal(DepositLifecycle current, Transition transition) =>
    LegalSources.TryGetValue(transition, out var sources) && sources.Contains(current);
```

`LegalSources` maps each transition to the set of lifecycle states it may fire
*from*. Opening fires only from `Pending`; every operating transition (accrue,
mature, terminate-early, …) fires only from `Active`. The decider consults this
table **before appending** an event and rejects an illegal command — the folds
themselves stay guard-free.

The single most elegant thing here, and worth carrying into your own family:
**terminality is absence, not a flag.** A state is terminal because no business
transition lists it as a legal source — there is no separate `IsTerminal`
boolean to keep in sync. One table, one source of truth. The dedicated
explanation page, [The family lifecycle state
machine](../explanation/the-family-lifecycle-state-machine.md), unpacks why this
shape is the right one; for now, just see that it exists and is small.

---

## Step 4 — Watch the purity gate refuse an impure fold

The "no clock, no I/O, no randomness" rule is not a convention you must
remember — it is a **build-time analyser** that fails compilation. Let us prove
it, then undo it.

Open [`Handlers.cs`](../../../families/term-deposit/src/Babelstone.Families.TermDeposit/Handlers.cs)
and, inside any handler body, add a clock read:

```csharp
public HandlerResult<DepositPosition> Apply(DepositPosition state, InterestAccrued @event)
{
    var now = DateTimeOffset.UtcNow;   // <- deliberately impure
    return HandlerResult<DepositPosition>.From(state with
    {
        AccruedGrossInterest = state.AccruedGrossInterest + @event.GrossInterest,
    });
}
```

Build the pure project:

```sh
mise exec -- dotnet build families/term-deposit/src/Babelstone.Families.TermDeposit/Babelstone.Families.TermDeposit.csproj --nologo -v q
```

The build **fails** with `BENG001`:

```
error BENG001: 'DateTimeOffset.UtcNow' reads the clock inside an event handler —
handlers are pure (state, event) → state; inject time as an event field or
runtime input (ADR-PC-010 §P5)
```

(`BENG002` is the same gate for I/O; `BENG003` for randomness. They are warnings,
but the engine builds warnings-as-errors, so a violation stops the build.)

**Now undo the edit** — remove the `DateTimeOffset.UtcNow` line and rebuild to
confirm green. You have just watched the structural guarantee enforce itself: in
this codebase, an impure fold cannot be committed, because it cannot compile.

---

## Step 5 — Run the fold test to green

The reference family's pure tests fold a realistic lifecycle and assert the
state is exact. Run them:

```sh
mise exec -- dotnet test families/term-deposit/tests/Babelstone.Families.TermDeposit.Tests/ --nologo -v q
```

These are pure unit tests — no Docker, no Postgres. They cover the folds (a
constitute → accrue → mature sequence folds to the expected `DepositPosition`),
the projection folds, and the legality table (every terminal state is closed to
every business transition). A green run here is the proof that a family's core —
events, folds, lifecycle — is internally consistent.

Open [`TermDepositProjectionTests.cs`](../../../families/term-deposit/tests/Babelstone.Families.TermDeposit.Tests/TermDepositProjectionTests.cs)
and [`LifecycleTransitionsTests.cs`](../../../families/term-deposit/tests/Babelstone.Families.TermDeposit.Tests/LifecycleTransitionsTests.cs)
to see the `Fold(seed, registry, event)` and `IsLegal(state, transition)`
patterns — these are exactly the two test shapes your own family copies.

If the run ends green, we are done.

---

## You did it

We read the four pieces of a real family, watched the purity analyser refuse an
impure fold, and ran the pure fold tests to green. That is the full *inner* loop
a family author works in: events define what happened, folds turn them into
state, the lifecycle table says which transitions are legal, and the analyser +
fold tests keep the whole thing deterministic.

What we deliberately did **not** do yet:

- **Scaffold a from-scratch family end to end.** That is the ten-step
  [`new-family-schema` skill](../../../plugins/babelstone-engine/skills/new-family-schema/SKILL.md)
  — projects, reference arrows, the CUE schema, host wiring. This tutorial gave
  you the lived feel of each piece so the skill's steps land.
- **Run the integration tests.** The command → decider → append → rehydrate
  happy-path tests need Docker (Testcontainers Postgres) and live in the
  family's `Application.Tests` project. The pure fold loop above needs neither.
- **Author the per-event Avro and EventCatalog entries.** Each event needs a
  governed `.avsc` and a catalogue entry; adding one is the
  [`new-event` skill](../../../plugins/babelstone-engine/skills/new-event/SKILL.md)'s
  job, kept separate so the four artefacts move in lock-step.

### Where to go next

- [The family lifecycle state machine](../explanation/the-family-lifecycle-state-machine.md)
  — why the legality table is the single source of truth, and why terminality is
  absence.
- [Write and test pure event handlers (folds)](../how-to/write-and-test-event-handlers.md)
  — the task-focused recipe for Step 1 and Step 4, including the analyser gates.
- [Structure event payloads](../how-to/structure-event-payloads.md) — the rules
  for what may ride on an event (no PII, computed facts, `Money` as cents).
- [Author the family CUE schema](../how-to/author-the-family-cue-schema.md) — the
  variant contract your family schema pairs with, without learning CUE.
</content>
</invoke>
