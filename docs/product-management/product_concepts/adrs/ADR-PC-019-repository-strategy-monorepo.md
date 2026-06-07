# ADR-PC-019: Repository Strategy — Monorepo

| Field | Value |
|---|---|
| Status | Accepted |
| Date | 2026-05-23 |
| Deciders | jhosm |
| Shape | Tool-selection |
| Common criteria | [ADR-IC-000](../../integration_concepts/adrs/ADR-IC-000-common-evaluation-criteria.md) (reused per [ADR-PC-000](./ADR-PC-000-namespace-and-contract-shape-framework.md) D2; this is the [ADR-PC-000 §D3](./ADR-PC-000-namespace-and-contract-shape-framework.md) "operational discipline" residual category — an engineering-practice decision, declared tool-selection per the [§D4](./ADR-PC-000-namespace-and-contract-shape-framework.md) default) |
| Depends on | [ADR-PC-010](./ADR-PC-010-dotnet-hand-rolled-engine.md) (the build approach this extends; **LLM-codability** is a first-class criterion there and the clinching dimension here), [ADR-PC-006](./ADR-PC-006-cue-schema-language.md) (the Go `pack-validate` binary is a co-located build artefact), [ADR-PC-007](./ADR-PC-007-signed-yaml-oci-pack.md) / [ADR-PC-008](./ADR-PC-008-rate-sheet-storage-and-deploy-api.md) (pack + rate-sheet data — the config-cadence carve-out), [ADR-IC-008](../../integration_concepts/adrs/retired/ADR-IC-008-event-catalog-governance-tooling.md) (EventCatalog governance, source-controlled in the monorepo), [ADR-IC-009](../../integration_concepts/adrs/ADR-IC-009-testing-infrastructure.md) (the path-scoped CI gates run this stack) |
| Extended by | [ADR-IC-013](../../integration_concepts/adrs/ADR-IC-013-in-house-estate-build-and-repository-placement.md) (classifies the integration estate by build provenance and places the in-house estate components in this monorepo) |
| Operationalised by | [ADR-PC-020](./ADR-PC-020-llm-toolchain-and-conformance-governance.md) (the LLM build toolchain and spec-conformance governance that run on this single tree) |
| Resolves | bd `archie-10r.20` (ADR-PC-019: Repository strategy) |

---

## Context

[ADR-PC-010](./ADR-PC-010-dotnet-hand-rolled-engine.md) fixed *what* is built and *in which languages*: a single-deployable C# (.NET 10) engine with a hand-rolled event-sourcing core, a Go `pack-validate` binary ([ADR-PC-006](./ADR-PC-006-cue-schema-language.md)), and a Python MCP sibling ([ADR-IC-010](../../integration_concepts/adrs/ADR-IC-010-mcp-server-runtime-and-sdk.md)) — polyglot only at the boundary. With the ADR-PC series substantially filed, the project is moving from specification to implementation, and one build-strategy question falls out before the first line of engine code:

> **Repository strategy** — one repository for the whole deliverable, or one per component?

This engine is authored primarily by an LLM (the LLM-codability criterion that drove the [ADR-PC-010](./ADR-PC-010-dotnet-hand-rolled-engine.md) language pick), and that fact bears on the answer. *How* the LLM-first build is operationalised — the agent toolchain that does the typing, and the conformance regime that keeps it faithful to the spec — is a separate decision, taken in [ADR-PC-020](./ADR-PC-020-llm-toolchain-and-conformance-governance.md). This ADR decides only where the code lives.

This entry is the [ADR-PC-000 §D3](./ADR-PC-000-namespace-and-contract-shape-framework.md) **residual category** ("operational discipline … fits neither template cleanly … default to tool-selection"), the same class as [ADR-PC-009](./ADR-PC-009-per-instance-version-pinning.md) (version pinning) and [ADR-PC-011](./ADR-PC-011-in-house-load-test-harness.md) (load-test harness). The honest consequence, surfaced up front as PC-009 did: **F1 and F2 do not discriminate.** Git hosting is already in hand; Git is free; source-tree organisation is not a regulated artefact. The load-bearing question is therefore not "which tool" but **which layout keeps the engine's contracts coherent while an LLM does the typing** — settled on S2 (ecosystem coherence) plus the LLM-codability dimension, not on the hard filters.

