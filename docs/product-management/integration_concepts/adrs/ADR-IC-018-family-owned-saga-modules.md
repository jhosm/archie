# ADR-IC-018: Saga Orchestrator — Family-Owned Saga Modules over a Family-Agnostic Substrate

| Field | Value |
|---|---|
| Status | Accepted |
| Date | 2026-06-16 |
| Deciders | jhosm |
| Shape | Tool-selection ([ADR-IC-000](./ADR-IC-000-common-evaluation-criteria.md) common criteria; the residual structural/engineering-practice class — F1/F2 do not discriminate, the same class as [ADR-PC-021](../../product_concepts/adrs/ADR-PC-021-application-layer-family-owned-deciders.md) and [ADR-PC-019](../../product_concepts/adrs/ADR-PC-019-repository-strategy-monorepo.md)) |
| Common criteria | [ADR-IC-000](./ADR-IC-000-common-evaluation-criteria.md) |
| Depends on | [ADR-IC-003](./ADR-IC-003-saga-orchestrator.md) (the custom saga orchestrator + §P3 "do not let saga-specific concerns leak into shared infrastructure" — the discipline this operationalises), [ADR-PC-021](../../product_concepts/adrs/ADR-PC-021-application-layer-family-owned-deciders.md) (the engine's family-owned-composition precedent this mirrors for the orchestrator), [ADR-PC-019](../../product_concepts/adrs/ADR-PC-019-repository-strategy-monorepo.md) (the `/families` subtree + extraction-readiness the substrate preserves), [ADR-IC-013](./ADR-IC-013-in-house-estate-build-and-repository-placement.md) (the orchestrator is in-house estate co-located in this monorepo) |
| Implemented by | bd `babelstone-t7o3.12` (this ADR + the behavior-preserving extraction of `ConstitutionProcess`); bd `babelstone-mtto` (the renewal saga, born as a family saga module on this substrate) |

---

## In plain English

The engine has a rule it takes seriously: the generic core must not know what a *term deposit* is. Product-specific logic lives in `families/`, the core stays family-agnostic, and a build-time test (`EngineFamilyAgnosticTests`) fails if the core ever reaches into a family. The **orchestrator never got the same rule.** Its one concrete saga — `ConstitutionProcess`, which is entirely about term-deposit constitution — lives *inside* the orchestrator, and there is a single central `SagaState` enum that holds every saga's states. Add a second saga (the renewal saga) and a third (when a new product family arrives) and `Babelstone.Orchestrator` becomes a tower of every family's saga code — a Babel Tower.

This ADR gives the orchestrator the engine's discipline. The orchestrator becomes a **family-agnostic saga substrate** (the runtime, the stores, the state-machine abstractions, and a new plug-in contract), and each concrete saga becomes a **family-owned module** that lives with its family in `families/`, exactly like the engine's deciders and folds do. A new fitness test makes it stick. Adding a family then means dropping in its saga module — never editing the orchestrator. This honors a rule [ADR-IC-003 §P3](./ADR-IC-003-saga-orchestrator.md) already wrote down but the implementation never enforced.

---

## Context

[ADR-IC-003](./ADR-IC-003-saga-orchestrator.md) chose a hand-rolled, Redpanda-consumer saga orchestrator over Temporal/Conductor/Axon, and — anticipating exactly this risk — wrote the discipline into §P3:

> *"Do not let saga-specific concerns leak into shared infrastructure, and do not copy the shared infrastructure into each saga. These are the only two failure modes."*

§P3 names *"the state enumeration and valid transition table for each saga type"* as a **per-saga** concern, distinct from the shared substrate. So the *principle* — a generic substrate, per-saga state machines that plug into it — is already an Accepted decision.

The **implementation does not yet realise it.** bd `babelstone-mtto` PR1 built the multi-saga *runtime* substrate (the advance handler, consume loop, and dispatcher host N sagas keyed by `saga_type`), which made the substrate generic at *runtime*. But two §P3 leaks remain at the *structure* and *type* level:

1. **The concrete saga lives in the substrate project.** `ConstitutionProcess` (and its `ConstitutionResultEvents` bridge, `SagaCommandRouter`, and command DTOs) — all term-deposit-constitution-specific — live in `orchestrator/src/Babelstone.Orchestrator/Saga/` and are registered directly in the orchestrator's own `Program.cs`. Structurally, `Babelstone.Orchestrator` *is* the term-deposit constitution saga.
2. **`SagaState` is one central enum.** `orchestrator/src/Babelstone.Orchestrator/Saga/SagaState.cs` holds *every* saga's states (`Started`, `ParallelValidation`, … and the renewal saga would bolt `RenewalStarted`, `RenewalConstituting`, `RenewalLinking` on). A shared enum that every saga's vocabulary pollutes is the §P3 leak in its purest form.

There is also **no fitness function** guarding the substrate — nothing stops family code leaking in, unlike the engine's `EngineFamilyAgnosticTests`.

The question this ADR answers falls out before the *second* saga is typed:

> **Where do concrete, product-family-specific sagas live, and how do they relate to the generic orchestrator substrate?**

This is the **integration-estate analog of the question [ADR-PC-021](../../product_concepts/adrs/ADR-PC-021-application-layer-family-owned-deciders.md) answered for the engine** ("where does the family-specific decider live, vs the family-agnostic engine?"). The trigger is the same shape: adding the renewal saga (bd `babelstone-mtto`) — and, beyond it, *every* new product family — would compound the leak. The brief's thesis is one substrate, many families ([01 §1](../../product_concepts/01-product-architecture.md)); the orchestrator must honor it the way the engine already does.

This entry is the residual structural/engineering class (the same class [ADR-PC-021](../../product_concepts/adrs/ADR-PC-021-application-layer-family-owned-deciders.md) and [ADR-PC-019](../../product_concepts/adrs/ADR-PC-019-repository-strategy-monorepo.md) sit in): the honest consequence, surfaced up front, is that **F1 and F2 do not discriminate** — no tool is bought, and a source-tree boundary carries no PII and is not a DORA/PSD2 runtime artefact. The load-bearing question is *which placement keeps the orchestrator reusable across families while a 1–2-person, LLM-first team adds them* — settled on S1 + S2 plus the open/closed property the engine's plugin model already commits to.

**Candidates evaluated** (where the concrete saga lives):

| # | Candidate | Notes |
|---|---|---|
| A | **Per-family saga module** — `families/<X>/.../Orchestration`, beside the family's deciders/folds; depends on the generic orchestrator substrate's ports. | Substrate never names a family. Adding a saga/family = a new module, zero substrate diff. Mirrors the engine's `IFamilyModule` arrow exactly. |
| B | **A shared cross-family saga project** inside the orchestrator referencing every family's sagas. | One composition project edited on *every* new saga — the open/closed violation; reference set grows without bound. This is the *de-facto* status quo (all sagas in `Babelstone.Orchestrator`). |
| C | **Concrete sagas inside the generic substrate** (`Babelstone.Orchestrator`). | The substrate names families (the central `SagaState` enum, the in-project `ConstitutionProcess`); a new family diffs the substrate — the precise property A preserves. |
| D | **Sagas inside the pure family fold project** (`Babelstone.Families.<X>`). | A saga is impure orchestration (it drives commands, reads consume offsets, persists state); putting it with the analyzer-pure folds collapses the same pure/impure boundary [ADR-PC-021](../../product_concepts/adrs/ADR-PC-021-application-layer-family-owned-deciders.md) §D3 protects. |

---

## Evaluation

### Hard filter results

#### F1 · Cost / licensing

| Candidate | Licence / cost | Verdict |
|---|---|---|
| A · per-family saga module | No tool, no licence; one extra project per family. | **Pass** |
| B · shared saga project | Same; one project total. | **Pass** |
| C · in-substrate | Same; no new project. | **Pass** |
| D · in pure family | Same; no new project. | **Pass** |

Uniform pass — F1 does not discriminate (no candidate buys anything).

#### F2 · Regulatory fit (GDPR / DORA / PSD2)

Source-tree placement of a saga carries no PII and is not itself a regulated artefact. The regulatory-weight properties a saga *exercises* — the reversibility ordering before the irreversible Core debit ([ADR-IC-003](./ADR-IC-003-saga-orchestrator.md) §P5), compensation-not-rollback (§P6), no-PII-on-the-bus ([ADR-PC-004](../../product_concepts/adrs/ADR-PC-004-pii-crypto-shredding.md)) — are owned by *those* ADRs and hold identically under all four placements. It is a correctness property of *how the saga behaves*, not a filter a placement passes or fails.

| Candidate | GDPR | DORA / PSD2 | Verdict |
|---|---|---|---|
| A / B / C / D | No PII in source placement; saga-flow regulatory properties owned by ADR-IC-003 / ADR-PC-004, placement-invariant. | Identical under all four. | **Pass** |

All four clear the hard filters. The decision is entirely in S1 + S2 and the open/closed analysis below — the expected shape for the residual structural class.

---

### Soft criteria

#### A · Per-family saga module — **CHOSEN**

**S1 · Operational complexity for 1–2 people.** Lowest *over the life of the build*. Each family is self-contained — its decider, folds, projection, and now its saga(s) live in one subtree under one `CODEOWNERS` entry ([ADR-PC-019 §P2](../../product_concepts/adrs/ADR-PC-019-repository-strategy-monorepo.md)). Adding a family's saga is additive (drop in a module) with no edit to the orchestrator, so the change a 1–2-person team makes most often — onboard a product flow — never touches the substrate.

**S2 · Ecosystem coherence — decisive.** The engine already commits to a **family-as-plugin** model end to end: folds are `IFamilyModule` bindings discovered by `FamilyModuleLoader` ([ADR-PC-021](../../product_concepts/adrs/ADR-PC-021-application-layer-family-owned-deciders.md) §S2), and deciders are family-owned `.Application` projects composed by the host's `IFamilyHostModule` (ADR-PC-021 §A1). Placement A extends that *exact* commitment to the *saga* side: the substrate exposes generic ports (`ISagaStateMachine`/`TableStateMachine`, `IResultEventBridge`, `ISagaCommandRouter`, the new `ISagaModule`, and the runtime), and the family-owned saga module consumes them. The dependency arrow stays **family → substrate**, never the reverse — the same direction the engine's fold and decider plugins already enforce. B and C bend that arrow back toward the substrate and break the coherence the rest of the build established.

**S3 · Exit cost.** Low and deferred-friendly. The substrate ports (`ISagaStateMachine`, the result-event bridge, the command router, the consume loop) already exist from bd `babelstone-mtto` PR1; this ADR relocates the concrete sagas and adds the `ISagaModule` composition seam, so it is a *lift*, not a rewrite. The substrate stays extraction-ready ([ADR-PC-019](../../product_concepts/adrs/ADR-PC-019-repository-strategy-monorepo.md) §P2): a `git filter-repo` of `/orchestrator` lifts the substrate cleanly because it references neither a family nor the engine kernel.

**S4 · Longevity.** Neutral — the substrate/family layering outlives any one saga or family.

**Decisive project-specific reason — open/closed for families.** The brief's thesis is one substrate, many families. The placement that honours it is the one where the *substrate* is closed for modification and *open* for extension by a new family saga — exactly A. B and C make the generic layer change every time a saga is added; A makes it change never. This is the orchestrator's `ZERO_SUBSTRATE_DIFF_PER_SAGA`, the cousin of the engine's `ENGINE_FAMILY_AGNOSTIC`.

#### B · Shared cross-family saga project — **rejected**

A single project that references every family's sagas is edited on every new saga — the open/closed violation the whole plugin model exists to avoid. Its reference set grows without bound, and a context-bounded LLM author must hold the whole cross-family project to add one saga. This is the *current de-facto state* (`ConstitutionProcess` in `Babelstone.Orchestrator`), and the smell this ADR removes. Rejected on S1 + S2.

#### C · Sagas in the generic substrate — **rejected**

Putting concrete sagas in the substrate forces it to name families (the central `SagaState` enum holding every saga's states; the in-project `ConstitutionProcess`) — so a new family diffs the substrate, the precise property A preserves. It also inverts the engine-established `family → substrate` arrow. Rejected on S2 (loses family-agnosticism).

#### D · Sagas in the pure family fold project — **rejected**

The pure-fold project (`Babelstone.Families.<X>`) references only `Babelstone.Engine` + `Babelstone.FinancialTypes` so a fold *structurally cannot* reach a DB or the kernel (the analyzer-backed purity guarantee, [ADR-PC-021](../../product_concepts/adrs/ADR-PC-021-application-layer-family-owned-deciders.md) §D3). A saga is impure orchestration — it drives commands over HTTP, reads consume offsets, and persists state in PostgreSQL — so putting it there drags impure deps onto the pure-fold project, dissolving the same guarantee. The saga module is the orchestration-side sibling of the `.Application` decider, not of the pure folds. Rejected on S2.

**Decisive reason for A over B/C/D:** the substrate is the reusable asset, the team is 1–2 people adding families over time, and the engine already commits to a `family → substrate` plugin arrow on both the fold and decider sides — all three point to a family-owned saga module that leaves the substrate untouched. B/C bend the arrow back; D collapses the purity boundary.

---

## Decision

### Concrete sagas are per-family modules; the orchestrator is a family-agnostic substrate.

- **D1 — Family-owned saga modules.** A concrete saga — its state machine (`ISagaStateMachine`/`TableStateMachine` subclass), its result-event bridge (`IResultEventBridge`), its command router (`ISagaCommandRouter`), its command payload DTOs, and its **state vocabulary** — lives in a per-family `Babelstone.Families.<X>.Orchestration` project under the family subtree. There is **no** shared cross-family saga project, and concrete sagas are **not** in the generic substrate.
- **D2 — Family-agnostic substrate.** Concrete sagas depend on generic substrate ports — `ISagaStateMachine`/`TableStateMachine`, `IResultEventBridge`, `ISagaCommandRouter`, the new `ISagaModule` plug-in, and the runtime (the advance handler, consume loop, dispatcher, and the `saga_state`/`saga_transition`/`saga_outbox` stores). The dependency arrow is **family → substrate**, never substrate → family. Adding a saga/family is **zero substrate diff** — the engine's `FamilyModuleLoader` plugin model, extended from folds and deciders to sagas.
- **D3 — States are family-owned, not a central enum.** The central `SagaState` enum is **dissolved**: each saga declares its own state vocabulary as string constants in its module (the `saga_state.state` column is already a string, so this is a type-level change, not a schema one). The substrate treats a saga's state as an **opaque string**; the saga's `TableStateMachine` is the sole authority on its `(state, event) → (next_state, commands)` function over its own states. `IsTerminal` is per-machine (already the substrate contract since bd `babelstone-mtto` PR1) — the substrate asks the routed machine, never a central per-saga predicate.
- **D4 — Composition at the host edge; the host MAY name a family, the substrate MAY NOT.** Which sagas a deployment runs, their consumer-group wiring, and their auto-start rules are the orchestrator **host/composition-root's** job, via discovery — a new `ISagaModule` contract, the saga-side mirror of [ADR-PC-021](../../product_concepts/adrs/ADR-PC-021-application-layer-family-owned-deciders.md) §A1's `IFamilyHostModule`. The host composition assembly *may* carry a `ProjectReference` into `families/**` (the standing, intended exemption — ADR-PC-021 §A2's pattern); the substrate libraries *may not*. Generic code never references a family to compose it.
- **D5 — Auto-start filters are header-keyed; the substrate never decodes a payload.** A saga module declares its start event and any auto-start filter as a **CloudEvents-header** predicate the substrate evaluates generically (e.g. "start on `DepositMatured` when the `ce_autorenewalpolicy` extension header ≠ `NONE`"). The substrate reads CloudEvents headers only — never an Avro payload — preserving the extraction-ready property [ADR-IC-003](./ADR-IC-003-saga-orchestrator.md) (2026-06-15 amendment) and [ADR-PC-019](../../product_concepts/adrs/ADR-PC-019-repository-strategy-monorepo.md) §P2 commit to. A structural discriminator a header-only consumer needs to route on is promoted to a CloudEvents extension attribute by the engine's outbox relay via a generic, family-agnostic seam (the value the module's filter reads).
- **D6 — Scope: the layering + the plug-in contract + the gate; realised first by extracting `ConstitutionProcess`.** This ADR commits to D1–D5 and the fitness gate (Verifiable commitments). The first realisation is a **behavior-preserving** extraction of `ConstitutionProcess` (+ its bridge/router/commands/states) into a `Babelstone.Families.TermDeposit.Orchestration` module (bd `babelstone-t7o3.12`); the renewal saga (bd `babelstone-mtto`) is then born as a family saga module, never an orchestrator edit. It deliberately does **not** freeze every cross-saga helper on one example: the **explicit-list-now, assembly-scan-later** discovery posture (ADR-PC-021 §A3) carries over — the host holds an explicit module list at the current saga count, swappable for assembly-scan with zero family change.

**Rejected: a shared cross-family saga project** — an open/closed violation edited on every saga (today's de-facto state). **Rejected: sagas in the substrate** — the substrate would name families and diff per family. **Rejected: sagas in the pure family fold project** — dissolves the fold-purity guarantee by dragging impure orchestration onto it.

*Revised 2026-07-02: §D6's anticipated "assembly-scan later" landed under [ADR-PC-040](../../product_concepts/adrs/ADR-PC-040-family-agnostic-substrate-covenant.md) (the cross-cutting covenant this Decision is an instance of): the host's explicit module list is replaced by `SagaModuleLoader` discovery over the shared `FamilyModuleScanner` (`Babelstone.Composition`; initially landed in `Babelstone.Cadence`, relocated 2026-07-03) — family `ISagaModule`s are found by the `Babelstone.Families.` assembly-scan and activated through their `(SagaModuleContext)` constructor, with zero family change, exactly as §D6 promised. Additively, `ISagaModule` gains a defaulted `FamilyIntegrationTopics` declaration (each family module answers it from its catalogue-generated `FamilyIntegrationTopics.All`) so the substrate-owned settlement saga's Movement-bearing subscribe set is derived from the DISCOVERED modules rather than supplied by host code that names a family. The host `Program.cs` now names no `Babelstone.Families.*` type and is gated by `COMPOSITION_ROOT_NAMES_NO_FAMILY`; §D4's "the host MAY name a family" survives as the `<BabelstoneRole>CompositionRoot</BabelstoneRole>` `ProjectReference` exemption (the scan anchor) plus the family-specific edge adapter files, per ADR-PC-040 §D3. D1–D6 remain binding as written.*

---

## Implementation Principles

### P1 — Project topology

```
orchestrator/src/
  Babelstone.Orchestrator.Substrate/   the family-agnostic saga substrate
      ISagaStateMachine, TableStateMachine, IResultEventBridge, ISagaCommandRouter,
      ISagaModule, the advance handler, consume loop, dispatcher, saga stores
      refs: Confluent.Kafka, Npgsql        (no family, no engine kernel)
  Babelstone.Orchestrator/             the host / composition root
      refs: Babelstone.Orchestrator.Substrate, families/**/*.Orchestration   (MAY name a family — §D4)

families/term-deposit/src/
  Babelstone.Families.TermDeposit.Orchestration/   ConstitutionProcess, RenewalProcess,
      their bridges, routers, command DTOs, state constants, and the ISagaModule
      refs: Babelstone.Orchestrator.Substrate        (family → substrate; never the engine kernel)
```

The precise split of the existing `Babelstone.Orchestrator` into a `…Substrate` library + a host is an implementation detail of bd `babelstone-t7o3.12`; the load-bearing invariant is §P2 — *some* enumerated set of substrate projects carries no `families/**` reference, and the host is excluded from that set.

### P2 — The substrate→family edge is forbidden

No project in the orchestrator substrate (the §P1 `Babelstone.Orchestrator.Substrate` set) may carry a `ProjectReference` to a `families/**` project. The arrow is one-way: **family → substrate**. This is the gateable invariant (see Verifiable commitments) — the saga-side twin of [ADR-PC-021](../../product_concepts/adrs/ADR-PC-021-application-layer-family-owned-deciders.md) §P2's `ENGINE_FAMILY_AGNOSTIC`. The orchestrator **host** (`Babelstone.Orchestrator`, the §D4 composition root) is **not** a substrate project and therefore *may* reference `families/**` — the standing, intended exemption (ADR-PC-021 §A2's pattern), so the host can compose the modules.

### P3 — States are family-owned strings; no central enum

A saga's states are constants in its module, not members of a shared enum. The substrate persists and compares `saga_state.state` as an opaque string; only the saga's `TableStateMachine` interprets it. Dissolving the central `SagaState` enum is the type-level half of §P2 — a shared enum every saga's vocabulary pollutes is the same leak a shared project reference is, one level down. The substrate's `IsTerminal` answer is the routed machine's, never a central static.

### P4 — Composition is discovery at the host edge

Each family contributes an `ISagaModule` (defined in the substrate) declaring: its `SagaType`(s); the `ISagaStateMachine`, `IResultEventBridge`, and `ISagaCommandRouter` it supplies; the consume topics + consumer-group id its sagas subscribe; and its auto-start rule(s) (§P5). The host composes by looping over the modules — registering machines/bridges/routers and starting one consumer per module's consumer group. The host holds an explicit module list now (ADR-PC-021 §A3); swapping for `FamilyModuleLoader`-style assembly-scan later is a localised host change with zero family change.

### P5 — Auto-start is a header-keyed declaration the substrate evaluates

A saga module's auto-start rule is `(startEventType, optional header predicate)` — both evaluated by the substrate against CloudEvents headers alone. The substrate never decodes an Avro payload (P2's extraction-readiness, [ADR-IC-003](./ADR-IC-003-saga-orchestrator.md) 2026-06-15 header-only clause). When a saga needs a payload-borne structural discriminator to filter on (e.g. `auto_renewal_policy`), the engine's outbox relay promotes it to a CloudEvents **extension attribute** via a generic, event-declared seam (family-agnostic on the relay side — it copies whatever the event declares), and the module's filter reads that header.

*Revised 2026-06-26 (bd `babelstone-t7o3.21`): an auto-start rule MAY additionally carry an optional family-agnostic, header-only **fan-out** projector that starts one saga instance per entry in a multi-valued CloudEvents header — e.g. one `settlement` instance per Originated `Movement` when a single event carries opposing debit+credit directions. The substrate still reads CloudEvents headers only and names no family; the fan-out is the module's own rule the substrate merely invokes. See [ADR-PC-032](../../product_concepts/adrs/ADR-PC-032-money-movement-primitive.md) §A9/§A10.*

### P6 — The first realisation is behavior-preserving

Extracting `ConstitutionProcess` (bd `babelstone-t7o3.12`) must not change its observed behaviour: the same transition table, the same `IsTerminal` answers (including HUMAN_INTERVENTION_REQUIRED staying non-terminal), the same SSE-stream terminal states read by `ProcessApiEndpoints`. The existing `ConstitutionProcess` and substrate tests are the regression net; the extraction is a relocation + the `ISagaModule` seam, not a logic change. The two optional machine hooks the multi-saga substrate deferred (`IEventSubstitutor` for the reissue-budget substitution, `IPostAdvanceHook` for the approval-fork self-emit — both currently `saga.SagaType == ConstitutionProcess.Type` guards in `SagaAdvanceHandler`) move into the substrate's saga contract as part of this work, so the substrate carries no `ConstitutionProcess`-specific branch.

---

## Consequences

**What this choice makes easier:**

1. **Open/closed for families.** Adding a product family's saga is additive — a new module, zero substrate diff. The change a small team makes most often never touches the orchestrator.
2. **A reusable substrate.** The orchestrator stays a generic saga-hosting + ports substrate that a second deployment (or a second jurisdiction's families) reuses unchanged, and that `git filter-repo` lifts cleanly ([ADR-PC-019](../../product_concepts/adrs/ADR-PC-019-repository-strategy-monorepo.md) §P2).
3. **Symmetry with the engine.** A family now ships its engine module *and* its saga module side by side under one `CODEOWNERS` entry — one mental model across the whole build.
4. **The §P3 leak is gated, not just stated.** A new fitness function turns ADR-IC-003 §P3 from a convention into a mechanical check.

**What this choice makes harder or impossible:**

1. **A behavior-preserving refactor lands before the renewal feature.** Extracting `ConstitutionProcess` + splitting the substrate is real work that ships no user-visible feature. Mitigation: it is behavior-preserving (the existing tests are the net), it is the one-time cost that makes every subsequent saga cheap, and it removes debt the renewal saga would otherwise compound.
2. **A per-family `.Orchestration` project is some boilerplate.** Each family repeats the module-registration shape. Mitigation: the shape is small and declarative (an `ISagaModule`), and is the accepted cost of the open/closed property — the same trade ADR-PC-021 §Consequences accepts for the `.Application` decider.

**Residual risks:**

- **The substrate→family no-edge rule is a convention until gated.** A stray `ProjectReference` from the substrate to a family would silently erode family-agnosticism. Mitigation: the `ORCHESTRATOR_FAMILY_AGNOSTIC` fitness function below makes it a mechanical check.
- **A concrete saga could be re-introduced into the substrate project without adding a `families/**` reference** (e.g. a `TableStateMachine` subclass typed directly in the substrate). Mitigation: the type-level `ORCHESTRATOR_SUBSTRATE_NO_CONCRETE_SAGA` guard below — the substrate assembly defines no concrete `ISagaStateMachine`/`IResultEventBridge`/`ISagaCommandRouter` implementation.

---

## Verifiable commitments

| # | Commitment (with §-anchor) | Gate (pyramid level) | Test ID | Status |
|---|---|---|---|---|
| 1 | The orchestrator substrate carries no `ProjectReference` to any `families/**` project — the `family → substrate` arrow is one-way; the host composition root is the standing exemption (§D2, §D4, §P2). | architecture / dependency assertion (CI) | `ORCHESTRATOR_FAMILY_AGNOSTIC` | Live |
| 2 | The orchestrator substrate assembly defines **no concrete** `ISagaStateMachine` / `IResultEventBridge` / `ISagaCommandRouter` implementation — every concrete saga lives in a family `.Orchestration` module; the substrate carries no `saga.SagaType == "…"` family branch (§D1, §D3, §P3, §P6). | architecture / type assertion (CI) | `ORCHESTRATOR_SUBSTRATE_NO_CONCRETE_SAGA` | Live |
| 3 | The substrate's saga **subscription wiring** (`Babelstone.Orchestrator.Substrate/Inbox/`) names **no per-family topic constant** — the consume topics arrive only from the family module's `ISagaModule.ConsumeTopics` via the `required` `SagaInboxConsumerOptions.Topics` (§D2, §P4); a hardcoded family topic literal is the per-family edit the family-count-invariant epic removes. The subscription-level cousin of commitments 1/2, added by bd `babelstone-9w2k.5` (enforcement of the existing §D2/§P4 decision — no amendment owed). | architecture / source assertion (CI) | `ORCHESTRATOR_SUBSTRATE_NO_FAMILY_TOPIC_CONSTANT` | Live |

Related: these are the orchestrator-side cousins of [ADR-PC-021](../../product_concepts/adrs/ADR-PC-021-application-layer-family-owned-deciders.md)'s `ENGINE_FAMILY_AGNOSTIC` (the engine spine carries no `families/**` reference) and `EVENT_STORE_SCHEMA_FAMILY_AGNOSTIC` (the engine migration set carries no family-named table). Commitment 1 is the direct `.csproj`-reference twin, modelled on `EngineFamilyAgnosticTests` (a sibling assertion keeps the substrate-project allowlist in lockstep with the §P1/§P2 enumeration parsed off this ADR, so the gate cannot silently drift from the decision). Commitment 2 is the type-level guard that catches a concrete saga typed *inside* the substrate even when no `.csproj` reference does — the structural analog of ADR-PC-021's schema-level twin. Both are promoted to the [commitment catalogue](../../product_concepts/adrs/commitment-catalogue.md) as rows **ORCH-1** / **ORCH-2** (the single source of truth for their status, the one-way ADR→catalogue reference rule) and are **`Live`** as of bd `babelstone-t7o3.12`, which landed the substrate split + `OrchestratorFamilyAgnosticTests` (`Babelstone.Orchestrator.Tests`, running in CI's orchestrator unit lane). Commitment 3 (catalogue row **ORCH-3**, added by bd `babelstone-9w2k.5`) is the **subscription-level** cousin: it scans the substrate's `Inbox/` saga consume-wiring source for a per-family topic constant, since the topics arrive only from the family module's `ISagaModule.ConsumeTopics` via the `required` `SagaInboxConsumerOptions.Topics` (catalogue-derived per [ADR-IC-003 §A9–§A11](./ADR-IC-003-saga-orchestrator.md)) — a guard the `.csproj` (commitment 1) and type-level (commitment 2) checks do not place on a hardcoded topic string. It is enforcement of the existing §D2/§P4 decision, so no amendment is owed.

---

## Amendment — 2026-06-24: a substrate-owned, *family-agnostic* saga is allowed — the `settlement` saga is the first

**In plain English.** This ADR's rule was "no concrete saga lives in the substrate — every saga is a family module," because the only sagas it knew were *product-family-specific* (term-deposit constitution, term-deposit renewal). [ADR-PC-032](../../product_concepts/adrs/ADR-PC-032-money-movement-primitive.md) introduces a saga that is the exact opposite: the **`settlement` saga** that effects any `Movement`'s cash leg. It names **no family** — it is parameterised only by a movement's `direction` (debit vs credit) and is auto-started by *any* family's money-moving event — so the design ([feature-design money-movement-settlement §4](../../product_concepts/feature-design-money-movement-settlement.md)) deliberately puts it **in the substrate as the one shared home**, not copied into every family. That is a direct contradiction of this ADR's blanket "no concrete saga in the substrate" gate. This amendment resolves it: a concrete saga **may** live in the substrate **iff it names no family**; the gate is narrowed from "no concrete saga" to "no *family-specific* concrete saga." It is additive — every product-family saga still lives in its family module (D1 unchanged); this only carves out the genuinely family-agnostic case the original decision had no example of. (Built by bd `babelstone-t7o3.15`; the orchestrator half of ADR-PC-032.)

### A1 · The narrowed rule: the substrate hosts the *generic* saga; families own the *specific* ones (refines §D1/§D2 + the ORCH-2 gate)

- **A family-specific concrete saga still lives in its family `.Orchestration` module** (D1 unchanged): `ConstitutionProcess` and `RenewalProcess` name term-deposit facts/states/commands and stay in `Babelstone.Families.TermDeposit.Orchestration`. The `family → substrate` arrow stays one-way (D2/§P2 unchanged) — a family-named saga in the substrate would still be the leak this ADR exists to stop.
- **A *family-agnostic* concrete saga MAY live in the substrate.** A saga whose state machine, bridge, router, command vocabulary, and state vocabulary name **no family** — keying only on the [ADR-PC-032](../../product_concepts/adrs/ADR-PC-032-money-movement-primitive.md) `Movement` atom's generic `direction` / opaque `account_ref` / generic settlement-command vocabulary — is substrate-owned, exactly as a generic port or store is. The `settlement` saga (`SettlementProcess`) is the first: it is the saga-level analog of a substrate store, not of a family module. This is the same posture the engine already takes for **cross-cutting, family-agnostic** spine events (`PackVersionMigrated`, `PersonalDataErasureRequested` — engine-declared, no family), promoted here to the saga layer.
- **The boundary is "names a family," not "is concrete."** The original §D1/§D3/§P3 conflated the two because every saga it had named a family. The load-bearing invariant is **family-agnosticism**, not "the substrate has zero sagas." A substrate saga that ever named a family (a `saga.SagaType == "ConstitutionProcess"` branch, a `term_deposit` topic literal, a deposit-typed command) is still forbidden — by `ORCHESTRATOR_FAMILY_AGNOSTIC` (ORCH-1, unchanged), `ORCHESTRATOR_SUBSTRATE_NO_FAMILY_TOPIC_CONSTANT` (ORCH-3, unchanged), and the narrowed ORCH-2 below.

### A2 · The ORCH-2 gate (`ORCHESTRATOR_SUBSTRATE_NO_CONCRETE_SAGA`) is narrowed, not removed (refines the Verifiable-commitments commitment 2)

Commitment 2 said "the substrate assembly defines **no concrete** `ISagaStateMachine` / `IResultEventBridge` / `ISagaCommandRouter`." That is narrowed to: **no concrete saga in the substrate names a family** — a substrate-owned saga is allowed only if it carries no `Babelstone.Families.*` reference and no per-family token (the same family-agnosticism every substrate type already owes). The gate's *intent* (catch a **family-specific** saga typed inside the substrate even when no `.csproj` reference does) is preserved; its *mechanism* changes from "zero concrete saga types" to "an allow-list of explicitly family-agnostic substrate sagas + a no-family-reference scan." The `settlement` saga is the first allow-listed entry; a family saga typed into the substrate still fails (it would name a family). The gate test (`OrchestratorFamilyAgnosticTests`) and the catalogue ORCH-2 row are updated in this same change (the §D5 explicit-drift rule).

### A3 · This amends §D1/§D3/§P3 + commitment 2; it does not supersede this ADR

§D1 ("a *family-owned* saga lives in its family module"), D2 (the one-way `family → substrate` arrow), D4 (the host composes; the substrate names no family), D5 (header-keyed auto-start), and the §Consequences all remain binding **as written** for every **family-specific** saga. What this amendment changes is the *scope* of "concrete saga in the substrate is forbidden": it was a blanket ban; it is now a ban on **family-specific** sagas only, because the original had no family-agnostic example. No family saga moves into the substrate; no `family → substrate` reference is introduced; the auto-start mechanism and the header-only payload-opacity rule are unchanged (the `settlement` saga is event-auto-started reading CloudEvents headers only, D5). The open/closed property holds in both directions: a new **family** saga is still zero substrate diff (a new module), and the one **generic** saga the platform owns lives in the one place the platform owns.

---

## Cross-references

- **Honors:** [ADR-IC-003 §P3](./ADR-IC-003-saga-orchestrator.md) (the substrate/per-saga separation this operationalises) — no clause of ADR-IC-003 is contradicted; this ADR adds the structural enforcement §P3 always implied. The optional-hook contract (§P6) generalises the `SagaAdvanceHandler` `ConstitutionProcess`-specific branches the multi-saga substrate (bd `babelstone-mtto` PR1) deferred.
- **Mirrors:** [ADR-PC-021](../../product_concepts/adrs/ADR-PC-021-application-layer-family-owned-deciders.md) (family-owned deciders + `IFamilyHostModule` over a family-agnostic engine) — this is its integration-estate analog for the saga layer; [ADR-PC-019](../../product_concepts/adrs/ADR-PC-019-repository-strategy-monorepo.md) (the `/families` subtree + extraction-readiness).
- **Realised by:** bd `babelstone-t7o3.12` (this ADR + the behavior-preserving `ConstitutionProcess` extraction + the fitness gates); bd `babelstone-mtto` (the renewal saga as the first net-new family saga module on this substrate).
- **Supports docs:** [05 Constitution Saga Walkthrough](../05-constitution-saga-walkthrough.md) (the worked saga this layering generalises).
