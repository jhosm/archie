# ADR-IC-019: Notification Service — Family-Owned Contributions over a Family-Agnostic Notification Core

| Field | Value |
|---|---|
| Status | Accepted |
| Date | 2026-06-21 |
| Deciders | jhosm |
| Shape | Tool-selection ([ADR-IC-000](./ADR-IC-000-common-evaluation-criteria.md) common criteria; the residual structural/engineering-practice class — F1/F2 do not discriminate, the same class as [ADR-IC-018](./ADR-IC-018-family-owned-saga-modules.md), [ADR-PC-021](../../product_concepts/adrs/ADR-PC-021-application-layer-family-owned-deciders.md) and [ADR-PC-019](../../product_concepts/adrs/ADR-PC-019-repository-strategy-monorepo.md)) |
| Common criteria | [ADR-IC-000](./ADR-IC-000-common-evaluation-criteria.md) |
| Depends on | [ADR-IC-011](./ADR-IC-011-async-saga-completion-notification.md) (the notification service this structures — its choreography-consumer character and read-model enrichment), [ADR-IC-018](./ADR-IC-018-family-owned-saga-modules.md) (the orchestrator-side precedent this directly mirrors for the notification estate), [ADR-PC-021](../../product_concepts/adrs/ADR-PC-021-application-layer-family-owned-deciders.md) (the engine's family-agnostic precedent both inherit), [ADR-PC-027](../../product_concepts/adrs/ADR-PC-027-deposit-read-surface-canonical-resource.md) (the storage-opaque canonical read resource the core consumes), [ADR-IC-005](./ADR-IC-005-cqrs-read-model-storage.md) (the read-model storage + its Postgres→Valkey/OpenSearch/DuckDB upgrade path the read contract hides), [ADR-PC-019](../../product_concepts/adrs/ADR-PC-019-repository-strategy-monorepo.md) (the `/families` subtree + extraction-readiness the core preserves), [ADR-IC-013](./ADR-IC-013-in-house-estate-build-and-repository-placement.md) (the notification service is in-house estate co-located in this monorepo), [ADR-IC-004](./ADR-IC-004-outbox-pattern-mechanism.md) (the per-service outbox the core hosts) |
| Implemented by | bd `babelstone-60n8` (the notification service epic — the read-path relocation onto the family-agnostic read contract + the `NOTIFICATION_FAMILY_AGNOSTIC` gate that makes it stick) |

---

## In plain English

The engine has a rule it takes seriously: the generic core must not know what a *term deposit* is. Product-specific logic lives in `families/`, the core stays family-agnostic, and a build-time test (`EngineFamilyAgnosticTests`) fails if the core reaches into a family. [ADR-IC-018](./ADR-IC-018-family-owned-saga-modules.md) gave the **orchestrator** that same rule. The **notification service never got it** — and as it is stood up (bd `babelstone-60n8.1`), its first read path took the deepest possible coupling: a generic notification worker that compile-references the engine kernel *and* the term-deposit family, deserializing that family's internal projection types by hand. Add a second product family and the notification service becomes a tower of every family's read code — the same Babel Tower [ADR-IC-018](./ADR-IC-018-family-owned-saga-modules.md) removed from the orchestrator.

This ADR gives the notification service the engine's discipline. The service becomes a **family-agnostic notification core** (the worker host, the scheduler loop, the per-service outbox, the subscription + delivery stores) that reads family data over a **published, storage-opaque read contract** — never by binding the engine's internal store and a family's C# types. Anything genuinely family-specific (the disclosure a notice carries, a family's scheduling rule) becomes a **family-owned contribution** that lives with its family in `families/`, exactly like the engine's folds/deciders and the orchestrator's saga modules. A new fitness test makes it stick. Adding a family then means dropping in its contribution — never editing the notification core.

---

## Context