### The build / estate / external split bounds what is ours to repo — and "estate" is not one thing

The [C4 container inventory](../feature-design-c4-architecture.md) partitions the engine's world by **architectural role**:

- **Product engine (blue)** — the engine process (C#), its PostgreSQL schema, the `pack-validate` Go binary, the loaded **family schemas** (event types, pure handlers, projections, lifecycle state machines), and the **contracts** (Avro payloads, CUE schemas, the EventCatalog source). Ours to version.
- **Integration estate (teal)** — the surrounding infrastructure the engine runs on: Kong, Redpanda + Schema Registry, the saga orchestrator, the outbox publisher, the ACL service, the MCP server, the notification service, observability. Specified in [integration_concepts/adrs/](../../integration_concepts/adrs/README.md).
- **External (grey)** — GL, IFRS 9, channels, KYC, … out-of-scope products we integrate with, not code we hold.

**Architectural role is not build provenance**, and conflating the two is a trap. The estate splits again by *who builds it*: some components are **consumed third-party images/SDKs** — Kong ([ADR-IC-006](../../integration_concepts/adrs/ADR-IC-006-edge-api-gateway.md)), Redpanda ([ADR-IC-001](../../integration_concepts/adrs/ADR-IC-001-event-backbone-message-broker.md)), Grafana LGTM ([ADR-IC-007](../../integration_concepts/adrs/ADR-IC-007-observability-stack.md)), PostgreSQL ([ADR-IC-005](../../integration_concepts/adrs/ADR-IC-005-cqrs-read-model-storage.md)), the test stack ([ADR-IC-009](../../integration_concepts/adrs/ADR-IC-009-testing-infrastructure.md)) — not code we hold; others are **in-house builds** — the saga orchestrator ([ADR-IC-003](../../integration_concepts/adrs/ADR-IC-003-saga-orchestrator.md)), the outbox publisher ([ADR-IC-004](../../integration_concepts/adrs/ADR-IC-004-outbox-pattern-mechanism.md)), the ACL ([ADR-IC-012](../../integration_concepts/adrs/ADR-IC-012-anti-corruption-layer-implementation.md)), the MCP server ([ADR-IC-010](../../integration_concepts/adrs/ADR-IC-010-mcp-server-runtime-and-sdk.md)), the notification service ([ADR-IC-011](../../integration_concepts/adrs/ADR-IC-011-async-saga-completion-notification.md)) — code we write and must place in a repo. [ADR-IC-013](../../integration_concepts/adrs/ADR-IC-013-in-house-estate-build-and-repository-placement.md) makes that role-vs-provenance distinction explicit and decides the in-house estate co-locates here.

So "what is ours to repo" is **the product-engine build *plus* the five in-house estate components** — not the product engine alone, and not the consumed third-party estate or the external systems. That scoping is itself half the finding: "multirepo" can only ever apply to those blue + in-house-teal artefacts.

**Candidates evaluated:**

| # | Candidate | Notes |
|---|---|---|
| A | **Monorepo** — one repository holding all build artefacts *and* the contracts: engine, `pack-validate`, family schemas, contract schemas (Avro + CUE + EventCatalog), config data (packs + rate sheets), the in-house integration-estate services ([ADR-IC-013](../../integration_concepts/adrs/ADR-IC-013-in-house-estate-build-and-repository-placement.md)), infra/deploy, and the existing `docs/`. | One clone, one working tree, one path-scoped CI. The config-cadence split ([01 §3](../01-product-architecture.md)) is honoured *inside* the repo by `CODEOWNERS` + path-scoped pipelines. |
| B | **Multirepo** — one repository per blue / in-house artefact (engine, validator, schemas, orchestrator, ACL, MCP, …), versioned and released independently. | Independent cadence per component; repo-level access boundaries; cross-repo contract changes span multiple PRs. |
| C | **Hybrid** — monorepo for code + contract *schemas*; the populated **pack + rate-sheet data** in a separate repo on its treasury/counsel cadence from day one. | A refinement of A that pre-splits only the config *data* (which ships as a signed OCI artefact by digest, decoupled from engine releases per [ADR-PC-007](./ADR-PC-007-signed-yaml-oci-pack.md)). |

---

## Evaluation

### Hard filter results

#### F1 · Cost / licensing

| Candidate | Licence / cost | Verdict |
|---|---|---|
| A · monorepo | Git + existing host. Zero incremental cost. | **Pass** |
| B · multirepo | Same; possibly N× CI-config maintenance, but no licence cost. | **Pass** |
| C · hybrid | Same as A plus one extra repo. Zero licence cost. | **Pass** |

Uniform pass — F1 does not discriminate (no candidate buys anything).

#### F2 · Regulatory fit (GDPR / DORA / PSD2)

Source-tree layout carries no PII and is not a DORA/PSD2 runtime artefact. The one regulatory-adjacent property a banking reviewer will look for is **approval-boundary auditability** of the configuration surface: [01 §3](../01-product-architecture.md) insists the three artefacts (product configs / rate sheets / pack) keep distinct owners and approval cadences, so "the cheapest change does not inherit the most expensive approval." That segregation is enforceable and auditable under *all three* candidates — by `CODEOWNERS` + path-scoped CI within one repo (A/C) or by repo-level permissions (B). It is a correctness property of *how the pipeline is gated*, not a filter a layout passes or fails.

| Candidate | GDPR | DORA / PSD2 (approval-boundary auditability) | Verdict |
|---|---|---|---|
| A · monorepo | No PII in layout. | Three-owner split enforced by `CODEOWNERS` + path-scoped pipelines; every merge is attributable. | **Pass** |
| B · multirepo | No PII in layout. | Enforced by per-repo permissions. | **Pass** |
| C · hybrid | No PII in layout. | As A, plus a hard repo boundary around config data. | **Pass** |

All three clear the hard filters. The decision is entirely in S2 and the LLM-codability analysis below — the expected shape for the [ADR-PC-000 §D3](./ADR-PC-000-namespace-and-contract-shape-framework.md) residual category.

---

### Soft criteria

#### A · Monorepo — **CHOSEN**

**S1 · Operational complexity for 1–2 people.** Lowest. One clone, one branch model, one path-scoped CI definition, one version coordinate. There is no cross-repo "which engine version goes with which schema version" matrix to maintain — the answer is "whatever is in this commit." For a 1–2-person team this is the difference between coordinating a change and simply making it.

**S2 · Ecosystem coherence — decisive.** This engine is unusually **contract-dense**, and its contracts are the asset the whole build exists to preserve ([01 §6](../01-product-architecture.md): "the bank's most valuable asset … is the integration shape"). The event envelope ([02 §2.4.3](../02-v1-scope-term-deposits.md)), the Avro payloads, the family-schema handler/projection signatures, and the CUE pack schemas are each touched by *multiple* artefacts: the engine produces them, the in-house estate (orchestrator, ACL, notification, outbox — [ADR-IC-013](../../integration_concepts/adrs/ADR-IC-013-in-house-estate-build-and-repository-placement.md)) binds to them, the MCP server maps them to `tools`/`resources`, downstream-consumer fixtures bind to them, and the EventCatalog ([ADR-IC-008](../../integration_concepts/adrs/retired/ADR-IC-008-event-catalog-governance-tooling.md)) governs them. In a monorepo a change to the envelope and *every* consumer plus the catalogue entry plus the registry-compatibility test ([ADR-IC-009](../../integration_concepts/adrs/ADR-IC-009-testing-infrastructure.md)) lands in **one atomic commit**. In a multirepo each is a version-skew surface negotiated across repositories. The brief's own commitment — "one codebase, one set of images, one configuration grammar" ([01 §6 Deployment](../01-product-architecture.md)) — is this coherence stated as a deployment property.

**S3 · Exit cost.** Low, and asymmetric in the monorepo's favour. Splitting a monorepo later (`git filter-repo` per path) is mechanical and can be deferred until a real cadence boundary is observed; *merging* multirepos later means reconciling divergent histories and CI after having paid the coordination tax throughout. Choosing A keeps the split option open at near-zero cost (this is the [C](#evaluation) carve-out, reserved in §P1).

**S4 · Longevity.** Neutral — Git and the chosen host outlive any layout choice.

**Decisive project-specific reason — LLM-codability.** [ADR-PC-010](./ADR-PC-010-dotnet-hand-rolled-engine.md) made LLM-codability a first-class selection criterion; it applies to the *repository* as much as to the language. An LLM agent reasons over a single working tree and a bounded context window. A monorepo lets one agent hold "envelope → producer → every consumer → schema → contract test" simultaneously and change them coherently in one pass. Cross-repo work forces multiple checkouts, multiple PRs, and manual version coordination — exactly the orchestration overhead agents handle poorly. For an LLM-first build, the monorepo is not merely convenient; it is the layout that matches how the primary author works.

#### B · Multirepo — **rejected**

The classic multirepo wins — independent deploy cadence, independent teams, repo-scoped blast radius — do not obtain here. The deliverable deploys as "one set of images" from one topology ([01 §6](../01-product-architecture.md)); the team is 1–2 people, so Conway's law exerts no separating pressure; deploy independence between the engine and the in-house estate services is achievable *inside* one repo by per-service pipelines ([ADR-IC-013](../../integration_concepts/adrs/ADR-IC-013-in-house-estate-build-and-repository-placement.md)) — it is a pipeline property, not a repo-count property. What multirepo *would* cost is real: every contract change becomes a multi-PR cross-repo dance — the precise operation an LLM author and a 1–2-person team are worst equipped to absorb. Rejected on S1 + S2 + LLM-codability with no offsetting S-criterion gain.

#### C · Hybrid (split config data from day one) — **rejected for v1, reserved as a future split**

C correctly identifies a real boundary: pack and rate-sheet *data* have distinct owners and cadences ([01 §3](../01-product-architecture.md)) and ship as digest-pinned OCI artefacts decoupled from engine releases ([ADR-PC-007](./ADR-PC-007-signed-yaml-oci-pack.md)). But that is an **approval-cadence** boundary, not a code boundary, and `CODEOWNERS` + path-scoped pipelines enforce it inside the monorepo without a second repo's coordination cost. Splitting on day one pays the multirepo tax (cross-repo PRs for any change that touches both a schema and its data) before there is evidence Treasury's cadence demands it. Per [ADR-PC-009 §P5](./ADR-PC-009-per-instance-version-pinning.md)'s "reserve, don't pre-build" discipline, C is **deferred**: keep one repo now; revisit the data-repo split once the observed rate-sheet commit cadence proves the boundary needs a hard wall (§P1, Residual Risks).

**Decisive reason for A over B and C:** the contracts are the asset, the primary author is an LLM, and the team is 1–2 people — all three point to atomic, single-context, single-CI change. B sacrifices that for independence the project does not need; C pre-pays for a cadence wall the project has not yet observed.

---

## Decision

### Monorepo, with the config-data split reserved (not taken) for v1.

One repository holds the engine, the `pack-validate` Go binary, the family schemas, the contract schemas (Avro + CUE + EventCatalog source), the config data (packs + rate sheets), the **in-house integration-estate services** ([ADR-IC-013](../../integration_concepts/adrs/ADR-IC-013-in-house-estate-build-and-repository-placement.md)), infra/deploy assets, and the existing `docs/`. The three-owner configuration-surface split ([01 §3](../01-product-architecture.md)) is honoured by `CODEOWNERS` + path-scoped CI, not by separate repositories. The decisive reasons are **S2 contract coherence** (atomic producer + every-consumer + schema + catalogue + contract-test change) and **LLM-codability** (one working tree, one context). Splitting the pack/rate-sheet data into its own repo is reserved as a cheap future move, deferred until the observed Treasury cadence justifies a hard boundary.

**Rejected: multirepo** — sacrifices contract-change atomicity and single-context LLM authorship for component independence a 1–2-person, one-topology deliverable does not need; deploy independence is achievable with per-service pipelines inside one repo. **Rejected (deferred): day-one config-data split** — a cadence boundary `CODEOWNERS` already enforces; pre-splitting pays the cross-repo tax before the cadence proves it necessary.

The agent toolchain that operationalises the LLM-first build on this tree, and the spec-conformance regime that keeps it faithful, are decided separately in [ADR-PC-020](./ADR-PC-020-llm-toolchain-and-conformance-governance.md). The build-provenance classification of the integration estate and the placement of its in-house components in this monorepo are decided in [ADR-IC-013](../../integration_concepts/adrs/ADR-IC-013-in-house-estate-build-and-repository-placement.md).

---

## Implementation Principles

### P1 — Monorepo layout: one tree, path-scoped CI, `CODEOWNERS` for the three-owner config split

A single repository with top-level paths separating the product-engine artefacts, the in-house estate services, and the contract/config planes — illustratively:

```
/engine          C# (.NET 10) single deployable + its PostgreSQL migrations   [ADR-PC-010, ADR-PC-001]
/pack-validate   Go static binary embedding cuelang.org/go                   [ADR-PC-006]
/families        loaded family schemas: event types, pure handlers,
                 projections, lifecycle state machines (term_deposit first)   [event-store §3]
/contracts       Avro payload schemas + CUE constraint schemas +
                 EventCatalog source (the governed contract surface)          [ADR-IC-002, ADR-IC-008]
/orchestrator    in-house saga orchestrator                                  [ADR-IC-003]  ← in-house estate, ADR-IC-013
/acl             anti-corruption layer service(s) + own DB                   [ADR-IC-012]  ← in-house estate, ADR-IC-013
/mcp-server      Python MCP server (runtime product deliverable)             [ADR-IC-010]  ← in-house estate, ADR-IC-013
/notification    async saga-completion notification service                 [ADR-IC-011]  ← in-house estate, ADR-IC-013
/packs           populated regulatory-pack YAML data (pt.YYYY.N)              [ADR-PC-007]  ← CODEOWNERS: engine team + counsel
/rate-sheets     versioned rate-sheet data                                   [ADR-PC-008]  ← CODEOWNERS: treasury / ALM
/infra           deploy/runbook/operational tooling                          [01 §6]
/docs            existing concept docs, feature-design notes, ADRs           (already present)
```

The outbox publisher ([ADR-IC-004](../../integration_concepts/adrs/ADR-IC-004-outbox-pattern-mechanism.md)) is a per-service worker, not its own top-level path — it lives inside `/engine`, `/acl`, and `/notification` (the three services that own an outbox, per the [IC ADR topology](../../integration_concepts/adrs/README.md)). The placement of these in-house estate paths and *why they belong here* is [ADR-IC-013](../../integration_concepts/adrs/ADR-IC-013-in-house-estate-build-and-repository-placement.md); this ADR owns the tree they join.

CI is **path-scoped**: a change under `/engine` runs the engine build + analysers + Testcontainers suite; a change under `/packs` runs `pack-validate` + cosign + the pack-load smoke test; a change under an estate-service path runs that service's build + its contract tests; a docs-only change runs link/diagram checks. Each in-house service carries its own Dockerfile / deploy pipeline, so the engine and the estate deploy independently from one tree. `CODEOWNERS` gates `/packs`, `/rate-sheets`, and product-config paths to their respective owners ([01 §3](../01-product-architecture.md)), so the approval-cadence segregation the surface ADRs require is a property of the merge gate, not of repository count.

### P2 — Each in-house artefact is a cleanly-bounded, extraction-ready subtree

Every blue and in-house-teal artefact is its own top-level path with its own build, its own `CODEOWNERS` entry, and its own deploy pipeline. This is the discipline that keeps the [C](#evaluation) split (and the [ADR-IC-013](../../integration_concepts/adrs/ADR-IC-013-in-house-estate-build-and-repository-placement.md) estate-repo split) a near-zero-cost future move: a path with clean boundaries extracts via `git filter-repo` mechanically, whereas a path entangled across the tree does not. Co-location is the default; extraction-readiness is the hedge that makes deferring the split safe rather than lazy.

---

## Consequences

**What this choice makes easier:**

1. **Atomic contract change.** An envelope/schema change plus every consumer — engine, the in-house estate services, the EventCatalog entry, the registry-compatibility test — lands in one commit; no cross-repo version-skew window.
2. **Single-context LLM authorship.** The primary author (an LLM) sees the whole dependency chain in one working tree, matching how it reasons.
3. **One CI, one version coordinate.** Path-scoped pipelines keep a 1–2-person team from maintaining N CI configs or a component-version compatibility matrix, while still giving per-service independent build and deploy.
4. **Cheap, deferred splits.** Both the config-data repo split and the in-house-estate repo split ([ADR-IC-013](../../integration_concepts/adrs/ADR-IC-013-in-house-estate-build-and-repository-placement.md)) stay near-zero-cost future moves (`git filter-repo`), taken only when a real cadence or second-consumer boundary appears, because §P2 keeps every path extraction-ready.

**What this choice makes harder or impossible:**

1. **CI must be path-scoped from the start.** A naïve "build everything on every push" monorepo CI is slow and wasteful for a 1–2-person team. Mitigation: path filters in the CI definition from day one (§P1).
2. **The config-cadence boundary is a convention, not a wall.** `CODEOWNERS` + path scoping enforce the three-owner split ([01 §3](../01-product-architecture.md)), but a misconfigured owner file weakens it in a way a separate repo could not. Mitigation: the split is auditable in merge history; revisit the hard-wall [C](#evaluation) split if it is ever bypassed.

**Residual risks:**

- **Monorepo CI scaling.** As `/engine`'s test suite (Testcontainers, the Q-AK load test) grows, even path-scoped CI on the engine path lengthens. Mitigation: the load test is already a separate acceptance gate ([ADR-PC-011](./ADR-PC-011-in-house-load-test-harness.md)), not an every-push job.
- **Deferred splits may arrive late.** If Treasury's rate-sheet cadence (or a second product consuming the estate) turns out to demand a hard repo boundary, the split is still mechanical but must be done under live operation. Mitigation: `/rate-sheets` and each in-house estate path is already an isolated, extraction-ready path with its own owners and pipeline (§P2), so the split is a path extraction, not a restructuring.

---

## Open Actions

1. **Scaffold the monorepo skeleton** — the §P1 top-level paths (product-engine + in-house estate per [ADR-IC-013](../../integration_concepts/adrs/ADR-IC-013-in-house-estate-build-and-repository-placement.md)), path-scoped CI, per-service Dockerfiles, and `CODEOWNERS` for `/packs`, `/rate-sheets`, and product-config paths.
2. **Revisit the config-data split** — once a few cycles of real rate-sheet/pack commits show the observed Treasury cadence, decide whether the reserved [C](#evaluation) split is warranted.
3. **Revisit the in-house-estate split** — per [ADR-IC-013](../../integration_concepts/adrs/ADR-IC-013-in-house-estate-build-and-repository-placement.md), if a second product engine begins consuming the estate, or the orchestrator's Temporal migration wants its own lifecycle.

---

## Verifiable commitments

This decision's load-bearing commitments are fitness functions in the [commitment catalogue](./commitment-catalogue.md) — the single source of truth for each commitment's exact claim, gate (pyramid level), and `Live`/`Planned`/`Gap` status ([ADR-PC-020 §P5–§P7](./ADR-PC-020-llm-toolchain-and-conformance-governance.md)):

No executable commitments — this repository-layout decision is realised by `CODEOWNERS` + path-scoped CI (§P1) and is process, not buildable engine behaviour an implementation can drift from. The engine/estate boundary it enables — the `family → engine` one-way arrow, with no `ProjectReference` from the generic spine into `families/**` — is gated separately by `ENGINE_FAMILY_AGNOSTIC`, owned by [ADR-PC-021 §P2 / §D2](./ADR-PC-021-application-layer-family-owned-deciders.md), not by this ADR. The §P2 extraction-ready-subtree discipline (the property that keeps the deferred config-data and estate splits near-zero-cost) is an engineering convention checked in review, with no Test ID wired.

---

## Cross-references

- [ADR-PC-010](./ADR-PC-010-dotnet-hand-rolled-engine.md) — the build approach this extends; LLM-codability as a first-class criterion, applied here to the repository.
- [ADR-PC-020](./ADR-PC-020-llm-toolchain-and-conformance-governance.md) — the agent toolchain and spec-conformance governance that run on this single tree (the "how we build on it" decision, split out from this one).
- [ADR-IC-013](../../integration_concepts/adrs/ADR-IC-013-in-house-estate-build-and-repository-placement.md) — the build-provenance classification of the integration estate and the placement of its in-house components in this monorepo.
- [ADR-PC-006](./ADR-PC-006-cue-schema-language.md) — the Go `pack-validate` binary, a co-located build artefact.
- [ADR-PC-007](./ADR-PC-007-signed-yaml-oci-pack.md) / [ADR-PC-008](./ADR-PC-008-rate-sheet-storage-and-deploy-api.md) — pack + rate-sheet data; the config-cadence boundary behind the reserved [C](#evaluation) split.
- [ADR-PC-009 §P5](./ADR-PC-009-per-instance-version-pinning.md) — the "reserve, don't pre-build" discipline applied to the config-data and estate splits.
- [ADR-PC-011](./ADR-PC-011-in-house-load-test-harness.md) — the prior "how we build/validate it" engineering-practice ADR; its load test is the separate acceptance gate that keeps monorepo CI scalable.
- [ADR-IC-008](../../integration_concepts/adrs/retired/ADR-IC-008-event-catalog-governance-tooling.md) — EventCatalog, source-controlled in the monorepo; updated atomically with the events it governs.
- [ADR-IC-009](../../integration_concepts/adrs/ADR-IC-009-testing-infrastructure.md) — the Testcontainers + consumer-driven-contract CI gates the path-scoped pipelines run.
- [01 §1, §3, §6](../01-product-architecture.md) — one-engine-many-families thesis; the three-owner config split; "one codebase, one set of images."
- [feature-design-c4-architecture](../feature-design-c4-architecture.md) — the build/estate/external split (by architectural role) that this ADR refines into role-vs-provenance.

---

*Decided 2026-05-23 by jhosm.*
*Revised 2026-05-24: scope narrowed to repository strategy; the agent toolchain (former D2 / §P2–§P5) moved to [ADR-PC-020](./ADR-PC-020-llm-toolchain-and-conformance-governance.md), and the build/estate split corrected into a role-vs-provenance distinction with the in-house-estate placement decided in [ADR-IC-013](../../integration_concepts/adrs/ADR-IC-013-in-house-estate-build-and-repository-placement.md).*
