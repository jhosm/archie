# ADR-PC-021: Application Layer — Family-Owned Deciders over a Family-Agnostic Engine

| Field | Value |
|---|---|
| Status | Accepted |
| Date | 2026-05-30 |
| Deciders | jhosm |
| Shape | Tool-selection ([ADR-PC-000 §D3](./ADR-PC-000-namespace-and-contract-shape-framework.md) residual category — a structural/engineering-practice decision, declared tool-selection per the [§D4](./ADR-PC-000-namespace-and-contract-shape-framework.md) default; F1/F2 do not discriminate, the same class as [ADR-PC-019](./ADR-PC-019-repository-strategy-monorepo.md)) |
| Common criteria | [ADR-IC-000](../../integration_concepts/adrs/ADR-IC-000-common-evaluation-criteria.md) (reused per [ADR-PC-000](./ADR-PC-000-namespace-and-contract-shape-framework.md) D2) |
| Depends on | [ADR-PC-010](./ADR-PC-010-dotnet-hand-rolled-engine.md) (the hand-rolled, single-deployable engine spine this layer sits above), [ADR-PC-019](./ADR-PC-019-repository-strategy-monorepo.md) (the `/families` subtree + `CODEOWNERS` ownership this places the decider in), [ADR-PC-008](./ADR-PC-008-rate-sheet-storage-and-deploy-api.md) (rate resolution at constitution — the decider's §P3 stamp; §S2 in-transaction deferred), [ADR-PC-016](./ADR-PC-016-legacy-current-account-adapter.md) (the legacy-settlement contract the decider's `ISettlementPort` seam fronts), [ADR-PC-009](./ADR-PC-009-per-instance-version-pinning.md) (the pinned pack/schema the decider resolves against) |
| Implemented by | bd `babelstone-eyof` (Epic E.3 — the first decider: term-deposit constitute→accrue→mature) |

---

## Context

[ADR-PC-010](./ADR-PC-010-dotnet-hand-rolled-engine.md) fixed the engine spine: a hand-rolled, single-deployable C# event-sourcing core whose generic runtime (`AggregateRuntime<TState>`) folds events into state and commits them with their outbox rows in one transaction. The family layer ([event-store §3](../feature-design-event-store-projections.md), first realised in Epic E.1) plugs into that spine as **pure folds** — `IEventHandler<TState,TEvent>` bodies that are analyzer-enforced free of clock, I/O, and randomness (BENG001/002/003) — discovered at load time by `FamilyModuleLoader`. The data layers around it are likewise in place: signed packs ([ADR-PC-007](./ADR-PC-007-signed-yaml-oci-pack.md)) loaded by the in-engine verifier (C.5), and rate sheets ([ADR-PC-008](./ADR-PC-008-rate-sheet-storage-and-deploy-api.md)) resolved point-in-time at constitution.

Epic E.3 introduces the **first command-side flow** — `constitute → accrue → mature` for a term deposit — and with it the question this ADR answers, which falls out before the first decider is typed:

> **Where does command-side domain logic — the "decider" that turns a command into events, running the financial-math kernel and resolving the rate sheet + pack primitives — live, and how does it relate to the generic engine and the per-family fold modules?**

A decider is not a fold. A fold is pure and reads only `(state, event)`; a decider is **impure by necessity** — it resolves a rate sheet (DB), reads pack primitives, calls the financial-math kernel, invokes the legacy-settlement seam, and appends through the runtime. The two have *opposite* dependency rules, which is the crux of the placement question.

This entry is the [ADR-PC-000 §D3](./ADR-PC-000-namespace-and-contract-shape-framework.md) **residual category** ("operational/engineering discipline … fits neither template cleanly … default to tool-selection"), the same class as [ADR-PC-019](./ADR-PC-019-repository-strategy-monorepo.md) (repository strategy) and [ADR-PC-009](./ADR-PC-009-per-instance-version-pinning.md) (version pinning). The honest consequence, surfaced up front: **F1 and F2 do not discriminate.** No tool is bought; a source-tree boundary carries no PII and is not a DORA/PSD2 runtime artefact. The load-bearing question is not "which tool" but **which placement keeps the engine reusable across families while a 1–2-person, LLM-first team adds them** — settled on S1 + S2 plus the open/closed property the existing fold-plugin model already commits to, not on the hard filters.

**Candidates evaluated** (where the decider lives):

| # | Candidate | Notes |
|---|---|---|
| A | **Per-family `Babelstone.Families.<X>.Application` project** — the decider lives in the family's own subtree, beside (but separate from) its pure-fold project; it depends on generic engine ports. | Engine never names a family. Adding a family = new family projects, zero generic-engine diff. The fold project keeps its narrow, DB-unreachable reference set. |
| B | **A shared `Babelstone.Application` project** referencing every family's commands/events. | One composition project edited on *every* new family — an open/closed violation; the shared project's reference set grows without bound. |
| C | **The decider inside the generic engine** (`Babelstone.Engine`). | The engine would reference family assemblies (events, command shapes), losing family-agnosticism: a new family would diff the spine. |
| D | **The decider inside the pure family project** (`Babelstone.Families.<X>`). | Forces the impure deps (FinancialMath, RateSheets, Packs, the settlement seam) onto the project that holds the pure folds — breaking the structural guarantee that a fold cannot reach a DB or the kernel. |

---

## Evaluation

### Hard filter results

#### F1 · Cost / licensing

| Candidate | Licence / cost | Verdict |
|---|---|---|
| A · per-family `.Application` | No tool, no licence; one extra project per family. | **Pass** |
| B · shared `Application` | Same; one project total. | **Pass** |
| C · in-engine | Same; no new project. | **Pass** |
| D · in pure family | Same; no new project. | **Pass** |

Uniform pass — F1 does not discriminate (no candidate buys anything).

#### F2 · Regulatory fit (GDPR / DORA / PSD2)

Source-tree placement of a decider carries no PII and is not itself a regulated artefact. The regulatory-weight properties the decider *exercises* — stamping the resolved TAN + `rate_sheet_version_id` at constitution ([ADR-PC-008 §P3](./ADR-PC-008-rate-sheet-storage-and-deploy-api.md)) and the gated settlement debit ([ADR-PC-016](./ADR-PC-016-legacy-current-account-adapter.md)) — are owned by *those* ADRs and hold identically under all four placements. It is a correctness property of *how the decider behaves*, not a filter a placement passes or fails.

| Candidate | GDPR | DORA / PSD2 | Verdict |
|---|---|---|---|
| A / B / C / D | No PII in source placement; constitution-flow regulatory properties owned by ADR-PC-008/016, placement-invariant. | Identical under all four. | **Pass** |

All four clear the hard filters. The decision is entirely in S1 + S2 and the open/closed analysis below — the expected shape for the [§D3](./ADR-PC-000-namespace-and-contract-shape-framework.md) residual category.

---

### Soft criteria

#### A · Per-family `.Application` project — **CHOSEN**

**S1 · Operational complexity for 1–2 people.** Lowest *over the life of the build*. Each family is self-contained — its decider, folds, events, and projection live in one subtree under one `CODEOWNERS` entry ([ADR-PC-019 §P2](./ADR-PC-019-repository-strategy-monorepo.md)). Adding a family is an additive operation (drop two projects in) with no edit to shared code, so the change a 1–2-person team makes most often — onboard a product — never touches the spine.