[ADR-IC-011](./ADR-IC-011-async-saga-completion-notification.md) established the notification service as a **dedicated choreography consumer** (its §D5): the orchestrator emits terminal domain events, the notification service subscribes and delivers, and the orchestrator is unaware of it. [ADR-IC-011 §P2](./ADR-IC-011-async-saga-completion-notification.md) sources a callback's `outcome` field "from the CQRS read model for the process (ADR-IC-005 / ADR-IC-003) — the same structured data that `get_process_status` returns." So the *principle* — a notification service that consumes events and reads a published read surface for enrichment — is already an Accepted decision. Subsequent work widened the service's remit. The engine emits **no** clock-driven temporal signal: [ADR-PC-023](../../product_concepts/adrs/ADR-PC-023-temporal-signals-projection-derived.md) (and the clean reissue [ADR-PC-025](../../product_concepts/adrs/ADR-PC-025-customer-notification-emit-contract.md) of the retired [ADR-PC-014](../../product_concepts/adrs/retired/ADR-PC-014-customer-notification-emit-contract.md)) push temporal timing **out of the engine** to a downstream customer-communications system that reads the projections, and **defer** that consumer (DEF-2, post-v1). bd `babelstone-60n8` builds that downstream scheduler **in the notification estate** — reading the term-deposit `maturity_calendar` / `accrual_schedule` / `withholding_ledger` projections over the [ADR-IC-005](./ADR-IC-005-cqrs-read-model-storage.md) read surface. Siting the deferred scheduler in this service is a notification-estate decision; this ADR governs *how* it reads, not *whether* it should exist — that consumer role is **sanctioned** and is not relitigated here.

The **implementation, as it is stood up, does not honour the family-agnostic discipline.** The notification service skeleton (bd `babelstone-60n8.1`) reads those three projections by:

1. **Compile-referencing the engine kernel.** `Babelstone.Notification` carries a `ProjectReference` to `Babelstone.Engine` and `Babelstone.EventStore` — two of the eight [ADR-PC-021 §P2](../../product_concepts/adrs/ADR-PC-021-application-layer-family-owned-deciders.md) engine-spine projects — making it the first in-house service outside the engine host to take an engine-kernel reference. (The orchestrator, by contrast, carries **no** engine kernel — [ADR-IC-018 §S3](./ADR-IC-018-family-owned-saga-modules.md) makes kernel-freedom the load-bearing extraction property.)
2. **Compile-referencing a family and naming its internal types.** It references `Babelstone.Families.TermDeposit` and, in `TermDepositProjectionReader`, names the family's concrete projection-state types (`MaturityCalendar`, `AccrualSchedule`, `WithholdingLedger`) and its `TermDepositProjectionModule.*Kind` discriminators — to deserialize the read model by hand via the kernel's `BitemporalProjectionQuery` / `JsonStateSerializer` over the byte store (`IProjectionStorage`).

This couples a generic backbone service to the exact storage tier the read contract was built to hide: [ADR-PC-027 slot 6](../../product_concepts/adrs/ADR-PC-027-deposit-read-surface-canonical-resource.md) makes the deposit read surface a **storage-opaque** resource whose stated guarantee is that "the projection technology may change (Postgres → Valkey/OpenSearch/DuckDB per [ADR-IC-005](./ADR-IC-005-cqrs-read-model-storage.md)'s upgrade path) with **zero contract change**." Binding `IProjectionStorage` + a family's JSON codec opts out of that path; and the three state types carry **no published Avro/CUE contract** — they are internal pure-fold types, so the binding couples to an unversioned internal shape. The notification estate also has **no fitness function** guarding it, unlike the engine's `EngineFamilyAgnosticTests` and the orchestrator's `OrchestratorFamilyAgnosticTests` — nothing stops family code leaking into the core.

The question this ADR answers falls out before the *second* product family's notification path is typed:

> **Where do concrete, product-family-specific notification reads and content live, and how do they relate to the generic notification core?**

This is the **integration-estate analog of the question [ADR-IC-018](./ADR-IC-018-family-owned-saga-modules.md) answered for the orchestrator and [ADR-PC-021](../../product_concepts/adrs/ADR-PC-021-application-layer-family-owned-deciders.md) for the engine.** The trigger is the same shape: a second product family would compound the leak (a `<Family>ProjectionReader` per family, each a fresh `families/**` reference). The brief's thesis is one platform, many families ([01 §1](../../product_concepts/01-product-architecture.md)); the notification service must honour it the way the engine and orchestrator already do.

