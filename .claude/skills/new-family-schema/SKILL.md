---
name: new-family-schema
description: >-
  Scaffold a brand-new product family for the engine — its event records, pure
  fold handlers, the IFamilyModule that binds event-type→handler, the lifecycle
  state-machine legality table, the IProjectionModule projections, and the
  replay/fold tests — modelled on the real `term_deposit` reference family and
  wired into the host. Use when the user wants to add/create a new family,
  product family, or aggregate schema to the engine.
---

# new-family-schema — scaffold a new product family

You scaffold a **new family** (a product domain the engine event-sources) by copying the
shape of the reference family,
[`term_deposit`](families/term-deposit/src/Babelstone.Families.TermDeposit/) — the one fully
realised family in the tree. A family is a self-contained .NET layer that contributes
event-type→handler bindings the engine dispatches and folds, plus its projections and its
lifecycle legality table. The engine spine **never names a family** — the dependency arrow is
`family → engine`, one-way (the `ENGINE_FAMILY_AGNOSTIC` fitness function,
[`engine/tests/Babelstone.Engine.Tests/EngineFamilyAgnosticTests.cs`](engine/tests/Babelstone.Engine.Tests/EngineFamilyAgnosticTests.cs),
[ADR-PC-021 §D2/§P2](docs/product-management/product_concepts/adrs/ADR-PC-021-application-layer-family-owned-deciders.md)).

> Study the reference family first. Every file this skill scaffolds has a concrete twin under
> `families/term-deposit/` — open the twin, copy its shape, swap the names. Do not invent
> base types or namespaces; the ones below are the real ones.

