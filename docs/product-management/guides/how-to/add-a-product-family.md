# How-to: Add a product family

**Goal.** Onboard a new product [family](../../reference/glossary.md#family) (the way `term_deposit` is onboarded) so the engine hosts its events, [folds](../../reference/glossary.md#fold), and command-side [decider](../../reference/glossary.md#decider) — with **zero generic-engine diff**.

**Audience.** An engine-team developer who understands the one-engine-many-families thesis ([01-product-architecture](../../product_concepts/01-product-architecture.md)) and the event-sourcing spine. The whole point of the design is that you touch *only* your family's subtree; if that surprises you, read [ADR-PC-021](../../product_concepts/adrs/ADR-PC-021-application-layer-family-owned-deciders.md) first.

---

## Before you start — the one invariant you build inside

The dependency arrow is **family → engine, never the reverse**. The generic engine spine carries no reference to any `families/**` project; adding a family is additive ([ADR-PC-021 §D2/§P2](../../product_concepts/adrs/ADR-PC-021-application-layer-family-owned-deciders.md)). This is mechanically gated by the `ENGINE_FAMILY_AGNOSTIC` fitness function (`EngineFamilyAgnosticTests`), so a stray reference from the spine into your family will fail CI. The family ownership and extraction-ready-subtree rules are [ADR-PC-019](../../product_concepts/adrs/ADR-PC-019-repository-strategy-monorepo.md).

The real subtree shape to mirror is the v1 family, `families/term-deposit/` (see [the `/families` README](../../../../families/README.md)):

```
families/<your-family>/src/
  Babelstone.Families.<X>/              pure folds + events + projection
      refs: Babelstone.Engine, Babelstone.FinancialTypes   (cannot reach a DB or the kernel)
  Babelstone.Families.<X>.Application/  the decider (commands → events)
      refs: Babelstone.Engine, Babelstone.FinancialMath, Babelstone.FinancialTypes,
            Babelstone.RateSheets, Babelstone.Packs, Babelstone.Families.<X>
```

That two-project split — pure fold project vs impure `.Application` decider project — is [ADR-PC-021 §P1/§D3](../../product_concepts/adrs/ADR-PC-021-application-layer-family-owned-deciders.md), and it is *load-bearing*: the fold project is structurally DB-unreachable so a fold stays pure, while the decider is the one place command-side I/O is orchestrated.

---

## Steps

### 1. Declare the events

Define the family's domain events as records in `Babelstone.Families.<X>` — each carrying already-**computed** facts ([money](../../reference/glossary.md#money-cents) as integer cents, [ADR-PC-010 §P1](../../product_concepts/adrs/ADR-PC-010-dotnet-hand-rolled-engine.md)) and **structural only**: no depositor PII, in cleartext or ciphertext, ever rides an event ([ADR-PC-004](../../product_concepts/adrs/ADR-PC-004-pii-crypto-shredding.md)). The term-deposit set in `families/term-deposit/src/Babelstone.Families.TermDeposit/Events.cs` is the worked example; the generated [event catalog](../../reference/events/README.md) is what these events look like once published.

### 2. Write the pure folds

One `IEventHandler<TState,TEvent>` per event — a [fold](../../reference/glossary.md#fold) with no clock, no I/O, no randomness (the BENG001/002/003 analyzers enforce it, [ADR-PC-010](../../product_concepts/adrs/ADR-PC-010-dotnet-hand-rolled-engine.md)). The folds build the family's [projection](../../reference/glossary.md#projection) (`DepositPosition` is term-deposit's). See `Handlers.cs` beside the events.

### 3. Bind events to handlers in the family module

Implement `IFamilyModule` with a public parameterless constructor (so `FamilyModuleLoader` discovers it) — `FamilyName`, `SchemaVersion`, and the `HandlerRegistration` list mapping each `"<family>.<Event>"` type to its handler. The real example is `TermDepositFamilyModule.cs`; `FamilyName` and `SchemaVersion` must match the CUE family schema (next step).

### 4. Write the decider in the `.Application` project

The [decider](../../reference/glossary.md#decider) turns a command into events — running the financial-math kernel, resolving the [rate sheet](../../reference/glossary.md#rate-sheet) and [pack](../../reference/glossary.md#pack-regulatory-pack) primitives, and appending through `AggregateRuntime<TState>`. It depends on **generic engine ports** (`IRateSheetStore`, `ISettlementPort`, a resolved `VerifiedPack`), never on engine internals naming your family ([ADR-PC-021 §D2/§P3](../../product_concepts/adrs/ADR-PC-021-application-layer-family-owned-deciders.md)). `TermDepositDecider.cs` shows the pure decision core kept separate from the impure orchestration (`TermDepositConstitutionService`) — write yours as separable resolve→stamp→settle→append steps for the same reason. The decider is deliberately *outside* the fold-purity analyzers and is reviewed for financial-math correctness and replay-determinism instead.

### 5. Author the CUE family schema

A product-family [variant](../../reference/glossary.md#variant) is YAML validated against a closed CUE schema with no DSL escape hatch ([ADR-PC-006](../../product_concepts/adrs/ADR-PC-006-cue-schema-language.md)). Add your schema under `contracts/cue/families/` (beside `term-deposit.cue`); its declared event taxonomy must match the module's `Handlers` list. The generated [family-schemas reference](../../reference/family-schemas/README.md) is produced from that directory.

### 6. Compose the family into the host

Contribute an `IFamilyHostModule` (the host-side composition seam, [ADR-PC-021 Amendment A1–A3](../../product_concepts/adrs/ADR-PC-021-application-layer-family-owned-deciders.md)): the host references your family, you add one list entry, and the host wires your runtime + decider + endpoints without naming your aggregate type. The host *may* reference families; the spine may not (Amendment A2) — that's the one intended exemption to the no-edge rule.

---

## Verify

- **The agnosticism gate stays green.** `EngineFamilyAgnosticTests` (`Babelstone.Engine.Tests`) parses the spine projects and fails if any references `families/**` — your additive change must not trip it ([ADR-PC-021 Verifiable commitment 1](../../product_concepts/adrs/ADR-PC-021-application-layer-family-owned-deciders.md)).
- **The CUE schema validates.** Family schemas are checked by the contracts pipeline ([ADR-PC-006](../../product_concepts/adrs/ADR-PC-006-cue-schema-language.md)); the repo's `make contracts-check` target runs the CUE fmt + fixture validation the maintainer invokes.
- **The math is unit-testable Docker-free.** The decider's pure compute (command + resolved inputs → events) is testable without a database; the impure resolve+settle+append is integration-tested ([ADR-PC-021 Consequences](../../product_concepts/adrs/ADR-PC-021-application-layer-family-owned-deciders.md)).

## When you're done / related tasks

- The author-facing side — writing the variant YAML against your schema — is [feature-design-configuration-authoring](../../product_concepts/feature-design-configuration-authoring.md).
- How folds and projections sit on the event-sourcing spine is [feature-design-event-store-projections](../../product_concepts/feature-design-event-store-projections.md).
- Back to the [how-to index](./README.md) · [guides root](../README.md).