This entry is the residual structural/engineering class (the same class [ADR-IC-018](./ADR-IC-018-family-owned-saga-modules.md) / [ADR-PC-021](../../product_concepts/adrs/ADR-PC-021-application-layer-family-owned-deciders.md) sit in): the honest consequence, surfaced up front, is that **F1 and F2 do not discriminate** — no tool is bought, and a source-tree boundary carries no PII and is not a DORA/PSD2 runtime artefact. The load-bearing question is *which placement keeps the notification core reusable across families while a 1–2-person, LLM-first team adds them* — settled on S1 + S2 plus the open/closed property the engine's and orchestrator's plugin models already commit to.

**Candidates evaluated** (where the family-specific notification read/content lives):

| # | Candidate | Notes |
|---|---|---|
| A | **Family-owned contribution over a family-agnostic core** — family-specific reads go through the storage-opaque read contract; any family-specific content/scheduling rule lives in a per-family `Babelstone.Families.<X>.Notification` contribution composed at the host edge. | Core never names a family or the engine kernel. Adding a family = a new contribution (or pure data over the contract), zero core diff. Mirrors the engine's `IFamilyModule` / the orchestrator's `ISagaModule` arrow exactly. |
| B | **A shared cross-family notification project** inside the service referencing every family's read types. | One composition project edited on *every* new family — the open/closed violation; reference set grows without bound. The refactor-trajectory of candidate C. |
| C | **Family-specific readers/logic inside the notification core** (`Babelstone.Notification`). | The current skeleton shape: the core names families *and* the engine kernel (`TermDepositProjectionReader`); a new family diffs the core, the precise property A preserves. |
| D | **Notification reads/logic inside the pure family fold project** (`Babelstone.Families.<X>`). | A notification read/scheduler is impure orchestration (clock, HTTP/DB I/O, outbox); putting it with the analyzer-pure folds collapses the pure/impure boundary [ADR-PC-021 §D3](../../product_concepts/adrs/ADR-PC-021-application-layer-family-owned-deciders.md) protects. |

---

## Evaluation

### Hard filter results

#### F1 · Cost / licensing

| Candidate | Licence / cost | Verdict |
|---|---|---|
| A · family-owned contribution | No tool, no licence; at most one extra project per family. | **Pass** |
| B · shared notification project | Same; one project total. | **Pass** |
| C · in-core | Same; no new project. | **Pass** |
| D · in pure family | Same; no new project. | **Pass** |

Uniform pass — F1 does not discriminate (no candidate buys anything).

#### F2 · Regulatory fit (GDPR / DORA / PSD2)

Source-tree placement of a notification read carries no PII and is not itself a regulated artefact. The regulatory-weight properties the notification path *exercises* — no PII on the durable bus ([ADR-PC-004](../../product_concepts/adrs/ADR-PC-004-pii-crypto-shredding.md)), the SELECT-only runtime credential resolved at the composition root ([ADR-PC-004 Amendment A1](../../product_concepts/adrs/ADR-PC-004-pii-crypto-shredding.md)), the at-least-once delivery + HMAC authenticity ([ADR-IC-011](./ADR-IC-011-async-saga-completion-notification.md)) — are owned by *those* ADRs and hold identically under all four placements. It is a correctness property of *how the service behaves*, not a filter a placement passes or fails.

| Candidate | GDPR | DORA / PSD2 | Verdict |
|---|---|---|---|
| A / B / C / D | No PII in source placement; delivery/credential/PII properties owned by ADR-IC-011 / ADR-PC-004, placement-invariant. | Identical under all four. | **Pass** |

All four clear the hard filters. The decision is entirely in S1 + S2 and the open/closed analysis below — the expected shape for the residual structural class.

---

### Soft criteria

#### A · Family-owned contribution over a family-agnostic core — **CHOSEN**

