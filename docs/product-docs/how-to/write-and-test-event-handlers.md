# How to write and test pure event handlers (folds)

This guide walks you through writing a family's **event handlers** — the pure
folds that turn an event into new state — and testing them. A fold is one
`(state, event) → state` function per event; together they *are* the aggregate's
state, recomputed by replaying its events. Get them right and the engine's whole
determinism guarantee follows; get them wrong and replay diverges.

You will write a fold, satisfy the three purity analysers, and pin it with a
replay test. The worked example throughout is the `term_deposit` family's
[`Handlers.cs`](../../../families/term-deposit/src/Babelstone.Families.TermDeposit/Handlers.cs).

**Before you start, know this:** a fold is the *only* place state is built, and
it runs on **every rebuild** — a cold replay folds an instance's whole history
from scratch. So a fold that reads the clock, or the database, or generates a
random id, produces *different* state depending on when or where it runs. That is
not a style problem; it is a correctness one, and the build refuses it.

---

## The shape: one fold per event, a single `state with { … }`

Each handler implements `IEventHandler<TState, TEvent>` and its body is, ideally,
a single `state with { … }`. The simplest folds accumulate one field:

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

Two habits make a fold correct under replay, and both are visible above:

- **Accumulate, don't overwrite.** Write `state.X + event.Y`, not `event.Y`
  alone. A future variant may emit several flows of the same event type, and an
  accumulating fold stays correct when they all land; an overwriting one silently
  loses all but the last.
- **Label the lifecycle, don't guard it.** A fold that changes lifecycle just
  *labels* it (`Lifecycle = DepositLifecycle.Active`). It does **not** check
  whether the transition was legal — that is the
  [lifecycle table](../explanation/the-family-lifecycle-state-machine.md)'s job,
  checked by the decider *before* the event was ever appended. The fold assumes
  the event happened and accounts for it.

## Record the computed fact; never recompute it

A fold does **no financial math**. When interest accrues, the event already
carries the computed `GrossInterest` as `Money`; the fold records it and stops.
It does not multiply a rate by a day-count — that ran on the command side that
*built* the event. A fold that recomputed would (a) need impure machinery the
analysers forbid and (b) make historical replay depend on the *current* math
code, breaking determinism. The discipline for what the event carries is
[Structure event payloads](./structure-event-payloads.md); the fold's job is just
to fold it in.

---

## Satisfy the three purity analysers

The "pure" in "pure fold" is enforced by build-time analysers, not by trust. A
fold body may not:

| Analyser | Forbids | Why it breaks replay |
|---|---|---|
| **`BENG001`** | reading the clock (`DateTime/DateTimeOffset.Now/UtcNow`, `Stopwatch`, `TimeProvider`, …) | replay at a different wall-clock time produces different state |
| **`BENG002`** | I/O (DB, network, filesystem — `HttpClient`, `File`, a `DbConnection`, …) | a fold coupled to live infrastructure can't be replayed deterministically |
| **`BENG003`** | randomness (`Random`, `Guid.NewGuid()`, `RandomNumberGenerator`) | replay yields different output every time |

These are *warnings*, but the engine's pure project builds **warnings-as-errors**
(via `Directory.Build.props`), so any one of them **fails the build**. Confirm
with:

```sh
mise exec -- dotnet build families/term-deposit/src/Babelstone.Families.TermDeposit/Babelstone.Families.TermDeposit.csproj --nologo -v q
```

A clock read inside a handler produces, for example:

```
error BENG001: 'DateTimeOffset.UtcNow' reads the clock inside an event handler —
handlers are pure (state, event) → state; inject time as an event field or
runtime input (ADR-PC-010 §P5)
```

**How to satisfy them — supply the value, don't generate it:**