Throughout, replace `<Family>` (PascalCase, e.g. `SavingsAccount`), `<family>` (snake_case
family name, e.g. `savings_account`), `<domain>` (the bus domain, e.g. `deposits`), and
`<State>` (the aggregate's folded-state record, e.g. `AccountPosition`).

## The base types you build on (all real, all in the tree)

| Type | Where | Role |
|---|---|---|
| `DomainEvent` (abstract record) | [`engine/src/Babelstone.Engine/Handlers.cs`](engine/src/Babelstone.Engine/Handlers.cs) | base of every event record |
| `IEventHandler<TState,TEvent>` → `HandlerResult<TState>` | same file | the pure fold `(state,event)→state` |
| `DispatchableHandler<TState,TEvent>` | same file | adapts a typed fold to the dispatch path |
| `IFamilyModule` + `HandlerRegistration` | [`engine/src/Babelstone.Engine/FamilyModule.cs`](engine/src/Babelstone.Engine/FamilyModule.cs) | exports `FamilyName` / `SchemaVersion` / `Handlers` |
| `IProjectionModule` / `ProjectionRunner<TState>` / `ProjectionMode` | [`engine/src/Babelstone.Engine/Projections.cs`](engine/src/Babelstone.Engine/Projections.cs), [`ProjectionRunner.cs`](engine/src/Babelstone.Engine/ProjectionRunner.cs) | declares the family's projections |
| `Money` (integer cents) | [`engine/src/Babelstone.FinancialTypes/Money.cs`](engine/src/Babelstone.FinancialTypes/Money.cs) | all monetary state |
| `IFamilyHostModule` / `FamilyHostContext` | [`engine/src/Babelstone.Engine.Api/IFamilyHostModule.cs`](engine/src/Babelstone.Engine.Api/IFamilyHostModule.cs) | the family's host composition seam |

The two namespaces a family code-lives in: `Babelstone.Families.<Family>` (the pure folds)
and `Babelstone.Families.<Family>.Application` (the impure decider/command side).

## Step 1 — Lay out the projects (mirror `families/term-deposit/`)

```
families/<family-kebab>/
  src/
    Babelstone.Families.<Family>/                 # PURE: events, folds, module, projections, lifecycle, state
    Babelstone.Families.<Family>.Application/      # IMPURE: commands + decider (the command side)
  tests/
    Babelstone.Families.<Family>.Tests/            # pure unit tests (no Docker)
    Babelstone.Families.<Family>.Application.Tests/ # integration tests (Testcontainers)
```

Copy each `.csproj` from its term-deposit twin and rename `RootNamespace`/`AssemblyName`. The
**reference arrows are load-bearing** — copy them exactly
([`Babelstone.Families.TermDeposit.csproj`](families/term-deposit/src/Babelstone.Families.TermDeposit/Babelstone.Families.TermDeposit.csproj)):

- The **pure project** references **only** `Babelstone.Engine` + `Babelstone.FinancialTypes`,
  and attaches `Babelstone.Engine.Analyzers` as an `OutputItemType="Analyzer"` (the
  `BENG001/002/003` purity analysers — a clock/IO/rng call in a fold fails the build, since
  warnings are errors via `Directory.Build.props`). It must **never** reference `EventStore`
  or `Pii`, so a fold structurally cannot reach the database.
- The **Application project** ([twin](families/term-deposit/src/Babelstone.Families.TermDeposit.Application/Babelstone.Families.TermDeposit.Application.csproj))
  is the impure decider; it adds `Babelstone.FinancialMath`, `Babelstone.RateSheets`,
  `Babelstone.Packs`, and the pure project. It attaches **no** purity analyser by design
  ([ADR-PC-021 §P3](docs/product-management/product_concepts/adrs/ADR-PC-021-application-layer-family-owned-deciders.md)).

## Step 2 — Events (`Events.cs`)

Model on [`Events.cs`](families/term-deposit/src/Babelstone.Families.TermDeposit/Events.cs).
Each event is `public sealed record <Entity><PastParticipleVerb>(…) : DomainEvent` in
`Babelstone.Families.<Family>`. Discipline:

- **Naming is `<Entity><PastParticipleVerb>`** — a past fact, PascalCase
  ([08-event-catalog-governance.md §Naming](docs/product-management/integration_concepts/08-event-catalog-governance.md)).
  Authoring the per-event Avro `.avsc` + catalogue entry for each is the `new-event` skill's job.
- **Computed facts only** (`Money` cents-native, [ADR-PC-010 §P1](docs/product-management/product_concepts/adrs/ADR-PC-010-dotnet-hand-rolled-engine.md));
  the financial-math kernel runs command-side, never in a fold.
- **Structural only — no PII** (no name/NIF/IBAN, cleartext or ciphertext): carry an opaque
  reference the engine resolves internally instead
  ([ADR-PC-004 §P2](docs/product-management/product_concepts/adrs/ADR-PC-004-pii-crypto-shredding.md)).
- The pack/schema/family **pins ride on the [`EventEnvelope`](engine/src/Babelstone.EventStore/EventEnvelope.cs)**,
  not on the event record.

## Step 3 — The folded-state record (`<State>.cs`)

Model on
[`DepositPosition.cs`](families/term-deposit/src/Babelstone.Families.TermDeposit/DepositPosition.cs).
A `sealed record <State>(…)` holding the aggregate's folded state — **all money is `Money`**,
no `decimal` state (`BMNY002`). Add:

- a `public static <State> Empty { get; }` seed (the state a fold starts from before any event), and
- a `DepositLifecycle`-style `<Family>Lifecycle` enum: a `Pending` seed, the live state(s), and
  the terminal states. The fold only *labels* the lifecycle; **legality** is Step 6, not here.

## Step 4 — Handlers: one pure fold per event (`Handlers.cs`)

Model on [`Handlers.cs`](families/term-deposit/src/Babelstone.Families.TermDeposit/Handlers.cs).
One `IEventHandler<<State>, <Event>>` per event; each body is a single `state with { … }`:

```csharp
public sealed class <Event>Handler : IEventHandler<<State>, <Event>>
{
    public HandlerResult<<State>> Apply(<State> state, <Event> @event)
        => HandlerResult<<State>>.From(state with { /* label / accumulate; no clock/IO/rng */ });
}
```

**Accumulate** (`state.X + event.Y`) rather than overwrite, so the fold stays correct under
replay when multiple flows of the same type land. No clock, no I/O, no randomness — the
analysers enforce it.

## Step 5 — The family module (`<Family>FamilyModule.cs`)

Model on
[`TermDepositFamilyModule.cs`](families/term-deposit/src/Babelstone.Families.TermDeposit/TermDepositFamilyModule.cs).
A `sealed class <Family>FamilyModule : IFamilyModule` with a **public parameterless
constructor** (the [`FamilyModuleLoader`](engine/src/Babelstone.Engine/FamilyModule.cs)
discovers it by that ctor and throws a diagnosable error without one). It exports:

- `FamilyName => "<family>";` (snake_case)
- `SchemaVersion => "<family>@YYYY.N";` (matches the CUE family schema, Step 8)
- `Handlers => [ … ]` — one `HandlerRegistration` per event, `event_type` string
  `"<family>.<EventName>"`, each wrapping the fold in a `DispatchableHandler<<State>,<Event>>`.

Expose `public static HandlerRegistry Registry() => new(new <Family>FamilyModule().Handlers);`
(the durable runtime and the projection runner reuse it — so the materialised state is the
*same* fold the live read path computes).

## Step 6 — The lifecycle state machine (`LifecycleTransitions.cs`)

Model on
[`LifecycleTransitions.cs`](families/term-deposit/src/Babelstone.Families.TermDeposit/LifecycleTransitions.cs).
This is the **one explicit, auditable transition-legality table** — pure data + a pure
`IsLegal(current, transition)` predicate (no clock/IO/rng; it is **not** an `IEventHandler`).
The decider consults it **before appending** and rejects an illegal command with
`DomainRejectedException`; the folds stay guard-free label-only writes.

- One `Transition` enum value per event that drives a transition (naming the transition by its
  driving event keeps the table in lock-step with the event taxonomy — adding an event without
  a row here is the only way a new transition can exist; that is the auditability the table buys).
- A `LegalSources` map: each transition → the set of lifecycle states it may fire FROM. The
  seed `Pending` is the only legal source for constitute/reject; live operating/closing
  transitions fire only from the live state. **Terminality is ABSENCE from every source set**,
  not a separate flag — one table, no second place to keep in sync.

## Step 7 — Projections (`<Family>ProjectionModule.cs`)

Model on
[`TermDepositProjectionModule.cs`](families/term-deposit/src/Babelstone.Families.TermDeposit/TermDepositProjectionModule.cs).
A `sealed class <Family>ProjectionModule : IProjectionModule` whose `CreateRunners` returns a
`ProjectionRunner<TState>` per projection, each with a distinct `kind` string
`"<family>.<projection_name>"`, `ProjectionMode.Async` (the v1 default), a dedicated
`HandlerRegistry` of folds, a seed, and a `ProjectionStore<TState>` over `ProjectionInfra`.
Reuse `<Family>FamilyModule.Registry()` for the position projection so it folds identically to
the live read path. A runner **skips** any event type it has no binding for, so each
projection's registry lists only the event types it records. The store shape is unchanged
([ADR-PC-002 §P1](docs/product-management/product_concepts/adrs/ADR-PC-002-application-level-bitemporality.md)) — a schedule/ledger is a state record holding a
collection, not new rows/columns. If the family exposes a denormalized CQRS read model, add a
`CreateReadModelRunner` + a pure `state→row` mapper, exactly as the twin does (D.4, ADR-IC-005).

## Step 8 — The CUE family schema and the pack binding

Add `contracts/cue/families/<family>.cue` modelled on
[`contracts/cue/families/term-deposit.cue`](contracts/cue/families/term-deposit.cue): a
**closed** `#<Family>` definition (a variant carrying an undeclared field fails depth 1 — no
DSL escape hatch, [ADR-PC-006](docs/product-management/product_concepts/adrs/ADR-PC-006-cue-schema-language.md)). Pin `schema` to `<family>@YYYY.N` and
`pack` to the governing `pt.YYYY.N`. Add accept/reject fixtures under
`contracts/cue/testdata/<family>/{valid,invalid}/` and verify:

```bash
mise exec -- make contracts-check   # CUE fmt + accept/reject fixtures (ADR-PC-006)
```

`<family>@YYYY.N` here must equal the module's `SchemaVersion` (Step 5). A new family's own
*regulatory pack* — primitives, parameters, sealed corpus — is the **`pack-author`** skill's
job, not this one.

## Step 9 — Replay / fold tests

Add tests mirroring the reference family's two tiers:

- **Pure fold/replay tests** in `Babelstone.Families.<Family>.Tests` (no Docker) — copy the
  `Fold(seed, registry, event)` pattern from
  [`TermDepositProjectionTests.cs`](families/term-deposit/tests/Babelstone.Families.TermDeposit.Tests/TermDepositProjectionTests.cs)
  and the legality table coverage from
  [`LifecycleTransitionsTests.cs`](families/term-deposit/tests/Babelstone.Families.TermDeposit.Tests/LifecycleTransitionsTests.cs).
  Replay a realistic lifecycle (constitute → operate×N → close) and assert the folded
  `<State>` is exact and byte-identical on rebuild.
- **Integration tests** in `Babelstone.Families.<Family>.Application.Tests` (Testcontainers
  PostgreSQL) — the end-to-end command→decider→append→rehydrate happy path, modelled on
  [`ConstituteAccrueMatureHappyPathTests.cs`](families/term-deposit/tests/Babelstone.Families.TermDeposit.Application.Tests/ConstituteAccrueMatureHappyPathTests.cs)
  and the cold-replay budget in
  [`ColdReplayBudgetTests.cs`](families/term-deposit/tests/Babelstone.Families.TermDeposit.Application.Tests/ColdReplayBudgetTests.cs).
  These need Docker — tag them `[Trait("Category", "Integration")]` so they run in the
  Testcontainers lane, not the default unit lane.

```bash
mise exec -- dotnet build families/<family-kebab>/src/Babelstone.Families.<Family>/Babelstone.Families.<Family>.csproj --nologo -v q
mise exec -- dotnet test  families/<family-kebab>/tests/Babelstone.Families.<Family>.Tests/ --nologo -v q
```

## Step 10 — Wire the family into the host

The engine spine stays family-count-invariant; you register the new family at the **host
edge** ([ADR-PC-021 §D4](docs/product-management/product_concepts/adrs/ADR-PC-021-application-layer-family-owned-deciders.md)). Model on
[`TermDepositHostModule.cs`](engine/src/Babelstone.Engine.Api/TermDepositHostModule.cs):

- Add a `sealed class <Family>HostModule : IFamilyHostModule` that registers the family's
  closed-generic `AggregateRuntime<<State>>` (seeded `() => <State>.Empty`, fed
  `<Family>FamilyModule.Registry()`), its decider, its `IProjectionModule`, and maps its
  endpoints. The family owns the closed generic here, so the host never names `<State>`.
- Add the entry to the host's module list in
  [`Program.cs`](engine/src/Babelstone.Engine.Api/Program.cs)
  (`IReadOnlyList<IFamilyHostModule> familyModules = [ … ]`) and add the two
  `ProjectReference`s (pure + Application) to
  [`Babelstone.Engine.Api.csproj`](engine/src/Babelstone.Engine.Api/Babelstone.Engine.Api.csproj).

## Per-event work hands off to `new-event`

This skill scaffolds the family *shell* and an initial event set. To add **another** event to
the family afterwards — the C# record + fold + binding **and** its governed Avro `.avsc`,
EventCatalog entry, and BACKWARD registry-compat check — use the **`new-event`** skill, which
owns that four-artefacts-in-lock-step procedure.

## Guardrails

- **`family → engine` only** — the pure project references just `Engine` + `FinancialTypes`;
  the spine never references a family (`ENGINE_FAMILY_AGNOSTIC`, [ADR-PC-021 §D2/§P2](docs/product-management/product_concepts/adrs/ADR-PC-021-application-layer-family-owned-deciders.md)).
- **Folds are pure** — single `state with { … }`, no clock/IO/rng; the `BENG001/002/003`
  analysers fail the build otherwise.
- **No PII on events** — references behind the OpenBao seam, never identity on the bus
  ([ADR-PC-004 §P2](docs/product-management/product_concepts/adrs/ADR-PC-004-pii-crypto-shredding.md)).
- **All money is `Money` cents** — no `decimal` state ([ADR-PC-010 §P1](docs/product-management/product_concepts/adrs/ADR-PC-010-dotnet-hand-rolled-engine.md), `BMNY002`).
- **The lifecycle table is the one source of legality** — terminality is absence from every
  source set; the decider rejects, the folds never guard.
- **Module needs a public parameterless ctor** — the `FamilyModuleLoader` throws a
  diagnosable error otherwise.
- **`SchemaVersion` == the CUE `schema` pin** (`<family>@YYYY.N`) — they must agree.