**S1 · Operational complexity for 1–2 people.** Lowest *over the life of the build*. Each family is self-contained — its decider, folds, projection, saga module, and now its notification specifics live in one subtree under one `CODEOWNERS` entry ([ADR-PC-019 §P2](../../product_concepts/adrs/ADR-PC-019-repository-strategy-monorepo.md)). Adding a family's notification path is additive (consume the shared read contract; drop in a contribution if it needs family-specific content) with no edit to the notification core, so the change a 1–2-person team makes most often — onboard a product — never touches the core.

**S2 · Ecosystem coherence — decisive.** The platform already commits to a **family-as-plugin** model end to end: folds are `IFamilyModule` bindings discovered by `FamilyModuleLoader`, deciders are family-owned `.Application` projects composed by the host's `IFamilyHostModule` ([ADR-PC-021 §A1](../../product_concepts/adrs/ADR-PC-021-application-layer-family-owned-deciders.md)), and sagas are family-owned `ISagaModule` contributions over a family-agnostic substrate ([ADR-IC-018 §D1](./ADR-IC-018-family-owned-saga-modules.md)). Placement A extends that *exact* commitment to the *notification* side: the core exposes generic ports + consumes a family-agnostic read contract, and the family-owned contribution supplies its specifics. The dependency arrow stays **family → core**, never the reverse — the same direction the fold, decider, and saga plugins already enforce. B and C bend that arrow back toward the core and break the coherence the rest of the platform established; both also drag the engine kernel into a backbone service, which neither the engine host nor the orchestrator does.

**S3 · Exit cost.** Low and deferred-friendly. The notification core is a per-service outbox worker ([ADR-IC-004](./ADR-IC-004-outbox-pattern-mechanism.md)) + a read-contract client; relocating the family-specific read off the kernel and onto the storage-opaque surface is a *lift*, not a rewrite, and it makes the core extraction-ready ([ADR-PC-019 §P2](../../product_concepts/adrs/ADR-PC-019-repository-strategy-monorepo.md)): a `git filter-repo` of `/notification` lifts the core cleanly because it then references neither a family nor the engine kernel — the property the current skeleton forfeits.

**S4 · Longevity.** Neutral — the core/family layering outlives any one family or notification channel (SMS/push/email are downstream of the same core, [ADR-IC-011](./ADR-IC-011-async-saga-completion-notification.md) scope boundary).

**Decisive project-specific reason — open/closed for families.** The brief's thesis is one platform, many families. The placement that honours it is the one where the *core* is closed for modification and *open* for extension by a new family — exactly A. B and C make the generic layer change every time a family is added; A makes it change never. This is the notification estate's `NOTIFICATION_FAMILY_AGNOSTIC`, the cousin of the engine's `ENGINE_FAMILY_AGNOSTIC` and the orchestrator's `ORCHESTRATOR_FAMILY_AGNOSTIC`.

#### B · Shared cross-family notification project — **rejected**

A single project that references every family's read types is edited on every new family — the open/closed violation the whole plugin model exists to avoid. Its reference set grows without bound, and a context-bounded LLM author must hold the whole cross-family project to add one family. It is the refactor-destination of the current per-family-reader trajectory, and it reproduces the precise "reference set grows without bound" smell [ADR-PC-021 §D1](../../product_concepts/adrs/ADR-PC-021-application-layer-family-owned-deciders.md) and [ADR-IC-018 §B](./ADR-IC-018-family-owned-saga-modules.md) reject. Rejected on S1 + S2.

#### C · Family-specific readers/logic in the core — **rejected**

