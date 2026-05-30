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

Related: this ADR's family-agnosticism is the family-level cousin of the variant-level [`ZERO_ENGINE_DIFF_PER_VARIANT`](./commitment-catalogue.md) (adding a *variant* is zero engine diff; adding a *family* is zero *generic*-engine diff). `ENGINE_FAMILY_AGNOSTIC` is now `Live` — the dependency assertion (`EngineFamilyAgnosticTests` in `Babelstone.Engine.Tests`) parses the six spine projects' `.csproj` and fails if any references `families/**`; it is promoted to the [commitment catalogue](./commitment-catalogue.md) (row 12) as the single source of truth for its status.

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