**S2 · Ecosystem coherence — decisive.** The engine already commits to a **family-as-plugin** model: folds are `IFamilyModule` bindings discovered by `FamilyModuleLoader`, and the engine dispatches them generically by event-type/payload-type without naming a family. Placement A extends that exact commitment from the *fold* side to the *command* side: the engine exposes generic ports (`AggregateRuntime<TState>`, `IRateSheetStore`, `ISettlementPort`) plus the resolved pack (`VerifiedPack`); the family-owned decider consumes them. The dependency arrow stays **family → engine**, never the reverse — the same direction the fold plugin already enforces. Any other placement (B/C) bends that arrow back toward the spine and breaks the coherence the loader established.

**S3 · Exit cost.** Low and deferred-friendly. The choreography common to deciders (resolve → stamp → settle → append) is written as small, separable steps inside the family decider, so promoting it to a generic `ConstitutionPipeline` later (when a *second* decider proves the shape — see §P5) is a lift, not a rewrite. Choosing A keeps that extraction option open at near-zero cost.

**S4 · Longevity.** Neutral — the layering outlives any one family.

**Decisive project-specific reason — open/closed for families.** The brief's thesis is one engine, many families ([01 §1](../01-product-architecture.md)). The placement that honours it is the one where the *engine* is closed for modification and *open* for extension by a new family — exactly A. B and C make the generic layer change every time a family is added; A makes it change never.

#### B · Shared `Babelstone.Application` — **rejected**

A single composition project that references every family is edited on every new family — the open/closed violation the whole plugin model exists to avoid, and the smell that motivated this ADR. Its reference set grows without bound, and a context-bounded LLM author must hold the whole cross-family project to add one family. Rejected on S1 + S2 with no offsetting gain.

#### C · Decider in the generic engine — **rejected**

Putting the decider in `Babelstone.Engine` forces the spine to reference family assemblies (command shapes, event records) — so a new family diffs the engine, the precise property A preserves. It also inverts the loader's `family → engine` arrow. Rejected on S2 (loses family-agnosticism).

#### D · Decider in the pure family project — **rejected**

The pure-fold project references only `Babelstone.Engine` + `Babelstone.FinancialTypes` so that a fold *structurally cannot* reach a DB or the kernel (the analyzer-backed purity guarantee). Putting the decider there drags FinancialMath, RateSheets, Packs, and the settlement seam onto that project, dissolving the guarantee. Rejected on S2 (collapses the pure/impure separation).

**Decisive reason for A over B/C/D:** the engine is the reusable asset, the team is 1–2 people adding families over time, and the fold layer already commits to a `family → engine` plugin arrow — all three point to a family-owned decider that leaves the spine untouched. B/C bend the arrow back; D collapses the purity boundary.

---

## Decision

### The decider is a per-family application project; the engine stays family-agnostic.

- **D1 — Family-owned deciders.** Command-side domain logic lives in per-family `Babelstone.Families.<X>.Application` projects. There is **no** shared cross-family application project, and deciders are **not** in the generic engine.
- **D2 — Family-agnostic engine.** Deciders depend on generic engine ports — `AggregateRuntime<TState>`, `IRateSheetStore`, the new `ISettlementPort` — and a resolved `VerifiedPack` (the pinned pack, loaded host-side via `IPackStore`; per-instance pinning per [ADR-PC-009](./ADR-PC-009-per-instance-version-pinning.md)). The dependency arrow is **family → engine**, never engine → family. Adding a family is **zero generic-engine diff** — the `FamilyModuleLoader` plugin model, extended from folds to deciders.
- **D3 — Pure/impure split inside the family.** The pure folds (`event → state`, no I/O, structurally DB-unreachable — the existing narrow `Babelstone.Families.<X>` project) and the impure decider (`command → events`, orchestrates I/O — the new `.Application` project) are **separate projects** in the same family subtree. The fold project keeps its analyzer-backed purity guarantee; the decider is the I/O-orchestrating layer and is reviewed as such (it is *outside* the handler-purity analyzers by design).
- **D4 — Composition at the edge.** Which families a deployment runs, and the wiring of the runtime + ports, is the **host/composition-root's** job (the engine process, or — until it exists — an integration test), via discovery; generic code never references a family to compose it.
- **D5 — Scope: the layering, not a one-example pipeline.** This ADR commits to D1–D4. It deliberately does **not** freeze a generic command-pipeline shape on a single decider. Three refinements are deferred: the generic `ConstitutionPipeline` extraction to the second decider (rule-of-three; bd `babelstone-osv6`); the [ADR-PC-008 §S2](./ADR-PC-008-rate-sheet-storage-and-deploy-api.md) in-transaction resolve+append (bd `babelstone-3k10`); and the external HTTP boundary, deferred to Epic E.5 (the decider is in-process for E.3, driven by the integration test as composition root — **realized in E.5**, see the 2026-05-30 revision below).

**Rejected: a shared `Babelstone.Application`** — an open/closed violation edited on every family. **Rejected: the decider in the engine** — the spine would name families and diff per family. **Rejected: the decider in the pure family project** — dissolves the fold-purity guarantee by dragging impure deps onto it.

*Revised 2026-07-02: the family-agnostic-core + composition-by-discovery covenant this Decision instantiates for the engine estate is now also stated once, cross-cuttingly, in [ADR-PC-040](./ADR-PC-040-family-agnostic-substrate-covenant.md), which adds two default-deny universal backstops behind this ADR's estate gates — `FAMILY_TO_CORE_DEFAULT_DENY` (an unmarked `.csproj` is a gated core by default; the §A2 host exemption becomes the explicit `<BabelstoneRole>CompositionRoot</BabelstoneRole>` marker on `Babelstone.Engine.Api`) and `COMPOSITION_ROOT_NAMES_NO_FAMILY` (the §A17–§A18 host source gate generalised to every marked root) — and relocates the `HostModuleLoader` scan/activation mechanics onto the shared `FamilyModuleScanner` (`Babelstone.Composition`; initially landed in `Babelstone.Cadence`, relocated 2026-07-03), the loader's public contract and the pack-manifest fail-closed cross-check unchanged. §D1–§D5 and every amendment remain binding as written; a new estate inherits the covenant from ADR-PC-040 instead of re-deriving it from this ADR's precedent.*

---

## Implementation Principles

### P1 — Project topology

```
families/term-deposit/src/
  Babelstone.Families.TermDeposit/              pure folds + events + projection
      refs: Babelstone.Engine, Babelstone.FinancialTypes        (cannot reach a DB or the kernel)
  Babelstone.Families.TermDeposit.Application/  the decider (commands → events)
      refs: Babelstone.Engine, Babelstone.FinancialMath, Babelstone.FinancialTypes,
            Babelstone.RateSheets, Babelstone.Packs, Babelstone.Families.TermDeposit
engine/src/Babelstone.Engine/
  + ISettlementPort                             the one new generic, family-agnostic port
```

The decider runs the financial-math kernel (`Accrual` / `Withholding` / `DayCount`) command-side, resolves the rate sheet ([ADR-PC-008](./ADR-PC-008-rate-sheet-storage-and-deploy-api.md)) and pack primitives ([ADR-PC-007](./ADR-PC-007-signed-yaml-oci-pack.md)), stamps the resolved TAN + `rate_sheet_version_id` onto `DepositConstituted`, and appends through `AggregateRuntime<DepositPosition>`.

### P2 — The engine→family edge is forbidden

