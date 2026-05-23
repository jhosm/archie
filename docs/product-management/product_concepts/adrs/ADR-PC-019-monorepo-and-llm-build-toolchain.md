# ADR-PC-019: Repository Strategy and LLM Build Toolchain — Monorepo + Claude Code Agent Tooling

| Field | Value |
|---|---|
| Status | Accepted |
| Date | 2026-05-23 |
| Deciders | jhosm |
| Shape | Tool-selection |
| Common criteria | [ADR-IC-000](../../integration_concepts/adrs/ADR-IC-000-common-evaluation-criteria.md) (reused per [ADR-PC-000](./ADR-PC-000-namespace-and-contract-shape-framework.md) D2; this is the [ADR-PC-000 §D3](./ADR-PC-000-namespace-and-contract-shape-framework.md) "operational discipline" residual category — an engineering-practice decision, declared tool-selection per the [§D4](./ADR-PC-000-namespace-and-contract-shape-framework.md) default) |
| Depends on | [ADR-PC-010](./ADR-PC-010-dotnet-hand-rolled-engine.md) (the build approach this extends — C# engine + Go validator + Python MCP polyglot-at-the-boundary; **LLM-codability** is a first-class criterion there and the clinching dimension here), [ADR-PC-006](./ADR-PC-006-cue-schema-language.md) (the Go `pack-validate` binary is a co-located build artefact), [ADR-PC-007](./ADR-PC-007-signed-yaml-oci-pack.md) / [ADR-PC-008](./ADR-PC-008-rate-sheet-storage-and-deploy-api.md) (pack + rate-sheet data — the config-cadence carve-out), [ADR-IC-008](../../integration_concepts/adrs/ADR-IC-008-event-catalog-governance-tooling.md) (EventCatalog governance, source-controlled in the monorepo), [ADR-IC-009](../../integration_concepts/adrs/ADR-IC-009-testing-infrastructure.md) (the CI gates the edit-time hooks mirror), [ADR-IC-010](../../integration_concepts/adrs/ADR-IC-010-mcp-server-runtime-and-sdk.md) (the engine's *runtime* MCP server — distinct from dev-time MCP, see §P5) |
| Resolves | bd `archie-10r.20` (ADR-PC-019: Repository strategy and LLM build toolchain) |

---

## Context

[ADR-PC-010](./ADR-PC-010-dotnet-hand-rolled-engine.md) fixed *what* is built and *in which languages*: a single-deployable C# (.NET 9) engine with a hand-rolled event-sourcing core, a Go `pack-validate` binary ([ADR-PC-006](./ADR-PC-006-cue-schema-language.md)), and a Python MCP sibling ([ADR-IC-010](../../integration_concepts/adrs/ADR-IC-010-mcp-server-runtime-and-sdk.md)) — polyglot only at the boundary. With the ADR-PC series substantially filed, the project is moving from specification to implementation. Two build-strategy questions fall out before the first line of engine code, and they are coupled by a single fact — **this engine is authored primarily by an LLM** (the LLM-codability criterion that drove the [ADR-PC-010](./ADR-PC-010-dotnet-hand-rolled-engine.md) language pick):

1. **Repository strategy** — one repository for the whole deliverable, or one per component?
2. **Agent toolchain** — which Claude Code primitives (hooks, skills, subagents, a plugin) and which agent-orchestration surface operationalize an LLM-first build, and which engine-specific invariants does each enforce?

This entry is the [ADR-PC-000 §D3](./ADR-PC-000-namespace-and-contract-shape-framework.md) **residual category** ("operational discipline … fits neither template cleanly … default to tool-selection"), the same class as [ADR-PC-009](./ADR-PC-009-per-instance-version-pinning.md) (version pinning) and [ADR-PC-011](./ADR-PC-011-in-house-load-test-harness.md) (load-test harness — the prior "how we build and validate it" ADR). The honest consequence, surfaced up front as PC-009 did: **F1 and F2 do not discriminate.** Git hosting is already in hand; Git is free; Claude Code hooks/skills/agents are dev-environment configuration with no licence or runtime-regulatory surface; source-tree organisation is not a regulated artefact. The load-bearing question is therefore not "which tool" but **which layout and which tooling keep the engine's contracts coherent and its invariants enforced while an LLM does the typing** — settled on S2 (ecosystem coherence) plus the LLM-codability dimension, not on the hard filters.

### The build-vs-estate split bounds what is even ours to repo

The [C4 container inventory](../feature-design-c4-architecture.md) partitions the engine's world three ways, and only one part is a repository decision at all:

- **Build (blue)** — the engine process (C#), its PostgreSQL schema, the `pack-validate` Go binary, the loaded **family schemas** (event types, pure handlers, projections, lifecycle state machines), and the **contracts** (Avro payloads, CUE schemas, the EventCatalog source). These are ours to version.
- **Estate (teal)** — Kong, Redpanda + Schema Registry, the ACL service, the MCP server runtime, the notification service. Inherited per [integration_concepts/adrs/](../../integration_concepts/adrs/README.md); *consumed as images and SDKs*, not as repositories we structure here.
- **External (grey)** — GL, IFRS 9, channels, KYC, … out-of-scope products we integrate with, not code we hold.

So "multirepo" can only ever apply to the ~3–5 blue artefacts. That scoping is itself half the finding.

**Candidates evaluated:**

| # | Candidate | Notes |
|---|---|---|
| A | **Monorepo** — one repository holding all build artefacts *and* the contracts: engine, `pack-validate`, family schemas, contract schemas (Avro + CUE + EventCatalog), config data (packs + rate sheets), infra/deploy, and the existing `docs/`. | One clone, one working tree, one path-scoped CI. The config-cadence split ([01 §3](../01-product-architecture.md)) is honoured *inside* the repo by `CODEOWNERS` + path-scoped pipelines. |
| B | **Multirepo** — one repository per blue artefact (engine, validator, schemas, MCP, ACL), versioned and released independently. | Independent cadence per component; repo-level access boundaries; cross-repo contract changes span multiple PRs. |
| C | **Hybrid** — monorepo for code + contract *schemas*; the populated **pack + rate-sheet data** in a separate repo on its treasury/counsel cadence from day one. | A refinement of A that pre-splits only the config *data* (which ships as a signed OCI artefact by digest, decoupled from engine releases per [ADR-PC-007](./ADR-PC-007-signed-yaml-oci-pack.md)). |

---

## Evaluation

### Hard filter results

#### F1 · Cost / licensing

| Candidate | Licence / cost | Verdict |
|---|---|---|
| A · monorepo | Git + existing host; Claude Code tooling is dev config. Zero incremental cost. | **Pass** |
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

**S2 · Ecosystem coherence — decisive.** This engine is unusually **contract-dense**, and its contracts are the asset the whole build exists to preserve ([01 §6](../01-product-architecture.md): "the bank's most valuable asset … is the integration shape"). The event envelope ([02 §2.4.3](../02-v1-scope-term-deposits.md)), the Avro payloads, the family-schema handler/projection signatures, and the CUE pack schemas are each touched by *multiple* blue artefacts: the engine produces them, the MCP server maps them to `tools`/`resources`, the ACL and downstream-consumer fixtures bind to them, and the EventCatalog ([ADR-IC-008](../../integration_concepts/adrs/ADR-IC-008-event-catalog-governance-tooling.md)) governs them. In a monorepo a change to the envelope and *every* consumer plus the catalogue entry plus the registry-compatibility test ([ADR-IC-009](../../integration_concepts/adrs/ADR-IC-009-testing-infrastructure.md)) lands in **one atomic commit**. In a multirepo each is a version-skew surface negotiated across repositories. The brief's own commitment — "one codebase, one set of images, one configuration grammar" ([01 §6 Deployment](../01-product-architecture.md)) — is this coherence stated as a deployment property.

**S3 · Exit cost.** Low, and asymmetric in the monorepo's favour. Splitting a monorepo later (`git filter-repo` per path) is mechanical and can be deferred until a real cadence boundary is observed; *merging* multirepos later means reconciling divergent histories and CI after having paid the coordination tax throughout. Choosing A keeps the split option open at near-zero cost (this is the [C](#evaluation) carve-out, reserved in §P1).

**S4 · Longevity.** Neutral — Git and the chosen host outlive any layout choice.

**Decisive project-specific reason — LLM-codability.** [ADR-PC-010](./ADR-PC-010-dotnet-hand-rolled-engine.md) made LLM-codability a first-class selection criterion; it applies to the *repository* as much as to the language. An LLM agent reasons over a single working tree and a bounded context window. A monorepo lets one agent hold "envelope → producer → every consumer → schema → contract test" simultaneously and change them coherently in one pass. Cross-repo work forces multiple checkouts, multiple PRs, and manual version coordination — exactly the orchestration overhead agents handle poorly. For an LLM-first build, the monorepo is not merely convenient; it is the layout that matches how the primary author works.

#### B · Multirepo — **rejected**

The classic multirepo wins — independent deploy cadence, independent teams, repo-scoped blast radius — do not obtain here. The deliverable deploys as "one set of images" from one topology ([01 §6](../01-product-architecture.md)); the team is 1–2 people, so Conway's law exerts no separating pressure; the estate components that *might* warrant isolation are inherited images, not repos this ADR governs. What multirepo *would* cost is real: every contract change becomes a multi-PR cross-repo dance — the precise operation an LLM author and a 1–2-person team are worst equipped to absorb. Rejected on S1 + S2 + LLM-codability with no offsetting S-criterion gain.

#### C · Hybrid (split config data from day one) — **rejected for v1, reserved as a future split**

C correctly identifies a real boundary: pack and rate-sheet *data* have distinct owners and cadences ([01 §3](../01-product-architecture.md)) and ship as digest-pinned OCI artefacts decoupled from engine releases ([ADR-PC-007](./ADR-PC-007-signed-yaml-oci-pack.md)). But that is an **approval-cadence** boundary, not a code boundary, and `CODEOWNERS` + path-scoped pipelines enforce it inside the monorepo without a second repo's coordination cost. Splitting on day one pays the multirepo tax (cross-repo PRs for any change that touches both a schema and its data) before there is evidence Treasury's cadence demands it. Per [ADR-PC-009 §P5](./ADR-PC-009-per-instance-version-pinning.md)'s "reserve, don't pre-build" discipline, C is **deferred**: keep one repo now; revisit the data-repo split once the observed rate-sheet commit cadence proves the boundary needs a hard wall (§P1, Residual Risks).

**Decisive reason for A over B and C:** the contracts are the asset, the primary author is an LLM, and the team is 1–2 people — all three point to atomic, single-context, single-CI change. B sacrifices that for independence the project does not need; C pre-pays for a cadence wall the project has not yet observed.

---

## Decision

This ADR makes **two coupled decisions**.

### D1 — Repository strategy: **monorepo**, with the config-data split reserved (not taken) for v1.

One repository holds the engine, the `pack-validate` Go binary, the family schemas, the contract schemas (Avro + CUE + EventCatalog source), the config data (packs + rate sheets), infra/deploy assets, and the existing `docs/`. The three-owner configuration-surface split ([01 §3](../01-product-architecture.md)) is honoured by `CODEOWNERS` + path-scoped CI, not by separate repositories. The decisive reasons are **S2 contract coherence** (atomic producer + every-consumer + schema + catalogue + contract-test change) and **LLM-codability** (one working tree, one context). Splitting the pack/rate-sheet data into its own repo is reserved as a cheap future move, deferred until the observed Treasury cadence justifies a hard boundary.

### D2 — Agent toolchain: **Claude Code is the sole agent-orchestration surface**; tooling is layered by enforcement mechanism; no second orchestration framework.

The build is operationalised with Claude Code primitives chosen by *how the rule is enforced*:

- **Deterministic always-rules → hooks** (the harness enforces them; the model cannot forget).
- **Judgement-bearing repeatable procedures → skills** (model-invoked, like the existing `create_backlog`).
- **Context-isolated review and parallel work → subagents** (domain-specialised review the generic review toolkit does not cover).
- **Packaging → one project plugin** (`babelstone-engine`) bundling the above, *once they stabilise*.

No heavyweight second multi-agent orchestrator is adopted. This mirrors the [ADR-PC-010](./ADR-PC-010-dotnet-hand-rolled-engine.md) hand-rolled-core posture applied to tooling: keep the agent stack lean and fully owned; reach for more orchestration only against a concrete wall. Claude Code subagents + Git worktrees cover the one parallelism case in sight (independent family-schema work). The per-primitive inventory and the invariants each enforces are specified in the Implementation Principles.

**Rejected: multirepo** — sacrifices contract-change atomicity and single-context LLM authorship for component independence a 1–2-person, one-topology deliverable does not need. **Rejected (deferred): day-one config-data split** — a cadence boundary `CODEOWNERS` already enforces; pre-splitting pays the cross-repo tax before the cadence proves it necessary. **Rejected: a second agent-orchestration framework** — against the lean, fully-owned-tooling posture; no current need Claude Code + worktrees does not meet.

---

## Implementation Principles

### P1 — Monorepo layout: one tree, path-scoped CI, `CODEOWNERS` for the three-owner config split

A single repository with top-level paths separating the blue artefacts and the contract/config planes — illustratively:

```
/engine          C# (.NET 9) single deployable + its PostgreSQL migrations   [ADR-PC-010, ADR-PC-001]
/pack-validate   Go static binary embedding cuelang.org/go                   [ADR-PC-006]
/families        loaded family schemas: event types, pure handlers,
                 projections, lifecycle state machines (term_deposit first)   [event-store §3]
/contracts       Avro payload schemas + CUE constraint schemas +
                 EventCatalog source (the governed contract surface)          [ADR-IC-002, ADR-IC-008]
/packs           populated regulatory-pack YAML data (pt.YYYY.N)              [ADR-PC-007]  ← CODEOWNERS: engine team + counsel
/rate-sheets     versioned rate-sheet data                                   [ADR-PC-008]  ← CODEOWNERS: treasury / ALM
/infra           deploy/runbook/operational tooling                          [01 §6]
/docs            existing concept docs, feature-design notes, ADRs           (already present)
```

CI is **path-scoped**: a change under `/engine` runs the engine build + analysers + Testcontainers suite; a change under `/packs` runs `pack-validate` + cosign + the pack-load smoke test; a docs-only change runs link/diagram checks. `CODEOWNERS` gates `/packs`, `/rate-sheets`, and product-config paths to their respective owners ([01 §3](../01-product-architecture.md)), so the approval-cadence segregation the surface ADRs require is a property of the merge gate, not of repository count. (The MCP server and any engine-owned ACL specialisation join as sibling top-level paths when built; they are estate-runtime per the C4 split but their *code*, where we own it, lives here.)

### P2 — Hooks: deterministic always-rules the harness enforces, mirroring authoritative CI gates

Hooks surface — at edit/commit time — checks that are *already* CI-authoritative, so the hook is a faster mirror and never the source of truth:

- **`*.puml` → re-render SVG.** Already implemented as the `.githooks/pre-commit` hook ([feature-design-c4-architecture §PlantUML](../feature-design-c4-architecture.md), `CLAUDE.md`); a Claude `PostToolUse` hook is optional faster feedback, not a second authority.
- **Engine handler edits → run the determinism gate + `Money`/`decimal` Roslyn analysers** ([ADR-PC-010 §P1–§P2, §P5](./ADR-PC-010-dotnet-hand-rolled-engine.md)). These are CI gates; a hook flags violations inline before commit. Enforcement lives in the analyser, not in the model's memory.
- **Session-end → print the mandatory push protocol** (`git pull --rebase` → `bd dolt push` → `git push` → verify clean) from `CLAUDE.md` / the bd session-close protocol.
- **`TodoWrite` / `TaskCreate` → block with "use `bd`."** `CLAUDE.md` and `bd prime` prohibit them; a `PreToolUse` hook makes the prohibition mechanical.
- **Markdown edits under `adrs/` → cross-link + ADR-number lint** (the [ADR-PC-000 §D5](./ADR-PC-000-namespace-and-contract-shape-framework.md) link-pattern-by-location rules and the disk+bd dual-number-check).

### P3 — Skills: model-invoked procedures (the existing `create_backlog` is the template)

In leverage order:

1. **`new-family-schema`** — *highest leverage.* Scaffolds a family's event types + pure handlers + projections + lifecycle state machine + replay fixtures, using `term_deposit` as the reference. The entire "one engine, many families" thesis ([01 §1](../01-product-architecture.md), [event-store §3](../feature-design-event-store-projections.md)) means product velocity = time-to-correct-family-schema while the engine code stays still. This is where LLM speed compounds.
2. **`new-event`** — enforces the `<Entity><PastParticipleVerb>` convention ([02 §2.4](../02-v1-scope-term-deposits.md), [integration_concepts §08](../../integration_concepts/08-event-catalog-governance.md)), generates the Avro schema, registers it in EventCatalog, adds the envelope fields, and adds a registry backward-compatibility check.
3. **`new-adr`** — automates the disk+bd dual-number-check (the `adr-numbering-check-disk-and-bd` bd memory), selects the [ADR-PC-000](./ADR-PC-000-namespace-and-contract-shape-framework.md) shape (tool-selection eval table vs contract-shape six slots), wires cross-links per the location rules, and updates the ADR README index. (This ADR was authored by that procedure done by hand.)
4. **`pack-author`** — scaffolds CUE schema + YAML data, runs `pack-validate` depths 1–4, cosign-signs, and `oras`-pushes ([ADR-PC-006](./ADR-PC-006-cue-schema-language.md), [ADR-PC-007](./ADR-PC-007-signed-yaml-oci-pack.md)).

### P4 — Subagents: domain-specialised review the generic toolkit does not cover

- **financial-math-reviewer** — checks kernel/handler changes against [financial_concepts](../../financial_concepts/banking_products_financial_mathematics.md): Act/360, the TANB/TANL split, **withholding applied flow-by-flow not by rate-scaling** (the subtle §5.4 rule), the TAE formula, and round-once-at-the-`Money` boundary ([ADR-PC-010 §P2](./ADR-PC-010-dotnet-hand-rolled-engine.md)).
- **contract-reviewer** — event/schema changes against the [§09](../../integration_concepts/09-long-term-schema-evolution.md) forward-only evolution rules (backward-compatible, or V2-in-parallel), the naming convention, and the **no-PII-on-the-durable-bus** rule (references only; resolve internally — [ADR-PC-014](./ADR-PC-014-customer-notification-emit-contract.md), [ADR-PC-004](./ADR-PC-004-pii-crypto-shredding.md)).
- **replay/determinism-auditor** — handlers pure (no clock, no I/O)? projections rebuildable folds? fixture replay still green? ([event-store §5.3, §10.3](../feature-design-event-store-projections.md)).
- **doc-consistency** — checks the heavily cross-linked docs and the C4 diagrams against their cited sources, honouring the [feature-design-c4-architecture](../feature-design-c4-architecture.md) "if this view and a cited source disagree, the source wins" rule.

### P5 — Packaging and the runtime-vs-dev MCP distinction; orchestration stance

- **One project plugin** (`babelstone-engine`) bundles P2–P4 once stable; do not package prematurely (prove loose first, then version with the repo).
- **Dev-time MCP is lean:** `github` (PR flow) + `context7` (library docs) suffice; `bd` is a CLI — allowlist it, no MCP needed. Unrelated plugins (analytics, browser, error-tracking) are noise for a backend banking engine.
- **The engine's own MCP server ([ADR-IC-010](../../integration_concepts/adrs/ADR-IC-010-mcp-server-runtime-and-sdk.md)) is a *runtime product deliverable*** — an untrusted agent channel into the engine's command/query surface — **not** dev tooling. The two must never be conflated: one ships to the bank and is IAM-gated; the other configures the developer's machine.
- **No second orchestration framework.** Claude Code subagents + Git worktrees cover parallel family-schema work; revisit only against a concrete wall (consistent with the [ADR-PC-010](./ADR-PC-010-dotnet-hand-rolled-engine.md) lean, fully-owned posture).

---

## Consequences

**What this choice makes easier:**

1. **Atomic contract change.** An envelope/schema change plus every consumer plus the EventCatalog entry plus the registry-compatibility test land in one commit — no cross-repo version-skew window.
2. **Single-context LLM authorship.** The primary author (an LLM) sees the whole dependency chain in one working tree, matching how it reasons.
3. **One CI, one version coordinate.** Path-scoped pipelines keep a 1–2-person team from maintaining N CI configs or a component-version compatibility matrix.
4. **Cheap, deferred split.** The config-data repo split stays a near-zero-cost future move (`git filter-repo`), taken only when a real cadence boundary appears.
5. **Invariants enforced where they are authoritative.** Hooks mirror the determinism/`Money` analysers and CI gates that already hold the line ([ADR-PC-010 §P5](./ADR-PC-010-dotnet-hand-rolled-engine.md)); the model's reliability is not the enforcement mechanism.
6. **Velocity on the thesis.** `new-family-schema` makes "one engine, many families" the fast path, which is where the product wedge ([01 §1, §3](../01-product-architecture.md)) actually pays out.

**What this choice makes harder or impossible:**

1. **CI must be path-scoped from the start.** A naïve "build everything on every push" monorepo CI is slow and wasteful for a 1–2-person team. Mitigation: path filters in the CI definition from day one (§P1).
2. **The config-cadence boundary is a convention, not a wall.** `CODEOWNERS` + path scoping enforce the three-owner split ([01 §3](../01-product-architecture.md)), but a misconfigured owner file weakens it in a way a separate repo could not. Mitigation: the split is auditable in merge history; revisit the hard-wall [C](#evaluation) split if it is ever bypassed.
3. **One plugin couples tooling versions to the repo.** Intentional — the toolchain should evolve with the contracts it enforces — but it means tooling is not independently reusable across projects without extraction.

**Residual risks:**

- **Monorepo CI scaling.** As `/engine`'s test suite (Testcontainers, the Q-AK load test) grows, even path-scoped CI on the engine path lengthens. Mitigation: the load test is already a separate acceptance gate ([ADR-PC-011](./ADR-PC-011-in-house-load-test-harness.md)), not an every-push job.
- **Deferred config-data split may arrive late.** If Treasury's rate-sheet cadence turns out to demand a hard repo boundary, the split is still mechanical but must be done under live operation. Mitigation: `/rate-sheets` is already an isolated path with its own owners and pipeline, so the split is a path extraction, not a restructuring.
- **Toolchain drift from the invariants it enforces.** A hook or review agent could lag a changed analyser or rule. Mitigation: hooks mirror the *authoritative* CI gates ([ADR-IC-009](../../integration_concepts/adrs/ADR-IC-009-testing-infrastructure.md), [ADR-PC-010 §P5](./ADR-PC-010-dotnet-hand-rolled-engine.md)) rather than re-implementing them, so the gate moving is what catches a stale hook.
- **Over-tooling.** Building skills/agents before they earn their place wastes effort. Mitigation: the leverage-ordered build sequence (Open Actions) and "package only when stable" (§P5).

---

## Open Actions

1. **Scaffold the monorepo skeleton** — the §P1 top-level paths, path-scoped CI, and `CODEOWNERS` for `/packs`, `/rate-sheets`, and product-config paths.
2. **Build the toolchain in leverage order** — hooks (§P2, the safety floor) → `new-family-schema` + `new-event` (§P3, velocity) → `new-adr` (needed now) → the §P4 domain review agents → fold into the `babelstone-engine` plugin (§P5) once stable.
3. **Confirm the dev-time MCP allowlist** — `github` + `context7` enabled; `bd` allowlisted; prune unrelated plugins from the project config.
4. **Revisit the config-data split** — once a few cycles of real rate-sheet/pack commits show the observed Treasury cadence, decide whether the reserved [C](#evaluation) split is warranted.

---

## Cross-references

- [ADR-PC-010](./ADR-PC-010-dotnet-hand-rolled-engine.md) — the build approach this extends; LLM-codability as a first-class criterion; the hand-rolled/lean posture applied here to tooling; the determinism + `Money` analysers the §P2 hooks mirror.
- [ADR-PC-006](./ADR-PC-006-cue-schema-language.md) — the Go `pack-validate` binary, a co-located build artefact and the `pack-author` skill's engine.
- [ADR-PC-007](./ADR-PC-007-signed-yaml-oci-pack.md) / [ADR-PC-008](./ADR-PC-008-rate-sheet-storage-and-deploy-api.md) — pack + rate-sheet data; the config-cadence boundary behind the reserved [C](#evaluation) split.
- [ADR-PC-009 §P5](./ADR-PC-009-per-instance-version-pinning.md) — the "reserve, don't pre-build" discipline applied to the config-data split.
- [ADR-PC-011](./ADR-PC-011-in-house-load-test-harness.md) — the prior "how we build/validate it" engineering-practice ADR; its load test is the separate acceptance gate that keeps monorepo CI scalable.
- [ADR-PC-014](./ADR-PC-014-customer-notification-emit-contract.md) / [ADR-PC-004](./ADR-PC-004-pii-crypto-shredding.md) — the no-PII-on-the-bus rule the contract-reviewer agent (§P4) enforces.
- [ADR-IC-008](../../integration_concepts/adrs/ADR-IC-008-event-catalog-governance-tooling.md) — EventCatalog, source-controlled in the monorepo; updated atomically with the events it governs.
- [ADR-IC-009](../../integration_concepts/adrs/ADR-IC-009-testing-infrastructure.md) — the Testcontainers + consumer-driven-contract CI gates the §P2 hooks mirror.
- [ADR-IC-010](../../integration_concepts/adrs/ADR-IC-010-mcp-server-runtime-and-sdk.md) — the engine's runtime MCP server, distinct from dev-time MCP (§P5).
- [01 §1, §3, §6](../01-product-architecture.md) — one-engine-many-families thesis; the three-owner config split; "one codebase, one set of images."
- [feature-design-c4-architecture](../feature-design-c4-architecture.md) — the build/estate/external split that bounds what is ours to repo.

---

*Decided 2026-05-23 by jhosm.*