- **Need the time?** Carry it as an event field (the event's own date), set on
  the command side. The fold reads `@event.WithdrawnOn`, never `UtcNow`.
- **Need an id?** The runtime mints ids on the write path and carries them into
  the event; the fold sees a fixed value, never `Guid.NewGuid()`.
- **Need a side effect?** A fold cannot do one. Side effects come back as
  scheduled-effect *data* the runtime turns into outbox rows — never an
  `HttpClient` call in the handler.

The structural guarantee here is that the pure project references **only**
`Babelstone.Engine` and `Babelstone.FinancialTypes` — never `EventStore` or the
PII assembly — so a fold *cannot even reach* the database. The analysers and the
reference graph are two layers of the same rule.

> One field is so subtle it has its own analyser: `BENG004` /
> `NO_CLOCK_DRIVEN_ENGINE_SIGNAL` forbids *emitting an engine signal because a
> date arrived* (a clock tick is not a fact about the aggregate). You will meet
> it on the command/decider side, not in a fold — but it is the same
> "time-is-a-value, never a cause" principle
> ([ADR-PC-023](../../product-management/product_concepts/adrs/ADR-PC-023-temporal-signals-projection-derived.md)).

---

## Bind each fold in the family module

A fold does nothing until it is bound to an event type in the family module
([`TermDepositFamilyModule.cs`](../../../families/term-deposit/src/Babelstone.Families.TermDeposit/TermDepositFamilyModule.cs)).
One `HandlerRegistration` per event maps a string `event_type` to its fold:

```csharp
new("term_deposit.InterestAccrued", typeof(InterestAccrued),
    new DispatchableHandler<DepositPosition, InterestAccrued>(new InterestAccruedHandler())),
```

The module also exposes `public static HandlerRegistry Registry()`, which both
the durable runtime *and* the projection runner reuse — so the state materialised
into the read model folds through the **same** handlers the live read path uses.
That single registry is why a projection can never silently disagree with the
live read.

---

## Test it: fold a lifecycle, assert exact state

A fold is tested by folding a realistic event sequence from the seed and
asserting the resulting state is exact. These are **pure unit tests — no Docker,
no Postgres** — and they live in the family's `…Tests` project. The pattern, from
[`TermDepositProjectionTests.cs`](../../../families/term-deposit/tests/Babelstone.Families.TermDeposit.Tests/TermDepositProjectionTests.cs):

```csharp
var registry = TermDepositProjectionModule.AccrualScheduleRegistry();

var schedule = Fold(AccrualSchedule.Empty, registry,
    new InterestAccrued(new Money(30_417), new DateOnly(2027, 1, 15)));

var entry = Assert.Single(schedule.Entries);
Assert.Equal(new Money(30_417), entry.GrossInterest);   // recorded as-is, not recomputed
```

Run the family's pure tests:

```sh
mise exec -- dotnet test families/term-deposit/tests/Babelstone.Families.TermDeposit.Tests/ --nologo -v q
```

Write three kinds of assertion for your folds:

1. **Exact state after a realistic sequence** — fold constitute → operate×N →
   close and assert every field of the resulting state record.
2. **Accumulation under repeats** — fold the same event type twice and assert the
   field summed, proving you accumulated rather than overwrote.
3. **Byte-identical rebuild** — fold the sequence twice and assert the two
   resulting states are equal. This is the determinism contract, and it is why a
   state record holding a collection must compare it *element-wise* (the
   reference family's `DepositPosition` overrides `Equals` for exactly this — a
   reference-equality default would make two identical rebuilds compare unequal).

> **The integration tests are separate.** The full command → decider → append →
> rehydrate happy path needs Docker (Testcontainers Postgres) and lives in the
> family's `Application.Tests` project, tagged `[Trait("Category",
> "Integration")]`. This page is about the *pure* fold loop, which needs neither.

---

## Honest limits

- **Purity is a build gate, not a runtime one.** `BENG001/002/003` are
  Roslyn analysers — they catch the impure *call shapes* they know
  (clock/IO/rng APIs). They are the build-time half of the determinism gate; the
  runtime half is the fixture-replay test that re-folds a recorded history and
  asserts the same state. Both must pass; neither alone is the whole guarantee.
- **The analysers run on the *pure* project only.** The Application (decider)
  project deliberately attaches no purity analyser — it is *meant* to be impure
  (it reads rate sheets, the clock, the store). Keep folds in the pure project so
  they are actually gated; a "fold" written in the Application project would not
  be checked.

## Related

- [Structure event payloads](./structure-event-payloads.md) — what the events
  your folds consume may carry.
- [The family lifecycle state machine](../explanation/the-family-lifecycle-state-machine.md)
  — the legality your folds rely on (so they stay guard-free).
- [Tutorial: author your first family schema](../tutorials/author-your-first-family-schema.md)
  — folds in the context of a whole family (Steps 1, 4, 5).
- The full scaffolding procedure (folds are its Step 4): the
  [`new-family-schema` skill](../../../plugins/babelstone-engine/skills/new-family-schema/SKILL.md).
- Normative source: [ADR-PC-010 §P5](../../product-management/product_concepts/adrs/ADR-PC-010-dotnet-hand-rolled-engine.md)
  (the hand-rolled engine and its fold-determinism contract).
- [Product-docs home](../README.md).
</content>