No project in the generic engine spine (`Babelstone.Engine`, `Babelstone.EventStore`, `Babelstone.RateSheets`, `Babelstone.Packs`, `Babelstone.FinancialMath`, `Babelstone.FinancialTypes`, `Babelstone.Engine.Avro`, `Babelstone.OutboxPublisher`) may carry a `ProjectReference` to a `families/**` project. The arrow is one-way; this is the gateable invariant (see Verifiable commitments). The Avro codec realises this for *serialization* the same way the decider does for *commands*: it binds any family's event to its `.avsc` by convention, so it names no family (the per-family serializer would be the same coupling this rule forbids).

### P3 — Folds stay pure; deciders are the impure layer

Pure folds remain in the narrow family project under the BENG001/002/003 analyzers. The decider is *expected* to do I/O and is deliberately **not** under those analyzers — it is the single place a family's command-side I/O is orchestrated, and is reviewed for it (silent-failure, financial-math, replay-determinism) rather than analyzer-gated for purity.

### P4 — Composition is discovery at the host/test edge

A deployment composes the families it runs at the host edge (the engine process, or an integration test), mirroring `FamilyModuleLoader`'s assembly-scan discovery of fold modules. Generic engine code never references a family to wire it.

### P5 — Commit to the layering, defer the pipeline

The choreography common to deciders is written as separable steps so the generic `ConstitutionPipeline` extraction (bd `babelstone-osv6`) is a later lift, taken on the **second** decider — not pre-built on the first (the rule-of-three discipline [ADR-PC-009 §P5](./ADR-PC-009-per-instance-version-pinning.md) applies to abstractions, not just config). The §S2 in-transaction wiring (bd `babelstone-3k10`) and the HTTP boundary (E.5) are the other two deferrals.

---

## Consequences

**What this choice makes easier:**