The current skeleton shape. Putting a `TermDepositProjectionReader` in `Babelstone.Notification` forces the core to name families (the family's projection-state types, its `*Kind` discriminators) *and* the engine kernel (`Babelstone.Engine` / `Babelstone.EventStore`, to fold the byte store) — so a new family diffs the core, the precise property A preserves, and the core couples to the storage tier [ADR-PC-027 slot 6](../../product_concepts/adrs/ADR-PC-027-deposit-read-surface-canonical-resource.md) hides. It also inverts the platform-established `family → core` arrow and forfeits extraction-readiness. Rejected on S2 (loses family-agnosticism).

#### D · Notification reads/logic in the pure family fold project — **rejected**

The pure-fold project (`Babelstone.Families.<X>`) references only `Babelstone.Engine` + `Babelstone.FinancialTypes` so a fold *structurally cannot* reach a DB or the kernel (the analyzer-backed purity guarantee, [ADR-PC-021 §D3](../../product_concepts/adrs/ADR-PC-021-application-layer-family-owned-deciders.md)). A notification read/scheduler is impure orchestration — it reads the clock, performs HTTP/DB I/O, and writes the outbox — so putting it there drags impure deps onto the pure-fold project, dissolving the same guarantee. The notification contribution is the orchestration-side sibling of the `.Application` decider and the `.Orchestration` saga module, not of the pure folds. Rejected on S2.

**Decisive reason for A over B/C/D:** the notification core is the reusable asset, the team is 1–2 people adding families over time, and the platform already commits to a `family → core` plugin arrow on the fold, decider, and saga sides — all four point to a family-owned notification contribution that leaves the core untouched. B/C bend the arrow back and pull the engine kernel into a backbone service; D collapses the purity boundary.

---

## Decision

### Family-specific notification reads and content are family-owned; the notification service is a family-agnostic core that reads a storage-opaque contract.

- **D1 — Family-owned notification contributions.** Anything family-specific a notification needs — the disclosure/content a family's notice carries, a family-specific scheduling rule (e.g. term-deposit maturity timing), and the read shape it requires — is family-owned: it is consumed as pure data over the family-agnostic read contract (D3), or, where genuine family-specific *code* is needed, it lives in a per-family `Babelstone.Families.<X>.Notification` contribution under the family subtree. There is **no** shared cross-family notification project, and **no** family-specific reader/logic in the generic core.
- **D2 — Family-agnostic notification core.** The notification core — the worker host shell, the scheduler loop, the per-service outbox ([ADR-IC-004](./ADR-IC-004-outbox-pattern-mechanism.md)), the subscription + delivery stores, the delivery client — carries **no `ProjectReference` to any `families/**` project, and no reference to the engine kernel** (`Babelstone.Engine` / `Babelstone.EventStore` or any other [ADR-PC-021 §P2](../../product_concepts/adrs/ADR-PC-021-application-layer-family-owned-deciders.md) spine project). The dependency arrow is **family → core**, never core → family, and never core → engine-kernel. Adding a family is **zero core diff** — the engine's `FamilyModuleLoader` plugin model, extended from folds, deciders, and sagas to notification.
- **D3 — Family read data is consumed over the storage-opaque read contract, never the engine kernel.** The core obtains family-specific read data through the [ADR-PC-027](../../product_concepts/adrs/ADR-PC-027-deposit-read-surface-canonical-resource.md) canonical read resource — a **storage-opaque** surface ([ADR-PC-027 slot 6](../../product_concepts/adrs/ADR-PC-027-deposit-read-surface-canonical-resource.md): the URL names the resource, not the storage; [ADR-IC-005](./ADR-IC-005-cqrs-read-model-storage.md) owns the storage-paradigm upgrade path that opacity hides) — or another family-agnostic, family-owned read contract. It does **not** bind the engine kernel's byte store (`IProjectionStorage` / `BitemporalProjectionQuery` / `JsonStateSerializer`) to a family's internal projection-state types and `*Kind` discriminators. This is the notification analog of [ADR-IC-018 §D5](./ADR-IC-018-family-owned-saga-modules.md) (the saga substrate reads CloudEvents *headers*, never an Avro *payload*): the core reads a **published contract**, never an internal storage shape. It preserves the Postgres→Valkey/OpenSearch/DuckDB migration with zero contract change ([ADR-PC-027 slot 6](../../product_concepts/adrs/ADR-PC-027-deposit-read-surface-canonical-resource.md)) and avoids coupling to unversioned internal pure-fold types. Where the canonical resource does not yet expose the per-entry temporal detail a scheduler needs, it is extended **additively** (slot 6's additive-evolution rule), never bypassed by reaching into engine internals.
- **D4 — Composition at the host edge; the host MAY name a family, the core MAY NOT.** Which families' notification contributions a deployment runs is the notification **host/composition-root's** job, via discovery — the notification-side mirror of [ADR-PC-021 §A1](../../product_concepts/adrs/ADR-PC-021-application-layer-family-owned-deciders.md)'s `IFamilyHostModule` and [ADR-IC-018 §D4](./ADR-IC-018-family-owned-saga-modules.md)'s `ISagaModule`. The host composition assembly *may* carry a `ProjectReference` into `families/**` (the standing, intended exemption — [ADR-PC-021 §A2](../../product_concepts/adrs/ADR-PC-021-application-layer-family-owned-deciders.md)'s pattern); the core libraries *may not*. Generic code never references a family to compose it. The §A2 exemption is for **composition**, not for the core's own read/deserialization logic — the conflation the current skeleton's csproj makes.
- **D5 — Scope: the layering + the gate; realised by relocating the family read out of the core.** This ADR commits to D1–D4 and the fitness gate (Verifiable commitments). The first realisation relocates the in-flight skeleton's family-specific read — the `TermDepositProjectionReader` and the `Babelstone.Engine` / `Babelstone.EventStore` / `Babelstone.Families.TermDeposit` references introduced by bd `babelstone-60n8.1` — onto the family-agnostic read contract (D3), so the maturity scheduler (bd `babelstone-60n8.2`) and the emission contract (bd `babelstone-60n8.3`) are built on the agnostic core, never on the engine kernel. The **explicit-list-now, assembly-scan-later** discovery posture ([ADR-PC-021 §A3](../../product_concepts/adrs/ADR-PC-021-application-layer-family-owned-deciders.md)) carries over to any family-notification contributions: the host holds an explicit list at the current family count, swappable for assembly-scan with zero family change.

**Rejected: a shared cross-family notification project** — an open/closed violation edited on every family (the refactor-destination of today's per-family reader). **Rejected: family readers/logic in the core** — the core would name families *and* the engine kernel and diff per family (today's skeleton shape). **Rejected: notification reads/logic in the pure family fold project** — dissolves the fold-purity guarantee by dragging impure orchestration onto it.

---

## Implementation Principles

### P1 — Project topology

```
notification/src/
  Babelstone.Notification[.Core]/      the family-agnostic notification core
      worker host shell, scheduler loop, per-service outbox (ADR-IC-004),
      subscription + delivery stores, the read-contract client
      refs: the read-contract client surface, Npgsql, Babelstone.Telemetry
            (NO families/**, NO engine kernel — Babelstone.Engine / Babelstone.EventStore)
  Babelstone.Notification[.Host]/      the host / composition root
      refs: the core, families/**/*.Notification   (MAY name a family — §D4)

families/term-deposit/src/
  Babelstone.Families.TermDeposit.Notification/   (only if family-specific CODE is needed)
      the family's notice content + scheduling-rule contribution
      refs: the notification core's contribution port   (family → core; never the engine kernel)
```

The precise split of the current single `Babelstone.Notification` project into a core library + a host (and whether a family needs a `.Notification` contribution at all, vs. pure data over the read contract) is an implementation detail of bd `babelstone-60n8`; the load-bearing invariant is §P2 — *some* enumerated set of core projects carries no `families/**` and no engine-kernel reference, and the host is excluded from that set.

### P2 — The core→family and core→engine-kernel edges are forbidden

No project in the notification core (the §P1 core set) may carry a `ProjectReference` to a `families/**` project, nor to an [ADR-PC-021 §P2](../../product_concepts/adrs/ADR-PC-021-application-layer-family-owned-deciders.md) engine-spine project (`Babelstone.Engine`, `Babelstone.EventStore`, …). The arrow is one-way: **family → core**, and the core reaches the engine only across the network read contract (D3), never by a compile-time kernel reference. This is the gateable invariant (see Verifiable commitments) — the notification-side twin of [ADR-PC-021 §P2](../../product_concepts/adrs/ADR-PC-021-application-layer-family-owned-deciders.md)'s `ENGINE_FAMILY_AGNOSTIC` and [ADR-IC-018 §P2](./ADR-IC-018-family-owned-saga-modules.md)'s `ORCHESTRATOR_FAMILY_AGNOSTIC`. The notification **host** (the §D4 composition root) is **not** a core project and therefore *may* reference `families/**` — the standing, intended exemption ([ADR-PC-021 §A2](../../product_concepts/adrs/ADR-PC-021-application-layer-family-owned-deciders.md)'s pattern) — so the host can compose contributions.

### P3 — Reads are over a published contract; the core never decodes an internal store shape

The core reads family data through the storage-opaque read contract (D3) — the [ADR-PC-027](../../product_concepts/adrs/ADR-PC-027-deposit-read-surface-canonical-resource.md) canonical resource by default — never by folding the engine's byte store with a family's JSON codec and projection-state types. This is the read-side half of §P2: binding an internal storage shape is the same family/kernel coupling a project reference is, one level down (the engine could swap Postgres for Valkey/OpenSearch/DuckDB under [ADR-IC-005](./ADR-IC-005-cqrs-read-model-storage.md) and the core must not break). When the canonical resource lacks a field the scheduler needs, the resource is extended additively ([ADR-PC-027 slot 6](../../product_concepts/adrs/ADR-PC-027-deposit-read-surface-canonical-resource.md)); the SELECT-only runtime credential and the [ADR-PC-004 Amendment A1](../../product_concepts/adrs/ADR-PC-004-pii-crypto-shredding.md) composition-root boundary are unchanged.

### P4 — Composition is discovery at the host edge

Each family that needs family-specific notification *code* contributes it at the host edge, mirroring `FamilyModuleLoader` / `IFamilyHostModule` / `ISagaModule`. The host composes by looping over the contributions; the host holds an explicit list now ([ADR-PC-021 §A3](../../product_concepts/adrs/ADR-PC-021-application-layer-family-owned-deciders.md)), swappable for assembly-scan later with zero family change. A family whose notification needs are fully satisfied by data over the read contract contributes **nothing** — the lightest case, and the default.

### P5 — The first realisation is a behavior-preserving relocation

Relocating the family read off the engine kernel (bd `babelstone-60n8`) must not change the data the service reads — the same maturity/accrual/withholding facts, now over the storage-opaque contract instead of the byte store. It is a relocation + the read-contract client, not a logic change; the downstream scheduler (bd `babelstone-60n8.2`) and emission contract (bd `babelstone-60n8.3`) are then built on the agnostic core.

---

## Consequences

**What this choice makes easier:**

1. **Open/closed for families.** Adding a product family's notification path is additive — data over the read contract, or a new contribution — with zero core diff. The change a small team makes most often never touches the notification service.
2. **A reusable, extraction-ready core.** The notification service stays a generic outbox-worker + read-contract-client that a second deployment (or a second jurisdiction's families) reuses unchanged, and that `git filter-repo` lifts cleanly ([ADR-PC-019 §P2](../../product_concepts/adrs/ADR-PC-019-repository-strategy-monorepo.md)) — the property the current kernel-coupled skeleton forfeits.
3. **Symmetry across the platform.** A family now ships its engine module, its saga module, and (when needed) its notification contribution side by side under one `CODEOWNERS` entry — one mental model across the whole build (engine, orchestrator, notification).
4. **The read surface stays migratable.** Because the core reads the storage-opaque contract, the [ADR-IC-005](./ADR-IC-005-cqrs-read-model-storage.md) Postgres→Valkey/OpenSearch/DuckDB upgrade path survives with zero notification change ([ADR-PC-027 slot 6](../../product_concepts/adrs/ADR-PC-027-deposit-read-surface-canonical-resource.md)).
5. **The boundary is gated, not just stated.** A new fitness function turns the family-agnostic rule into a mechanical check for the notification estate, closing the one platform service that had none.

**What this choice makes harder or impossible:**

1. **A read-path relocation lands before the scheduler feature.** Moving the family read off the engine kernel onto the read contract is real work that ships no user-visible feature, and it may require an additive field on the canonical resource (the flat `DepositResponse` is summary-only today; per-entry maturity/accrual/withholding detail is an additive [ADR-PC-027 slot 6](../../product_concepts/adrs/ADR-PC-027-deposit-read-surface-canonical-resource.md) extension). Mitigation: it is behavior-preserving (the same facts, a different source), it is the one-time cost that makes every subsequent family's notification path cheap, and it removes debt the second family would otherwise compound.
2. **An HTTP/contract hop replaces an in-process read.** Reading the contract adds a network hop the in-process kernel binding avoided. Mitigation: the latency is bounded and the read is not on a user's synchronous path (the notification service is a background worker); if a future need genuinely requires lower-latency or richer-than-contract access, it is met by extending the contract or adding a family contribution behind the seam — never by re-introducing the engine-kernel reference (which would re-trip the gate and re-couple the storage tier).

**Residual risks:**

- **The core→family / core→kernel no-edge rule is a convention until gated.** A stray `ProjectReference` from the core to a family or the engine kernel would silently erode family-agnosticism. Mitigation: the `NOTIFICATION_FAMILY_AGNOSTIC` fitness function below makes it a mechanical check (it lands `Live` with the bd `babelstone-60n8` relocation; until then it is a deliberate, listed `Planned` gap).
- **The read contract may lag a scheduler need.** A family-specific datum the scheduler needs may not be on the canonical resource yet. Mitigation: §P3's additive-extension rule ([ADR-PC-027 slot 6](../../product_concepts/adrs/ADR-PC-027-deposit-read-surface-canonical-resource.md)) — extend the published contract, never reach past it into engine internals.

---

## Verifiable commitments

This ADR's load-bearing commitment lives in the [commitment catalogue](../../product_concepts/adrs/commitment-catalogue.md) — the single source of truth for its claim, gate (pyramid level), and `Live`/`Planned`/`Gap` status (the one-way ADR→catalogue reference rule, [ADR-PC-020 §P5–§P7](../../product_concepts/adrs/ADR-PC-020-llm-toolchain-and-conformance-governance.md)):

- `NOTIFICATION_FAMILY_AGNOSTIC` (§D2/§P2) — the notification **core** carries no `ProjectReference` to any `families/**` project nor to an engine-spine project; the host composition root is the standing [ADR-PC-021 §A2](../../product_concepts/adrs/ADR-PC-021-application-layer-family-owned-deciders.md) exemption. The notification-estate cousin of `ENGINE_FAMILY_AGNOSTIC` (row 12) and `ORCHESTRATOR_FAMILY_AGNOSTIC` (ORCH-1). Catalogue row **NOTIF-1**, **`Planned`** — a deliberate, visible gap: the gate lands `Live` with the bd `babelstone-60n8` read-path relocation (today's skeleton would fail it by construction, which is the point).

---

## Cross-references

- **Structures:** [ADR-IC-011](./ADR-IC-011-async-saga-completion-notification.md) (the notification service — its choreography-consumer character and read-model enrichment; this ADR adds the internal family-agnostic discipline IC-011 never named, honouring it, contradicting no clause).
- **Mirrors:** [ADR-IC-018](./ADR-IC-018-family-owned-saga-modules.md) (family-owned saga modules over a family-agnostic substrate) — this is its notification-estate twin; [ADR-PC-021](../../product_concepts/adrs/ADR-PC-021-application-layer-family-owned-deciders.md) (family-owned deciders + `IFamilyHostModule` over a family-agnostic engine, and the §A2 host-vs-spine exemption this applies to the notification core/host split); [ADR-PC-019](../../product_concepts/adrs/ADR-PC-019-repository-strategy-monorepo.md) (the `/families` subtree + extraction-readiness).
- **Consumes:** [ADR-PC-027](../../product_concepts/adrs/ADR-PC-027-deposit-read-surface-canonical-resource.md) (the storage-opaque canonical read resource the core reads — its consumer set grows by one, additively); [ADR-IC-005](./ADR-IC-005-cqrs-read-model-storage.md) (the read-model storage + the upgrade path the contract hides).
- **Realised by:** bd `babelstone-60n8` (the notification service epic — the read-path relocation onto the family-agnostic read contract + the `NOTIFICATION_FAMILY_AGNOSTIC` gate).
- **Supports docs:** [11 Chat-Agent Channel Strategy](../11-chat-agent-channel-strategy.md) (the out-of-band notification patterns the service realises).