1. **Open/closed for families.** Adding a product family is additive — new family projects, zero generic-engine diff. The change a small team makes most often never touches the spine.
2. **A reusable engine.** The spine stays a generic event-sourcing + ports substrate that a second engine deployment (or a second jurisdiction's families) reuses unchanged.
3. **Testable in two tiers.** The decider's *pure* compute (command + resolved inputs → events) is unit-testable Docker-free (CI default lane); the *impure* orchestration (resolve + settle + append) is integration-tested against Testcontainers — so the family-specific math gets fast CI coverage independent of the DB lane.
4. **Clear ownership.** The decider sits under the family's `CODEOWNERS` entry ([ADR-PC-019](./ADR-PC-019-repository-strategy-monorepo.md)), beside the folds it complements.

**What this choice makes harder or impossible:**

1. **A per-family `.Application` project is boilerplate until the pipeline is extracted.** Until bd `babelstone-osv6`, each family's decider repeats the resolve→stamp→settle→append choreography. Mitigation: write it as separable steps (§P5) so the second decider triggers a lift, not a rewrite; accept the duplication as cheaper than a one-example abstraction.
2. **The decider is outside the handler-purity analyzers.** It *can* reach a DB and the kernel by design, so the BENG purity net does not cover it. Mitigation: it is reviewed by the financial-math, silent-failure, and replay-determinism lenses instead of analyzer-gated (§P3).

**Residual risks:**

- **Premature-abstraction risk on the pipeline.** Generalising the choreography from one AT_MATURITY example could freeze assumptions that PERIODIC/ADVANCE lifecycles (Epic F) break. Mitigation: the deferral in D5/§P5 — extract on the second decider, with evidence, not on the first.
- **The engine→family no-edge rule is a convention until gated.** A stray `ProjectReference` from the spine to a family would silently erode family-agnosticism. Mitigation: the `ENGINE_FAMILY_AGNOSTIC` fitness function below makes it a mechanical check (Live).

---

## Verifiable commitments

| # | Commitment (with §-anchor) | Gate (pyramid level) | Test ID | Status |
|---|---|---|---|---|
| 1 | The generic engine spine carries no reference to any `families/**` project — the `family → engine` arrow is one-way (§D2, §P2). | architecture / dependency assertion (CI) | `ENGINE_FAMILY_AGNOSTIC` | Live |
| 2 | The engine event-store migration set carries **no family-named table** — the whole engine `MigrationSet.All` is scanned for a family-typed table/column/FK, and an inverse positive guard RED-fails if a `read_model` schema or `deposits`-named object re-appears in the engine set (§A5–§A7, 2026-06-13). | architecture / dependency assertion (CI) | `EVENT_STORE_SCHEMA_FAMILY_AGNOSTIC` | Live |
| 3 | The Engine API host's composition CODE (`Babelstone.Engine.Api/Program.cs`) names **no concrete family type in code** — not the aggregate state type, not a family store/endpoint, not a `Babelstone.Families.*` identifier (a family named only in a COMMENT is fine). A SOURCE gate, NOT the §P2 `.csproj` gate — the host keeps its `families/**` reference as the `HostModuleLoader` scan anchor (§A14), so banning the reference would contradict §A14; this bans naming a family in the composition code (§A17–§A18, 2026-06-20). | architecture / source assertion (CI) | `ENGINE_API_HOST_FAMILY_AGNOSTIC` | Live |

Related: this ADR's family-agnosticism is the family-level cousin of the variant-level [`ZERO_ENGINE_DIFF_PER_VARIANT`](./commitment-catalogue.md) (adding a *variant* is zero engine diff; adding a *family* is zero *generic*-engine diff). `ENGINE_FAMILY_AGNOSTIC` is now `Live` — the dependency assertion (`EngineFamilyAgnosticTests` in `Babelstone.Engine.Tests`) parses the eight spine projects' `.csproj` and fails if any references `families/**`, and a sibling assertion keeps that allowlist in lockstep with the §P2 enumeration parsed off this ADR; it is promoted to the [commitment catalogue](./commitment-catalogue.md) (row 12) as the single source of truth for its status. `EVENT_STORE_SCHEMA_FAMILY_AGNOSTIC` is the **schema-level twin** added 2026-06-13 (§A7): the code gate guards the `family → engine` arrow at the `.csproj` level, this one guards it at the migration-schema level, so a family-named table cannot leak into the engine set even though no `.csproj` reference does. It is `Live` as `EventStoreSchemaFamilyAgnosticTests` (`Babelstone.Engine.Tests`) and registered in the [commitment catalogue](./commitment-catalogue.md) (row 12a) as the single source of truth for its status.

---

## Amendment — 2026-05-31: The host-module composition contract (`IFamilyHostModule`)

E.5 realized the §D5-deferred HTTP boundary as `Babelstone.Engine.Api` wiring a *single* term-deposit family inline (the 2026-05-30 revision). Preparing the host for a *second* family revealed an asymmetry: §D4/§P4 commit to "composition at the edge via discovery", but only the **fold** side has a concrete mechanism (`FamilyModuleLoader`'s assembly-scan). §P4's "mirroring assembly-scan discovery" for the **decider + endpoint** side is an analogy, not a named contract — so a second family would mean hand-editing the host's compose code. This amendment pins the host-side contract that closes that gap. It is **additive**: it realizes §D4/§P4 and reverses nothing.

### A1 · `IFamilyHostModule` is the host-side composition seam

Each family contributes an `IFamilyHostModule` (defined in the host, `Babelstone.Engine.Api`) with three members: `FamilyName`; `ConfigureServices(IServiceCollection, FamilyHostContext)`; `MapEndpoints(IEndpointRouteBuilder)`. A family's module owns everything family-specific the host used to hand-wire: its closed-generic `AggregateRuntime<TState>` (with its `() => TState.Empty` seed and fold registry), its decider registration, and its endpoint mapping — so the host never names a family aggregate type. The host composes by looping over the modules: `ConfigureServices` before `Build()`, `MapEndpoints` after. `FamilyHostContext` carries the per-deployment ingredients that are not DI services — the pinned `VerifiedPack` (shared across families, [ADR-PC-009](./ADR-PC-009-per-instance-version-pinning.md)) and the configuration root; the family-agnostic infrastructure (event store, codec, PII protector, clock, rate-sheet store, settlement port) is registered once as shared singletons each module resolves. This gives §D4 ("composition at the edge … via discovery") and §P4 a concrete decider+endpoint contract.

### A2 · The host MAY reference families; the spine MAY NOT (clarifying §P2/§D4)

`IFamilyHostModule` and all composition code live in `Babelstone.Engine.Api` — the §D4 composition root — which is **not** one of the §P2 spine projects and therefore *may* carry a `ProjectReference` into `families/**`. This is the standing, intended exemption: `ENGINE_FAMILY_AGNOSTIC` (Verifiable commitment 1) gates only the spine, never the host. The host naming a family is §D4's job; a *spine* library naming one is the forbidden edge. (Recorded here to make the host's exclusion from the gated spine an explicit exemption rather than a silent absence from the allowlist.)

### A3 · Explicit list now (Option A), assembly-scan later (Option B) — same contract

The host holds an explicit list of modules today (`[new TermDepositHostModule()]`) — type-safe and debuggable at the current family count. Because every module implements this contract with a public parameterless ctor, swapping the explicit list for `FamilyModuleLoader`-style assembly-scan discovery later (the §P4 mechanism) is a localized change to the host's discovery loop, with **zero change to any family**. Adding a family today is: write its module, add the host `ProjectReference`, add one list entry — never a surgical edit threading a new aggregate type through the compose block. The residual one-line edit is the accepted cost of an in-tree host (a referenced assembly must be loadable to be composed or scanned); a true runtime-plugin model (glob + `Assembly.LoadFrom`) is the only zero-host-touch path and is out of scope here.

### A4 · This amends the decision; it does not supersede this ADR

§D1–§D5 remain binding as written. §D4 (composition at the edge via discovery) and §P4 (discovery at the host/test edge) are the sections this refines; the contract is *appended to* — not a revision of — them. No decision is reversed: the host was always the composition root that wires families; this names the contract by which it does so. The `IFamilyHostModule` contract is enforced by the compiler and exercised end-to-end by `DepositsApiIntegrationTests` (the constitute→read→mature flow through the real host); it adds no new gated fitness function, so the [commitment catalogue](./commitment-catalogue.md) is unchanged.

---

## Amendment — 2026-06-13: Family-agnosticism extends to migration-owned schema

**In plain English:** the rule "the engine must not know what a *deposit* is" was, until now, only enforced at the *code* level — no engine project may reference a family's code (§P2). But a family's *database schema* had quietly leaked in: the term-deposit read-model table `read_model.deposits` (a family-named table with family-typed columns like `maturity_date` and `coupons_paid`) shipped in the *engine's* own migration set. This amendment closes that gap. A family-named table belongs in a family-owned migration set, not the engine's, and the engine's event-store migrations now carry **zero** family-named tables — proven by a new schema-level fitness test. It is **additive**: it extends §D2/§P2's family-agnostic boundary from code to schema and reverses no part of the Decision.

### A5 · The family-agnostic boundary covers migration-owned schema, not just code

§P2 forbids the spine from carrying a `ProjectReference` into `families/**` — the *code*-coupling edge. This amendment records the parallel rule for *schema*: the engine's event-store migration set (`Babelstone.EventStore.Migrations`) MUST carry **no family-named table** — no `read_model.deposits`, no `maturity_calendar`, no per-family relational shape. Events stay OPAQUE (a `payload BYTEA` keyed by the generic `family` / `event_type` columns, ADR-PC-001 §P1); a family-named table in the engine set is the schema-shaped erosion of the same family-agnosticism the `.csproj` gate guards at the code level. A family-named table — including a denormalized CQRS read model — belongs in a **family-owned** migration set.

### A6 · Realised by relocating `read_model.deposits` to the term-deposit family

This is realised by **relocating** the CQRS read-model schema (formerly engine migration `0013_read_model.sql`) into a term-deposit family-owned migration set: `families/term-deposit/src/Babelstone.Families.TermDeposit.Application/Migrations/` (`Migration.cs` / `MigrationRunner.cs` / `MigrationSet.cs` + `Sql/0001_read_model.sql`), mirroring the orchestrator's own-schema precedent (`Babelstone.Orchestrator.Migrations`). Because the read model lives on the **same** Postgres tier as the engine event store ([ADR-IC-005](../../integration_concepts/adrs/ADR-IC-005-cqrs-read-model-storage.md) §S1 — zero incremental infrastructure), the family runner keeps a **distinct** ledger (`schema_migrations_term_deposit`) and a **distinct** advisory-lock key from the engine's, so the two independently-versioned sets coexist on one cluster without ledgering or blocking each other. A hard **engine-before-family** ordering holds: `0001_read_model.sql` GRANTs on the `babelstone_engine` role that engine migration `0002_append_only_role.sql` creates, and carries a fail-loud guard that RAISEs a clear exception if that role is absent. The schema name (`read_model`) and table (`deposits`) and all ADR-IC-005 §P1 semantics are unchanged — only ownership (which migration set) moved. The host applies it via a `ReadModelMigrationHostedService` registered by `TermDepositHostModule` (the composition root — §A2 lets the HOST name a family; the spine still may not).

### A7 · Gated by a schema-level fitness test (a sibling of `ENGINE_FAMILY_AGNOSTIC`)

The boundary in §A5 is now a mechanical check, the schema-level twin of the code-level `ENGINE_FAMILY_AGNOSTIC` dependency gate. `EventStoreSchemaFamilyAgnosticTests` (`Babelstone.Engine.Tests`) parses the **entire** engine `MigrationSet.All` — no read-side carve-out, because the engine now owns zero family tables — and runs three deny scans (no family-typed table name, column name, or FK target) plus an **inverse positive guard** that RED-fails if a `read_model` schema or a `deposits`-named object is ever re-introduced into the engine set. It is infrastructure-free and deterministic (it reads the same embedded SQL the runner applies, never a database). Registered as `EVENT_STORE_SCHEMA_FAMILY_AGNOSTIC` in the [commitment catalogue](./commitment-catalogue.md), recorded in this ADR's Verifiable commitments below.

### A8 · This amends the decision; it does not supersede this ADR

§D1–§D5 and §P2 remain binding as written. §D2/§P2 (the family-agnostic engine; the forbidden engine→family edge) are the sections this extends — from code coupling to migration-owned schema — and the rule is *appended to*, not a revision of, them. No decision is reversed: the engine was always meant to be family-agnostic; this names the schema half of that boundary and gates it.

---

## Amendment — 2026-06-20: The host-composition seam moves to a shared `Babelstone.Engine.Hosting` assembly; the family owns its host wiring

**In plain English:** the per-family host wiring — the host module, its `/v1/deposits` HTTP endpoints, and its request/response DTOs — used to live in the shared engine API host project (`Babelstone.Engine.Api`), which therefore had to compile-reference each family. This amendment moves the *contract* of that wiring (the `IFamilyHostModule` seam + the family-agnostic in-process hosting components) into a small new shared assembly, and moves the term-deposit family's *concrete* wiring into the family's own `.Application` project. Now the family references a tiny hosting-contract assembly instead of the host referencing the family for those types — the prerequisite that lets the host *discover* families instead of listing them (the follow-up, bd `babelstone-9w2k.2`). It is **additive** and reverses no part of the Decision: the `family → engine` arrow is preserved (the new assembly points engine-ward), the `/v1/deposits` route literals and the mandatory `Idempotency-Key` check are byte-for-byte unchanged, and §D1–§D5 stay binding. This refines the 2026-05-31 amendment's §A1/§A2 (it does not edit them in place).

### A9 · `IFamilyHostModule` now lives in `Babelstone.Engine.Hosting`, not the host (refines §A1)

The 2026-05-31 §A1 placed `IFamilyHostModule` "in the host, `Babelstone.Engine.Api`". It now lives in a new shared hosting-contract assembly, **`Babelstone.Engine.Hosting`**, alongside `FamilyHostContext` and the **family-agnostic in-process hosting components** the seam needs (the `BusEventSerializer` marker record, and `ProjectionRelayService` / `ProjectionRelayOptions` / `BudgetedPostCommitProjector`). The move is *forced* by §D2/§P2: the term-deposit module resolves `BusEventSerializer` and `ProjectionRelayService`/`Options`, so if the module relocates into the family `.Application` project (§A10) while those types stayed host-only, the family would have to reference the host — the arrow inversion §D2 forbids. `Babelstone.Engine.Hosting` references only `Babelstone.Engine` (the spine, for `IEventSerializer` / `ProjectionDrainer` / `ProjectionRegistry` / `IPostCommitProjector`), `Babelstone.Packs` (`VerifiedPack`), and the ASP.NET shared framework (`IServiceCollection` / `IEndpointRouteBuilder` / `BackgroundService`); it carries **no** `families/**` reference. The arrow is hosting → engine — the same direction the host already takes — so the deterministic spine gains nothing (no ASP.NET/hosting type enters `Babelstone.Engine`). The Avro/Schema-Registry composition that *produces* a `BusEventSerializer` (`HostBusEncoding`) stays in `Babelstone.Engine.Api`; only the marker the family resolves moved.

### A10 · The family owns its host module, endpoints, and HTTP contracts (refines §A1)

`TermDepositHostModule`, `DepositsEndpoints`, and the deposits HTTP DTOs (`ConstituteDepositRequest` et al., formerly `Contracts.cs`, now `DepositsContracts.cs`) move out of `Babelstone.Engine.Api` and **into** `families/term-deposit/src/Babelstone.Families.TermDeposit.Application/` (namespace `Babelstone.Families.TermDeposit.Application`), implementing `IFamilyHostModule` against the `Babelstone.Engine.Hosting` contract. The closed-generic `AggregateRuntime<DepositPosition>` is still constructed **inside** the family module (compile-time type safety preserved), and the host still never names a family aggregate type. The `/v1/deposits` route literals and the mandatory `Idempotency-Key` 400-on-missing check are relocated **byte-for-byte** — endpoints stay family-owned, only their home assembly changed. The family `.Application` project gains a `FrameworkReference` to the ASP.NET shared framework and a `ProjectReference` to `Babelstone.Engine.Hosting`; the `family → engine` / `family → hosting` arrows are the only edges added.

### A11 · The §A2 spine-exemption now covers `Babelstone.Engine.Hosting`; the host keeps its family reference (refines §A2)

The 2026-05-31 §A2 said "`IFamilyHostModule` **and all composition code** live in `Babelstone.Engine.Api`" — which is now false on both counts. The load-bearing §A2 *decision* — that a non-spine project **may** reference families while the §P2 spine **may not** — is **preserved and extended**: `Babelstone.Engine.Hosting` is *also* not one of the §P2 spine projects and *also* may name a family in principle (it chooses not to), exactly like `Babelstone.Engine.Api`. The host retains its `ProjectReference` into `families/**` **solely** to instantiate the module in the §A3 explicit Option-A list (`[new TermDepositHostModule()]`), which is unchanged by this move; bd `babelstone-9w2k.2` replaces that list with assembly-scan discovery, after which the host's compile reference to the family drops too. `ENGINE_FAMILY_AGNOSTIC` (Verifiable commitment 1) is unchanged: `Babelstone.Engine.Hosting` is **not** added to the §P2 spine enumeration nor to the `EngineFamilyAgnosticTests` `SpineProjects` allowlist (the gate iterates that explicit allowlist, so a new non-spine assembly is simply not checked — and adding it would wrongly promise the gate enforces a spine constraint §P2 does not place on a hosting-contract assembly). The eight §P2 spine projects stay exactly as enumerated.

### A12 · This amends the decision; it does not supersede this ADR

§D1–§D5 remain binding as written, and the 2026-05-31 §A1–§A4 and 2026-06-13 §A5–§A8 amendments stay in force except where §A9–§A11 above explicitly refine the *placement* recorded in §A1/§A2 (where the seam and composition code live). No decision is reversed: the host was always the composition root that wires families, the engine spine was always family-agnostic, and the `family → engine` arrow always pointed one way — this names the contract assembly the seam now lives in and the family assembly the concrete wiring now lives in, both on the family/hosting side of that same arrow. It adds no new gated fitness function (the contract stays compiler-enforced and is exercised end-to-end by `DepositsApiIntegrationTests` through the real host), so the [commitment catalogue](./commitment-catalogue.md) is unchanged.

---

## Amendment — 2026-06-20: The host discovers families by assembly-scan (§A3 Option B), retiring the explicit list

**In plain English:** since the 2026-05-31 amendment the host wired the families it runs from a hand-written list — one literal `[new TermDepositHostModule()]` in `Program.cs`. Adding a family meant editing that list. This amendment makes the host *discover* the families instead: it scans the family assemblies it already compiles against and finds every host module automatically, so a new family needs zero edits to the host. That is exactly the Option-B step §A3 promised was a "localized change with zero change to any family", now taken. It is **additive** — it realises §A3/§A4's deferred Option B and reverses nothing; §D1–§D5 and every prior amendment stay binding.

### A13 · The explicit Option-A list is replaced by `HostModuleLoader` assembly-scan discovery (realises §A3 Option B)

The §A3 explicit Option-A list (`IReadOnlyList<IFamilyHostModule> familyModules = [new TermDepositHostModule()]` in `Babelstone.Engine.Api/Program.cs`) is replaced by **`HostModuleLoader`** (`Babelstone.Engine.Api`), the host-side twin of the spine's `FamilyModuleLoader` (`FamilyModule.cs`). It assembly-scans for concrete `IFamilyHostModule` types with a public parameterless ctor and activates one of each — the **same** discovery mechanism, public-parameterless-ctor contract, and `ReflectionTypeLoadException` resilience `FamilyModuleLoader` uses for fold modules. The host's existing `ConfigureServices` / `MapEndpoints` loops over the returned modules are **unchanged**; only the *source* of the module list changed (a literal → a scan). Adding a family is now its module + the host `ProjectReference` (the load anchor, §A14) — the residual one-entry list edit §A3 still carried is now gone, so the host compose block is fully family-count-invariant.

### A14 · §A3-blessed in-process scan over compile-referenced assemblies; the `families/**` `ProjectReference` STAYS as the load anchor (refines §A11)

The scan targets the **compile-referenced** `Babelstone.Families.*` assemblies (`HostModuleLoader.FamilyHostAssemblies()` derives them from the *host* assembly's — `typeof(HostModuleLoader).Assembly`, i.e. `Babelstone.Engine.Api` — direct references and force-loads each; the host assembly, NOT `Assembly.GetEntryAssembly()`, because under `WebApplicationFactory<Program>` the entry assembly is the test runner, which does not reference the families), **not** an `Assembly.LoadFrom` glob over a plugin directory — preserving the compile-time type safety, AOT-friendliness, and greppability §A3 names, and keeping the drop-in-plugin model "out of scope" exactly as §A3 ruled. This **refines** the 2026-06-20 §A11 wording "after which the host's compile reference to the family drops too": for an *in-tree* host the `families/**` `ProjectReference` **must stay** — a referenced assembly must be loadable to be *scanned*, the same accepted in-tree cost §A3 states for it to be *composed*. What drops is only the explicit **list-entry edit** in `Program.cs`, not the project reference; the family's host wiring still reaches `main` solely through that one-way `family → engine` / `family → hosting` reference, and `ENGINE_FAMILY_AGNOSTIC` (Verifiable commitment 1) is untouched (the host is the §A2 standing exemption; `HostModuleLoader` adds no spine→family edge).

### A15 · Reflection stays at the composition root; discovery is stable-ordered to preserve engine-before-family migration ordering (§A6)

`HostModuleLoader` lives in `Babelstone.Engine.Api` — the §D4 composition root — and the dispatch spine names no reflection ([ADR-PC-010](./ADR-PC-010-dotnet-hand-rolled-engine.md) §P5: reflection is confined to the composition seam, never the per-event dispatch path). The loader **fails loud at the discovery seam** on a duplicate `FamilyName` (two modules composing one family would double-register its runtime + endpoints — the host-module analogue of `HandlerRegistry` throwing on a duplicate `event_type`, `FamilyModule.cs`) and on a module lacking a public parameterless ctor (surfacing a diagnosable error rather than a bare `Activator` `MissingMethodException`). Discovered modules are returned in a **stable** order (by assembly name, then full type name), so the per-module `ConfigureServices` loop — and therefore each family's `ReadModelMigrationHostedService` registration order — is reproducible across boots, keeping the **engine-before-family** read-model migration ordering (§A6) independent of reflection's unspecified type-enumeration order. A new `HostModuleLoaderTests` fitness set exercises discovery, stable ordering, and both fail-loud guards; the existing `DepositsApiIntegrationTests` boots the real host through the loader end-to-end. It adds no new *gated* commitment-catalogue row, so the [commitment catalogue](./commitment-catalogue.md) is unchanged.

### A16 · This amends the decision; it does not supersede this ADR

§D1–§D5 remain binding as written, and the 2026-05-31 §A1–§A4, 2026-06-13 §A5–§A8, and 2026-06-20 §A9–§A12 amendments stay in force. This **realises** §A3's deferred Option B and **refines** §A11's "compile reference drops too" wording to the accurate in-tree position (the `ProjectReference` stays as the scan's load anchor; only the explicit list edit drops). No decision is reversed: the host was always the composition root discovering families at the edge (§D4/§P4); this names the concrete discovery mechanism it now uses.

---

## Amendment — 2026-06-20: The host's last family wiring relocates into the family module; a source gate makes it stick

**In plain English:** after the assembly-scan move (§A13–§A16), the host's `Program.cs` still wired ONE family-owned thing — the term-deposit read-model store. This amendment relocates that registration into the family's own `IFamilyHostModule`, so the host's composition code now names no family at all, and adds a fitness gate that keeps it that way. This is the host-side capstone of the family-count-invariant epic (bd `babelstone-9w2k`).

### A17 · The family-owned read-model store registration relocates into `TermDepositHostModule` (refines §A10/§A2)

§A10 placed the family's *concrete* host wiring in its `.Application` `IFamilyHostModule`, but one registration — the family-owned `IDepositReadModelStore` / `PostgresDepositReadModelStore` (a deposit-shaped, family-NAMED store, family-owned by §D2/§P2) — was still wired inline in `Program.cs` because it needed the host's secret-resolved engine connection string. This amendment **completes the §A10 relocation**: the registration moves into `TermDepositHostModule.ConfigureServices`, and the host hands the family module its already-secret-resolved engine connection string via a new `FamilyHostContext.EngineConnectionString` field — so the family module registers its own store while the `ISecretProvider` boundary ([ADR-PC-004](./ADR-PC-004-pii-crypto-shredding.md) A1) stays at the host composition root and is never re-crossed in the family. The host's composition code now names **no** concrete family type.

### A18 · Gated by `ENGINE_API_HOST_FAMILY_AGNOSTIC`, a SOURCE gate distinct from §P2's `.csproj` gate (refines §A14)

The §A14 position — the host KEEPS its `families/**` `ProjectReference` as the `HostModuleLoader` scan anchor — means the `.csproj`-level `ENGINE_FAMILY_AGNOSTIC` gate (Verifiable commitment 1) deliberately does NOT cover the host (§A2 standing exemption). So the "host names no family" property §A17 achieves needs a DIFFERENT gate. It is **two family-agnostic pattern scans** for the `Babelstone.Families.*` namespace prefix (the membership predicate the whole design uses, so adding a family never edits the gate) — deliberately NOT a hand-maintained per-family token denylist, which is the high-churn shape the engine/orchestrator allowlist gates avoid: (1) a SOURCE scan of `Program.cs` (comments + string literals stripped) — catching a fully-qualified family reference or a LOCAL `using` (whose directive line carries the prefix); and (2) a scan of the host's GLOBAL-import surface — the csproj `<Using>` items + any `global using` in another host file — which is the one vector that would leave a bare, prefix-less family token in `Program.cs`, invisible to a `Program.cs`-only scan (the sibling `.csproj` gate cannot backstop it: the host is the §A2/§A14 exemption it does not cover, and it checks `ProjectReference`, not `<Using>`; `<ImplicitUsings>` imports only the SDK set, never a project namespace, so these two surfaces are the whole vector). `EngineApiHostFamilyAgnosticTests` (`Babelstone.Engine.Api.Tests`) is that gate, registered as `ENGINE_API_HOST_FAMILY_AGNOSTIC` in the [commitment catalogue](./commitment-catalogue.md) (row 12b) and recorded in this ADR's Verifiable commitments (row 3). It is the host-side cousin of the orchestrator's `ORCHESTRATOR_FAMILY_AGNOSTIC` (ADR-IC-018) and the epic's capstone.

**Discovery-anchor refinement (refines §A14's scan mechanism).** Once the host names no family type in code (§A17), the C# compiler **elides the family `ProjectReference` from the host assembly's IL metadata reference list** (an unused reference is not emitted), so `HostModuleLoader.FamilyHostAssemblies()` reading `host.GetReferencedAssemblies()` — the §A14 compile-graph anchor — would discover ZERO families. The fix keeps §A14's decision (the `ProjectReference` STAYS as the load anchor) and adds a second anchor: the loader also probes its **own output directory** for `Babelstone.Families.*.dll`, which the `ProjectReference`s copy next to the host identically under `dotnet run` and `WebApplicationFactory<Program>`. This stays in-tree (the probe reads only the host's own output dir, where its compile-referenced families land — not an external `Assembly.LoadFrom` plugin glob, still "out of scope" per §A3) and keeps the `Babelstone.Families.` name prefix as the family-agnostic predicate. A genuinely missing family then surfaces fail-closed at the §A1-companion pack family-manifest cross-check (bd `babelstone-9w2k.3`), not as a silent zero-discovery.

### A19 · This amends the decision; it does not supersede this ADR

§D1–§D5 remain binding as written, and the 2026-05-31 §A1–§A4, 2026-06-13 §A5–§A8, and 2026-06-20 §A9–§A16 amendments stay in force. This **completes** §A10's relocation (the last family registration moves family-ward) and **refines** §A14 by naming the SOURCE gate the §A2 exemption requires (the `.csproj` gate cannot cover the host). No decision is reversed: the host was always the composition root that wires no family by hand (§D4); the family → engine/hosting arrow is preserved (`FamilyHostContext` points engine-ward); the host keeps its scan-anchor `ProjectReference` (§A14). It adds the gated `ENGINE_API_HOST_FAMILY_AGNOSTIC` commitment recorded above.

---

## Amendment — 2026-06-24: A countable exit condition for the `ConstitutionPipeline` deferral (bd `babelstone-osv6`), and the narrow rate-resolution-prefix extraction judged on its own

**In plain English:** §D5/§P5 deferred the generic `ConstitutionPipeline` to the "second decider" on a rule-of-three judgement. The second decider (personal_loan, [ADR-PC-031](./ADR-PC-031-personal-loan-family.md)) has now landed, and the rule-of-three was found NOT cleanly met for the *full* choreography — so osv6 was deferred a second time. But "deferred until the shape matches" is a *qualitative* trigger: it has no number, so the one genuinely-repeated fragment — the resolve-sheet-then-resolve-TAN-or-throw two-step — could keep being copied silently with no rule for when to stop. This amendment does three things, all bookkeeping: it **counts** that duplication exactly (3 call sites today, named with file + line), it evaluates the *narrow* fix some reviewers reach for first — a throwing `RateSheetResolution.RequireTan` helper — **on its own** and explicitly defers it with a verified reason, and it gives osv6 a **countable** revisit trigger (the 4th matching call site) on top of the existing qualitative full-shape one. It is pure documentation: no code, no schema, no event, no gate changes. It is **additive** — it sharpens §D5/§P5's deferral with a number and reverses nothing; §D1–§D5 and every prior amendment stay binding.

### A20 · The rate-resolution two-step is duplicated across exactly 3 decider call sites (the counted baseline)

The single fragment genuinely repeated across deciders — the only one §P5's "written as separable steps" leaves copy-shaped rather than shared-through-a-generic-port — is the **rate-resolution-and-fail-loud two-step**: resolve the active rate sheet for the family at the operative instant (`IRateSheetStore.ResolveAsync(...)`), `?? throw new DomainRejectedException(...)`; then resolve the TAN for `(product, role, principal)` (`resolution.ResolveTanBasisPoints(...)`), `?? throw new DomainRejectedException(...)`. The kernel math and event shapes around it are irreducibly per-family (so they are not duplication), and the engine ports (`AggregateRuntime` / `IRateSheetStore` / `ISettlementPort`) are already generic and *reused*, not copy-pasted (so they are not duplication either). As of this amendment the two-step appears at **exactly three** call sites, all in family `.Application` deciders:

| # | Site | File | Lines (approx.) | Operative instant |
|---|---|---|---|---|
| 1 | term-deposit **constitution** | `families/term-deposit/src/Babelstone.Families.TermDeposit.Application/TermDepositConstitutionService.cs` | ~148–158 | `command.ConstitutedAt` |
| 2 | term-deposit **renewal** (`SAME_TERM_CURRENT_RATE` branch) | `families/term-deposit/src/Babelstone.Families.TermDeposit.Application/TermDepositConstitutionService.cs` | ~580–588 | `command.RenewedAt` |
| 3 | personal_loan **disbursement** | `families/personal-loan/src/Babelstone.Families.PersonalLoan.Application/PersonalLoanConstitutionService.cs` | ~97–107 | `command.DisbursedAt` |

This count is the **baseline** the osv6 trigger below is measured against. Recording it here makes the duplication a tracked, bounded quantity rather than one that can grow unnoticed: the next reviewer who adds a 4th site knows the threshold it crosses (§A22), and the next reviewer who reads osv6 knows it is at 3-of-4, not "some unknown amount".

### A21 · The narrow `RateSheetResolution.RequireTan` extraction is evaluated on its own — and DEFERRED, separately from the full-pipeline question

The full-choreography `ConstitutionPipeline` (osv6) is the *broad* extraction. There is a **narrower** one that PR #283's review did not separately weigh: lift only the §A20 two-step — NOT the whole settle/primitive/tail choreography — into a small throwing helper, e.g. a `RateSheetResolution.RequireTan(family, instant, product, role, principal)` that returns the TAN + `rate_sheet_version_id` or throws `DomainRejectedException`, or equivalently relocating just the `?? throw` onto the resolution type. It is judged here on its own merits, decoupled from the full-pipeline rule-of-three:

- **It does meet rule-of-three on its own.** Unlike the full choreography (which §A20's three sites diverge on — de-settled deposit vs eager-debit disbursement, pack-primitive reads, post-decide tails), the two-step itself is *byte-identical in shape* across all three sites. So the usual "wait for the shape to stabilise" objection does NOT apply to this fragment.
- **But it cannot be placed without a cost the fragment does not justify.** Its only two sound homes are both blocked:
  - **(a) The generic `Babelstone.RateSheets` spine project.** `ResolveTanBasisPoints` already lives on `RateSheetResolution` there, so this is the natural home — *except* the helper must throw `DomainRejectedException`, which lives in `Babelstone.Engine` (the spine's command-result type). `Babelstone.RateSheets` today references only Npgsql + YamlDotNet — it has **no** reference to `Babelstone.Engine`. Adding the helper would force a brand-new `Babelstone.RateSheets → Babelstone.Engine` `ProjectReference`, widening spine-internal coupling for a six-line helper. (Verified at this amendment's date: `engine/src/Babelstone.RateSheets/Babelstone.RateSheets.csproj` carries no engine reference; `DomainRejectedException` is `engine/src/Babelstone.Engine/DomainRejectedException.cs`.)
  - **(b) A shared cross-family application project.** This is precisely the candidate **§D1 REJECTS** ("There is **no** shared cross-family application project"). Putting the helper there to be shared by both families would re-introduce the open/closed-violating composition project the whole ADR exists to forbid.
- **A non-throwing variant does not help.** A helper that *returns* a nullable instead of throwing pushes the `?? throw` back to all three call sites — leaving the duplication exactly where it is while adding an indirection. The throw is the part that wants `Babelstone.Engine`; removing it removes the value.

**Verdict: DEFERRED, same trigger as osv6.** Forcing either home would trade a small, honest, 3-site, six-line duplication for either a leaky abstraction (a spine library taking an engine reference to throw an engine exception) or an outright §D1 violation. A documented defer is sounder than either. The deciders stay written for-lift (§P5); the narrow extraction is taken — if at all — *with* the osv6 lift, not before it, because whichever home the full pipeline lands in is the home this fragment would share. **The optional code change is therefore NOT made in this amendment** (it is documentation only).

### A22 · osv6 gains a COUNTABLE revisit trigger on top of the qualitative full-shape one

§D5/§P5 and the osv6 issue already carry a *qualitative* exit condition: extract when a path appears whose settle/primitive/tail shape genuinely matches one of the existing two (a real rule-of-three on the **full** choreography). That condition stands. This amendment **adds** a second, *countable* trigger so the deferral cannot drift unbounded:

> **osv6 revisit trigger (countable).** Revisit the osv6 `ConstitutionPipeline` (and, with it, the §A21 narrow `RequireTan` extraction) when EITHER holds:
> 1. **Full-shape match (qualitative, pre-existing).** A constitution/disbursement path appears whose settle + pack-primitive + post-decide-tail shape genuinely matches one of the two existing full shapes — a real rule-of-three on the FULL choreography, not just its rate-resolution prefix.
> 2. **Count threshold (quantitative, new).** The §A20 rate-resolution two-step reaches a **4th** decider call site (i.e. one more than the 3 counted in §A20). At the 4th site, the narrow extraction (§A21) MUST be re-evaluated explicitly in that change's review — either taken (accepting whichever placement cost the then-current project graph imposes) or re-deferred with the count updated in this ADR. A 4th copy without that explicit re-evaluation is the silent-growth this amendment exists to prevent.

Whichever trigger fires first reopens osv6. Until then osv6 stays `DEFERRED` with the duplication bounded at the §A20 count.

### A23 · This amends the decision; it does not supersede this ADR

§D1–§D5 remain binding as written, and the 2026-05-31 §A1–§A4, 2026-06-13 §A5–§A8, and 2026-06-20 §A9–§A19 amendments stay in force. This **sharpens** §D5/§P5's `ConstitutionPipeline` deferral — adding a counted baseline (§A20), an on-its-own evaluation of the narrow extraction §P5 did not separately weigh (§A21), and a countable revisit trigger (§A22) — and reverses nothing: the pipeline is still deferred, deciders still written for-lift, §D1's "no shared cross-family application project" is reaffirmed (it is exactly why §A21 (b) is blocked), and the `family → engine` arrow is untouched (the narrow helper is declined precisely because its throwing form would add a `RateSheets → Engine` edge inside the spine). It adds **no** new gated fitness function and changes **no** commitment-catalogue row (the duplication count is a documented baseline, not a CI gate), so the [commitment catalogue](./commitment-catalogue.md) is unchanged.

---

## Amendment — 2026-06-27: `ISettlementPort` is retired — the family-agnostic settlement port role passes to the `Movement` spine (bd `babelstone-t7o3.17`)

The plain-English version: this ADR's Decision (§D2) and topology sketch (§P1) introduced `ISettlementPort` as "the one new generic, family-agnostic port" the deciders depend on. That port has since been replaced wholesale by the **append-first `Movement`** spine, and bd `babelstone-t7o3.17` deletes the now-orphaned `ISettlementPort` / `SettlementInstruction` / `LoggingSettlementPort` types. So the words in §D2/§P1 that name `ISettlementPort` are now stale. The *decision they encode* — deciders depend on generic engine ports, never on a family — is untouched; only the example port changed. This amendment records that so the deletion is not a silent contradiction ([ADR-PC-020](./ADR-PC-020-llm-toolchain-and-conformance-governance.md) §D3).

### A24 · The generic settlement port is now the `Movement` spine, not `ISettlementPort`

- **What changed.** [ADR-PC-032](./ADR-PC-032-money-movement-primitive.md) made eager settlement illegal and replaced the eager `SettleAsync`-before-append port with the **append-first `Movement`** primitive: a money-moving decider records an Originated `Movement` on its event (APPEND-FIRST) and the confirmation-gated substrate-owned settlement saga (ADR-PC-032 slot 5) effects the cash leg. The decider→`ISettlementPort` dependency was already removed leg-by-leg in bd `babelstone-t7o3.13`/`.16` (dated-amended into [ADR-PC-016](./ADR-PC-016-legacy-current-account-adapter.md) §128–129 and [ADR-PC-029](./ADR-PC-029-engine-command-ingress.md)); bd `babelstone-t7o3.17` now deletes the unreferenced `ISettlementPort` / `SettlementInstruction` / `LoggingSettlementPort` types (and the `Recording`/`Throwing` test doubles). `SettlementDirection` survives, now owned by the `Movement` spine.
- **What §D2/§P1 should now read.** Wherever §D2 and the §P1 topology say "the new `ISettlementPort`" / "the one new generic, family-agnostic port", read **the `Movement` spine** (ADR-PC-032). That generic-port enumeration is the only stale text.
- **What is unchanged (and why this is additive, not a supersession).** §D1–§D5 remain binding, and the prior amendments stay in force. The load-bearing §D2 invariant — *deciders depend on generic engine ports, the dependency arrow is `family → engine`, adding a family is zero generic-engine diff* — holds exactly as written: `Movement` is itself a generic, family-agnostic spine primitive, so the arrow and the open/closed property are untouched. This reverses nothing, adds **no** new gated fitness function (the append-first invariant is gated by `MOVEMENT_APPEND_FIRST`, owned by ADR-PC-032), and changes **no** [commitment-catalogue](./commitment-catalogue.md) row.

---

## Cross-references

- [ADR-PC-010](./ADR-PC-010-dotnet-hand-rolled-engine.md) — the hand-rolled, single-deployable engine spine this application layer sits above.
- [ADR-PC-019](./ADR-PC-019-repository-strategy-monorepo.md) — the monorepo `/families` subtree, `CODEOWNERS` ownership, and extraction-ready-subtree discipline the decider project joins.
- [ADR-PC-008](./ADR-PC-008-rate-sheet-storage-and-deploy-api.md) — rate resolution at constitution (the decider's §P3 stamp); the §S2 in-transaction resolve is deferred (bd `babelstone-3k10`).
- [ADR-PC-016](./ADR-PC-016-legacy-current-account-adapter.md) — the legacy current-account settlement contract the decider's `ISettlementPort` fronts; E.3 uses an in-memory stub, WireMock SOAP fidelity is H.2, the real ACL is DEF-1.
- [ADR-PC-009](./ADR-PC-009-per-instance-version-pinning.md) — per-instance pack/schema pinning the decider resolves against; its "reserve, don't pre-build" discipline applied here to the pipeline abstraction.
- [event-store §3](../feature-design-event-store-projections.md) — the family fold layer (`IFamilyModule` / `FamilyModuleLoader`) whose `family → engine` plugin arrow this extends to the command side.
- [01 §1](../01-product-architecture.md) — the one-engine-many-families thesis the open/closed placement honours.

---

*Proposed 2026-05-30; accepted 2026-05-31 by jhosm.*
*Revised 2026-05-30 (E.5): the §D5-deferred external HTTP boundary is realized as `Babelstone.Engine.Api` — a minimal-API host (`POST /v1/deposits` constitute, `GET /v1/deposits/{id}` deposit_position, `POST /v1/deposits/{id}/maturity`) wrapping `TermDepositConstitutionService`, mirroring the `RateSheets.Api` precedent ([ADR-PC-008](./ADR-PC-008-rate-sheet-storage-and-deploy-api.md) Amendment A1). It is the engine boundary the Python MCP server ([ADR-IC-010](../../integration_concepts/adrs/ADR-IC-010-mcp-server-runtime-and-sdk.md)) calls. Authn/authz (OAuth 2.1 + Kong per ADR-IC-010) is DEFERRED — the E.5 host is the auth-deferred dev boundary; the secured edge is Epic J (bd `babelstone-e50n`).*
