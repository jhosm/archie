# ADR-PC-020: LLM Build Toolchain and Spec-Conformance Governance — Agent Tooling, Verifiable Commitments, and an Explicit-Drift Gate

| Field | Value |
|---|---|
| Status | Accepted |
| Date | 2026-05-23 |
| Deciders | jhosm |
| Shape | Tool-selection |
| Common criteria | [ADR-IC-000](../../integration_concepts/adrs/ADR-IC-000-common-evaluation-criteria.md) (reused per [ADR-PC-000](./ADR-PC-000-namespace-and-contract-shape-framework.md) D2; this is the [ADR-PC-000 §D3](./ADR-PC-000-namespace-and-contract-shape-framework.md) "operational discipline" residual category — an engineering-practice decision, declared tool-selection per the [§D4](./ADR-PC-000-namespace-and-contract-shape-framework.md) default) |
| Depends on | [ADR-PC-019](./ADR-PC-019-repository-strategy-monorepo.md) (the single tree this toolchain and governance run on — its atomic-change property is what lets a contract + every consumer + commitment test land together), [ADR-PC-010](./ADR-PC-010-dotnet-hand-rolled-engine.md) (LLM-codability + the lean, fully-owned posture this extends to tooling; the determinism + `Money` analysers the hooks mirror), [ADR-PC-000 §D5](./ADR-PC-000-namespace-and-contract-shape-framework.md) (the amend/supersede lifecycle the drift gate makes mechanical; its [`Verifiable commitments`](./ADR-PC-000-namespace-and-contract-shape-framework.md) template slot), [ADR-IC-009](../../integration_concepts/adrs/ADR-IC-009-testing-infrastructure.md) (the Testcontainers + Pact CDC stack that hosts the runtime fitness functions), [ADR-IC-008](../../integration_concepts/adrs/ADR-IC-008-event-catalog-governance-tooling.md) / [ADR-IC-002](../../integration_concepts/adrs/ADR-IC-002-schema-format-and-registry.md) (EventCatalog + schema-registry, the boundary contract layer), [ADR-IC-013](../../integration_concepts/adrs/ADR-IC-013-in-house-estate-build-and-repository-placement.md) (the in-house estate this governance also covers), [07-testing-strategy](../../integration_concepts/07-testing-strategy.md) (the five-level pyramid the commitments map onto) |
| Guards (representative) | [ADR-PC-001 §P2](./ADR-PC-001-event-store-technology.md), [ADR-PC-009](./ADR-PC-009-per-instance-version-pinning.md), [ADR-PC-010 §P1–§P5](./ADR-PC-010-dotnet-hand-rolled-engine.md), [ADR-PC-012](./ADR-PC-012-gl-posting-signal-contract.md) / [ADR-PC-014](./ADR-PC-014-customer-notification-emit-contract.md) / [ADR-PC-015](./ADR-PC-015-ifrs9-signal-contract.md) (post-flag-never-gates), [ADR-PC-013](./ADR-PC-013-aml-kyc-upstream-precondition.md), [ADR-PC-017](./ADR-PC-017-legacy-batch-ingest-contract.md), [event-store §8.2](../feature-design-event-store-projections.md) (replay budgets), [01 §3](../01-product-architecture.md) (the wedge's two falsifiable claims) |
| Resolves | bd `archie-10r.20` (LLM build toolchain half) + `archie-10r.21` (spec-conformance and drift governance) |

---

## Context

This engine is specified before it is built and authored primarily by an LLM ([ADR-PC-010](./ADR-PC-010-dotnet-hand-rolled-engine.md) LLM-codability criterion), on one monorepo ([ADR-PC-019](./ADR-PC-019-repository-strategy-monorepo.md)). Two build-strategy questions fall out of that fact, and they are **decisionally** coupled — not merely thematically — because the answer to the second is built out of the answer to the first:

1. **Agent toolchain** — which Claude Code primitives (hooks, skills, subagents, a plugin) and which agent-orchestration surface operationalise an LLM-first build?
2. **Spec-conformance** — how is the LLM-authored implementation kept faithful to a large specification, and how is genuine divergence forced into the open rather than landing silently in code?

The coupling is structural: the conformance regime is *made of* toolchain primitives. The explicit-drift gate is a hook; the conformance reviewer is a subagent; the coverage checker is a hook + CI step; the spec-first authoring loop is a skill. Deciding conformance without deciding the toolchain that enforces it would leave the mechanism unspecified; deciding the toolchain without the conformance regime would leave it purposeless. So one ADR settles both — and [ADR-PC-019](./ADR-PC-019-repository-strategy-monorepo.md) (repository strategy) is the separable decision that was correctly split off.

The specification is large: ~19 ADR-PC + ~13 ADR-IC entries, the concept docs, the feature-design notes, the event contract ([02 §2.4](../02-v1-scope-term-deposits.md)), the financial mathematics, and the C4 model — most cross-linked, many carrying *falsifiable* commitments. With the integration estate now also in scope to build (the in-house ADR-IC components, classified and placed in the monorepo by [ADR-IC-013](../../integration_concepts/adrs/ADR-IC-013-in-house-estate-build-and-repository-placement.md) — saga orchestrator [IC-003], outbox [IC-004], MCP server [IC-010], notification service [IC-011], ACL [IC-012]), the body of decisions the implementation must honour spans **both** ADR namespaces.

The risk this ADR addresses is **drift**: the implementation silently diverging from a decision — a handler that reads the clock (violating [ADR-PC-010 §P5](./ADR-PC-010-dotnet-hand-rolled-engine.md) determinism), a consumer reject that unwinds a deposit (violating the post-flag-never-gates contract of [ADR-PC-012](./ADR-PC-012-gl-posting-signal-contract.md)/[014](./ADR-PC-014-customer-notification-emit-contract.md)/[015](./ADR-PC-015-ifrs9-signal-contract.md)), a second non-atomic write at constitution (violating [ADR-PC-001 §P2](./ADR-PC-001-event-store-technology.md)/[009](./ADR-PC-009-per-instance-version-pinning.md)) — none of which a compiler or a boundary contract test would necessarily catch. The goal is twofold: **prevent silent drift where mechanically possible, and where divergence is genuine, force it to be acknowledged** — never silent.

Two existing assets make this tractable, and this ADR builds on rather than replaces them:

1. **The commitments are already falsifiable.** Project discipline (bd memory `product-concepts-no-calendar-effort`) forces concept-doc claims to be *falsifiable internal commitments* — replay budgets (cold replay 5s with-a-plan / 30s irregular, [event-store §8.2](../feature-design-event-store-projections.md)), "zero engine code per variant", "≤ 5 working days PM commit to production" ([01 §3](../01-product-architecture.md)) — not vague intent. A falsifiable claim is one step from an executable check.
2. **The acknowledgment machinery already exists.** The [ADR-PC-000 §D5](./ADR-PC-000-namespace-and-contract-shape-framework.md) amend/supersede lifecycle, the "source wins" rule ([feature-design-c4-architecture](../feature-design-c4-architecture.md)), and the live precedent of [ADR-PC-010 Open Action #4](./ADR-PC-010-dotnet-hand-rolled-engine.md) (the §10.4 "no in-house build" tension tracked as a dated, acknowledged deferral) show the project already records divergence explicitly. This ADR makes that mechanical and unskippable.

This entry is the [ADR-PC-000 §D3](./ADR-PC-000-namespace-and-contract-shape-framework.md) **residual category** (engineering-practice discipline, declared tool-selection per §D4), like [ADR-PC-019](./ADR-PC-019-repository-strategy-monorepo.md) and [ADR-PC-011](./ADR-PC-011-in-house-load-test-harness.md). The honest consequence, surfaced up front as [ADR-PC-009](./ADR-PC-009-per-instance-version-pinning.md) did: **F1 and F2 do not discriminate for either question.** Claude Code hooks/skills/agents are dev-environment configuration with no licence or runtime-regulatory surface; the conformance mechanisms are dev-time discipline and free tooling; the runtime fitness functions reuse the already-chosen Pact + Testcontainers stack ([ADR-IC-009](../../integration_concepts/adrs/ADR-IC-009-testing-infrastructure.md)); nothing is bought and nothing touches a regulated runtime surface. The load-bearing questions are **which toolchain keeps the engine lean and fully owned at 1–2-person, LLM-first scale**, and **which governance approach actually prevents silent drift** — both decided on S2 coherence plus project-specific correctness analysis, not on the hard filters.

**Candidates evaluated — two axes.**

*Agent toolchain:*

| # | Candidate | Notes |
|---|---|---|
| T-A | **Claude Code primitives only** — hooks + skills + subagents + one project plugin; Claude Code as the sole agent-orchestration surface; Git worktrees for parallelism | Lean, fully owned, no second runtime. Matches the [ADR-PC-010](./ADR-PC-010-dotnet-hand-rolled-engine.md) hand-rolled-core posture applied to tooling. |
| T-B | **Claude Code + a second multi-agent orchestrator** — adopt a heavyweight orchestration framework on top | Buys parallel-agent orchestration the project has one use for (independent family-schema work) at the cost of a second stack to own and keep current. |
| T-C | **Minimal / no tooling** — rely on the model plus generic review | Zero setup, but leaves the conformance regime (D2/D3) with no enforcement substrate; the model's memory becomes the gate. |

*Spec-conformance:*

| # | Candidate | Notes |
|---|---|---|
| C-A | **Layered conformance** — machine-checkable Verifiable-commitments per ADR (fitness functions) + code↔ADR traceability + a design-time conformance gate + the explicit-drift (amend/supersede) rule | Defence in depth: prevent where mechanical, detect where not, acknowledge where genuine. Reuses the [ADR-IC-009](../../integration_concepts/adrs/ADR-IC-009-testing-infrastructure.md) test stack and the T-A toolchain. |
| C-B | **Contract-tests-only** — rely on the [ADR-IC-009](../../integration_concepts/adrs/ADR-IC-009-testing-infrastructure.md) two-layer gate (schema registry + Pact CDC) as the sole drift guard | Catches drift that crosses a bounded-context boundary. Adds nothing for internal-design decisions (determinism, rounding, atomicity, one-engine-many-families). |
| C-C | **Review-only / convention** — rely on human + agent review against the ADRs, no machine-checkable commitments, no traceability | Cheapest to start. Enforcement = vigilance, which is exactly what erodes over a long LLM-authored build. |
| C-D | **Formal specification / model-checking** — encode invariants in TLA⁺ / a proof system and machine-verify | Strongest guarantee in principle. Disproportionate for a 1–2-person team; most commitments are about *code behaviour*, already exercisable as tests against real infrastructure. |

---

## Evaluation

### Hard filter results

#### F1 · Cost / licensing

| Axis · Candidate | Licence / cost | Verdict |
|---|---|---|
| Toolchain · T-A / T-B / T-C | Claude Code primitives are dev config; Git worktrees are free; a second orchestrator (T-B) is open-source. Zero incremental licence cost in every case. | **Pass** (all) |
| Conformance · C-A | ADR annotations + a coverage checker (engine-team code); runtime fitness functions on the already-chosen Pact/Testcontainers ([ADR-IC-009](../../integration_concepts/adrs/ADR-IC-009-testing-infrastructure.md)). Zero incremental cost. | **Pass** |
| Conformance · C-B | Already-chosen stack. Zero cost. | **Pass** |
| Conformance · C-C | Zero cost. | **Pass** |
| Conformance · C-D | Tooling is open (TLA⁺ etc.), but the cost is *engineering time* at 1–2-person scale, not licence. | **Pass** (no licence bar) |

Uniform pass — F1 does not discriminate on either axis (no candidate buys anything).

#### F2 · Regulatory fit (GDPR / DORA / PSD2)

Neither a toolchain nor a governance strategy carries PII or is itself a regulated runtime artefact. There is one regulatory-adjacent property worth naming: DORA expects resilience and correctness behaviours to be **demonstrably tested, not assumed** ([ADR-IC-009](../../integration_concepts/adrs/ADR-IC-009-testing-infrastructure.md) frames its fault-injection tests as DORA artefacts), and PSD2/audit expects decisions about money handling to be traceable. Every candidate *permits* that; they differ in how *completely* the obligation is met — a correctness/coverage property, not a hard-filter pass/fail.

| Axis · Candidate | Verdict | Note |
|---|---|---|
| Toolchain · T-A / T-B / T-C | **Pass** | Dev tooling; no runtime regulatory surface. |
| Conformance · C-A | **Pass** | Makes "this commitment is tested" first-class and auditable per ADR — the strongest demonstrable-conformance posture. |
| Conformance · C-B | **Pass** | Demonstrates boundary contracts; silent on internal-decision conformance. |
| Conformance · C-C | **Pass** | No mechanical evidence trail; conformance is asserted, not demonstrated. |
| Conformance · C-D | **Pass** | Strongest evidence for the invariants modelled; says nothing about the unmodelled remainder. |

All clear the hard filters. The decision is entirely in S2 and the correctness analyses below — the expected shape for the [ADR-PC-000 §D3](./ADR-PC-000-namespace-and-contract-shape-framework.md) residual category.

---

### Soft criteria — toolchain axis

#### T-A · Claude Code primitives only — **CHOSEN**

**S1 · Operational complexity.** Lowest. Hooks, skills, and subagents are configuration in the repo; nothing else to deploy or keep running. **S2 · Ecosystem coherence — decisive.** The primitives map one-to-one onto *how a rule is enforced* (deterministic always-rule → hook; judgement-bearing procedure → skill; context-isolated review → subagent), and they are the substrate the conformance regime (D2/D3) is built from — so the toolchain and the governance compose without glue. **S3 · Exit cost.** Low — hooks/skills are scripts and prompts; abandoning them leaves ordinary Git and ordinary tests behind. **S4 · Longevity.** Inherits Claude Code's; no second-vendor dependency. **Decisive reason.** This mirrors the [ADR-PC-010](./ADR-PC-010-dotnet-hand-rolled-engine.md) hand-rolled-core posture applied to tooling: keep the agent stack lean and fully owned, reach for more orchestration only against a concrete wall. Claude Code subagents + Git worktrees cover the one parallelism case in sight (independent family-schema work), so there is no wall.

#### T-B · Claude Code + a second orchestrator — **rejected**

Buys multi-agent orchestration the project has exactly one use for, at the cost of a second stack to own, version, and keep current — against the lean posture, and with no parallelism need that worktrees do not already meet. Rejected on S1 + the PC-010 posture, no offsetting gain.

#### T-C · Minimal / no tooling — **rejected**

Leaves D2/D3 with no enforcement substrate: the model's memory becomes the gate, which is precisely the failure mode the conformance regime exists to remove. Rejected — it is not a viable floor once conformance is layered (C-A).

### Soft criteria — conformance axis

#### C-A · Layered conformance — **CHOSEN**

**S1 · Operational complexity.** Moderate, and front-loadable. The expensive part — fitness functions — mostly *already exists* as obligations: the [07-testing-strategy](../../integration_concepts/07-testing-strategy.md) pyramid, the [ADR-PC-001](./ADR-PC-001-event-store-technology.md) projection-rebuild drills, the Q-AK load test ([ADR-PC-011](./ADR-PC-011-in-house-load-test-harness.md)). The net-new work is *cataloguing* each commitment and asserting it has a gate, plus a thin coverage checker and ADR annotations. It scales down: seed the ~8 load-bearing invariants first, grow the catalogue as ADRs are implemented.

**S2 · Ecosystem coherence — decisive.** The strategy slots into machinery already chosen: fitness functions run on the [ADR-IC-009](../../integration_concepts/adrs/ADR-IC-009-testing-infrastructure.md) Testcontainers/Pact stack at the right pyramid level; the explicit-drift gate mechanises the [ADR-PC-000 §D5](./ADR-PC-000-namespace-and-contract-shape-framework.md) lifecycle that already governs ADR changes; the conformance agent and the §D5 hook are T-A toolchain items (§P1, §P3); the monorepo ([ADR-PC-019](./ADR-PC-019-repository-strategy-monorepo.md)) makes a contract + its consumers + its commitment-test changeable in one atomic commit. Nothing is bolted on sideways.

**S3 · Exit cost.** Low. Verifiable-commitments are prose-plus-test-IDs in the ADRs; annotations are comments; the coverage checker is a small script. Abandoning the strategy leaves ordinary tests and ordinary ADRs behind — no lock-in.

**S4 · Longevity.** Inherits the longevity of its substrates (the test stack, the ADR corpus, the monorepo). No single-vendor dependency.

**Decisive reason — drift prevention is layered because drift is layered.** Two *different* classes of drift need two *different* guards, and a third mechanism for the residue:
- **Boundary drift** (a schema-valid but semantically-wrong change crossing a bounded context) → the **runtime two-layer gate**: schema-registry compatibility ([ADR-IC-002](../../integration_concepts/adrs/ADR-IC-002-schema-format-and-registry.md)) + Pact CDC ([ADR-IC-009](../../integration_concepts/adrs/ADR-IC-009-testing-infrastructure.md)).
- **Internal-design drift** (legal code that contradicts a decision — a clock read in a handler, a rounding at the wrong place, a second write at constitution) → the **design-time conformance gate**: Verifiable-commitments-as-fitness-functions + the ADR-conformance review agent. No contract test sees this class.
- **Genuine divergence** (the decision itself should change) → the **explicit-drift rule**: it cannot land without an amend/supersede in the same change, so drift becomes a dated ADR edit, never a silent code fact.

#### C-B · Contract-tests-only — **rejected**

The [ADR-IC-009](../../integration_concepts/adrs/ADR-IC-009-testing-infrastructure.md) gate is necessary and retained — but it guards *boundaries*. It says nothing about decisions that live entirely inside a component: determinism ([ADR-PC-010 §P5](./ADR-PC-010-dotnet-hand-rolled-engine.md)), round-once-at-the-Money-boundary ([§P1–§P2](./ADR-PC-010-dotnet-hand-rolled-engine.md)), append+outbox atomicity ([ADR-PC-001 §P2](./ADR-PC-001-event-store-technology.md)), pin-per-event ([ADR-PC-009](./ADR-PC-009-per-instance-version-pinning.md)), one-engine-many-families ([01 §1](../01-product-architecture.md)). A change can be fully contract-compatible and still violate any of these. C-B leaves the larger drift surface unguarded; it is a layer of C-A, not an alternative to it.

#### C-C · Review-only / convention — **rejected**

C-C relies on a reviewer (human or agent) remembering ~30 ADRs and noticing a contradiction every time. Over a long, fast, LLM-authored build that vigilance is exactly what erodes — and an undetected internal-design drift is precisely the failure mode C-A exists to prevent. C-C also leaves no audit trail of *which* commitments are actually tested, weakening the DORA/PSD2 demonstrable-conformance posture. The conformance agent is valuable, but only *on top of* machine-checkable commitments that give it a concrete checklist rather than open-ended inference.

#### C-D · Formal specification / model-checking — **rejected (kept as a targeted future option)**

The strongest guarantee for what it models, but disproportionate at 1–2-person scale, and a poor fit for commitments that are about *runtime behaviour against real infrastructure* (PostgreSQL `ON CONFLICT`, outbox `SKIP LOCKED`, Redpanda rebalance) — those are most credibly verified by [ADR-IC-009](../../integration_concepts/adrs/ADR-IC-009-testing-infrastructure.md)-style integration tests, not a model. Reserved for a *specific* invariant later if one proves worth it (e.g. saga-compensation money-conservation as a property-based/TLA⁺ check), consistent with [07-testing-strategy §4.3](../../integration_concepts/07-testing-strategy.md) putting property-based tests at the top of the pyramid, "where you end up, not where you start." Adopting it wholesale now contradicts the lean, fully-owned posture of [ADR-PC-010](./ADR-PC-010-dotnet-hand-rolled-engine.md)/T-A.

**Decisive reasons.** *Toolchain:* T-A keeps the agent stack lean and fully owned, the PC-010 posture applied to tooling, with the one parallelism case covered by worktrees. *Conformance:* drift is layered, so the guard must be layered — C-B guards only boundaries; C-C guards only by vigilance; C-D guards only the modelled fragment. C-A composes the runtime boundary gate, the design-time conformance gate, and the explicit-drift rule so that every class of drift is either prevented, detected, or forced into the open — built out of the T-A primitives.

---

## Decision

This ADR makes **three coupled decisions**.

### D1 — Agent toolchain: **Claude Code is the sole agent-orchestration surface**; tooling is layered by enforcement mechanism; no second orchestration framework.

The build is operationalised with Claude Code primitives chosen by *how the rule is enforced*:

- **Deterministic always-rules → hooks** (the harness enforces them; the model cannot forget).
- **Judgement-bearing repeatable procedures → skills** (model-invoked, like the existing `create_backlog`).
- **Context-isolated review and parallel work → subagents** (domain-specialised review the generic review toolkit does not cover).
- **Packaging → one project plugin** (`babelstone-engine`) bundling the above, *once they stabilise*.

No heavyweight second multi-agent orchestrator is adopted. Claude Code subagents + Git worktrees cover the one parallelism case in sight (independent family-schema work). The per-primitive inventory is specified in §P1–§P4.

### D2 — Adopt layered spec-conformance: Verifiable commitments + traceability + a design-time conformance gate, composed with the runtime [ADR-IC-009](../../integration_concepts/adrs/ADR-IC-009-testing-infrastructure.md) contract gate.

Each ADR (PC and IC) gains a machine-checkable list of its load-bearing commitments, each bound to the test/gate that proves it (fitness functions). Implementing code is annotated with the ADR sections it satisfies, and a coverage checker asserts the two stay connected. The ADR-conformance agent (§P3) reviews every change against the governing ADRs for the internal-design class that no contract test catches. The runtime two-layer gate (schema registry + Pact) is retained as the boundary guard. Decided on **S2 coherence** (reuses the chosen test stack, the §D5 lifecycle, and the monorepo's atomic-change property) and **drift-prevention correctness** (boundary, internal-design, and genuine divergence each have a guard).

### D3 — The explicit-drift gate: no change may contradict an Accepted ADR without an amendment or supersession in the same change.

Divergence from an Accepted decision is permitted — the project expects decisions to evolve — but only *on the record*. A change that contradicts an Accepted ADR must carry, in the same PR, either a dated amendment to that ADR or a superseding ADR (per [ADR-PC-000 §D5](./ADR-PC-000-namespace-and-contract-shape-framework.md)); a deliberate, time-bounded deferral is recorded in [04-open-questions](../04-open-questions.md) (the existing register, the home of the [ADR-PC-010 Open Action #4](./ADR-PC-010-dotnet-hand-rolled-engine.md)-style acknowledged tension). Enforced by the §D5 immutability hook (§P1) + the conformance agent (§P3) + the PR-body "ADRs touched/honoured" gate (§P1). This is the structural form of "if it drifts, it is explicitly acknowledged."

**Rejected: a second agent-orchestration framework** — against the lean, fully-owned-tooling posture; no current need Claude Code + worktrees does not meet. **Rejected: contract-tests-only** — guards boundaries, blind to internal-design drift; retained as a layer, not the whole. **Rejected: review-only** — enforcement by vigilance is what erodes; kept only as the conformance agent *on top of* machine-checkable commitments. **Rejected (deferred): formal methods** — disproportionate now; reserved for a specific invariant later.

---

## Implementation Principles

### P1 — Hooks: deterministic always-rules the harness enforces, mirroring authoritative gates

Hooks surface — at edit/commit time — checks that are *already* authoritative, so the hook is a faster mirror and never the source of truth:

- **`*.puml` → re-render SVG.** Already implemented as the `.githooks/pre-commit` hook ([feature-design-c4-architecture §PlantUML](../feature-design-c4-architecture.md), `CLAUDE.md`); a Claude `PostToolUse` hook is optional faster feedback, not a second authority.
- **Engine handler edits → run the determinism gate + `Money`/`decimal` Roslyn analysers** ([ADR-PC-010 §P1–§P2, §P5](./ADR-PC-010-dotnet-hand-rolled-engine.md)). These are CI gates; a hook flags violations inline before commit. Enforcement lives in the analyser, not in the model's memory.
- **§D5 ADR-immutability hook → block in-place edits to an Accepted ADR's `## Decision`** unless the same change carries a dated amendment or a superseding ADR ([ADR-PC-000 §D5](./ADR-PC-000-namespace-and-contract-shape-framework.md)). This is the mechanical half of D3.
- **PR-body "ADRs touched/honoured" gate → require the PR description to name the ADRs the change implements, amends, or honours**, so review starts from the decision, not the diff (the other half of D3).
- **Coverage-checker hook → run the §P6 ADR↔code↔test checker** before commit (CI is authoritative; the hook is the fast mirror).
- **Session-end → print the mandatory push protocol** (`git pull --rebase` → `bd dolt push` → `git push` → verify clean) from `CLAUDE.md` / the bd session-close protocol.
- **`TodoWrite` / `TaskCreate` → block with "use `bd`."** `CLAUDE.md` and `bd prime` prohibit them; a `PreToolUse` hook makes the prohibition mechanical.
- **Markdown edits under `adrs/` → cross-link + ADR-number lint** (the [ADR-PC-000 §D5](./ADR-PC-000-namespace-and-contract-shape-framework.md) link-pattern-by-location rules and the disk+bd dual-number-check).

### P2 — Skills: model-invoked procedures (the existing `create_backlog` is the template)

In leverage order:

1. **`new-family-schema`** — *highest leverage.* Scaffolds a family's event types + pure handlers + projections + lifecycle state machine + replay fixtures, using `term_deposit` as the reference. The entire "one engine, many families" thesis ([01 §1](../01-product-architecture.md), [event-store §3](../feature-design-event-store-projections.md)) means product velocity = time-to-correct-family-schema while the engine code stays still. This is where LLM speed compounds.
2. **`new-event`** — enforces the `<Entity><PastParticipleVerb>` convention ([02 §2.4](../02-v1-scope-term-deposits.md), [integration_concepts §08](../../integration_concepts/08-event-catalog-governance.md)), generates the Avro schema, registers it in EventCatalog, adds the envelope fields, and adds a registry backward-compatibility check.
3. **`new-adr`** — automates the disk+bd dual-number-check (the `adr-numbering-check-disk-and-bd` bd memory), selects the [ADR-PC-000](./ADR-PC-000-namespace-and-contract-shape-framework.md) shape (tool-selection eval table vs contract-shape six slots), wires cross-links per the location rules, updates the ADR README index, and seeds the `Verifiable commitments` section.
4. **`amend-adr` / `supersede-adr`** — the D3 companion: makes the [ADR-PC-000 §D5](./ADR-PC-000-namespace-and-contract-shape-framework.md) amend/supersede a one-command step (dated amendment block, or a new superseding ADR with the back-link and status flip), so acknowledging drift is cheap enough that nobody is tempted to skip it.
5. **`pack-author`** — scaffolds CUE schema + YAML data, runs `pack-validate` depths 1–4, cosign-signs, and `oras`-pushes ([ADR-PC-006](./ADR-PC-006-cue-schema-language.md), [ADR-PC-007](./ADR-PC-007-signed-yaml-oci-pack.md)).

### P3 — Subagents: domain-specialised review the generic toolkit does not cover

- **ADR-conformance agent** — the design-time drift guard: reviews every change against the governing ADRs (PC + IC) for the internal-design class no contract test catches, and when it finds a genuine contradiction, proposes the §P2 `amend-adr`/`supersede-adr` rather than silently passing. The mechanical gates carry the load-bearing invariants; this agent covers the long tail.
- **spec-coverage auditor** — sweeps periodically (not per-push) for ADRs with **no** implementing code (decided-but-unbuilt) and code paths governed by an ADR with **no** commitment test; treats a no-commitment ADR as a finding (§P6).
- **financial-math-reviewer** — checks kernel/handler changes against [financial_concepts](../../financial_concepts/banking_products_financial_mathematics.md): Act/360, the TANB/TANL split, **withholding applied flow-by-flow not by rate-scaling** (the subtle §5.4 rule), the TAE formula, and round-once-at-the-`Money` boundary ([ADR-PC-010 §P2](./ADR-PC-010-dotnet-hand-rolled-engine.md)).
- **contract-reviewer** — event/schema changes against the [§09](../../integration_concepts/09-long-term-schema-evolution.md) forward-only evolution rules (backward-compatible, or V2-in-parallel), the naming convention, and the **no-PII-on-the-durable-bus** rule (references only; resolve internally — [ADR-PC-014](./ADR-PC-014-customer-notification-emit-contract.md), [ADR-PC-004](./ADR-PC-004-pii-crypto-shredding.md)).
- **replay/determinism-auditor** — handlers pure (no clock, no I/O)? projections rebuildable folds? fixture replay still green? ([event-store §5.3, §10.3](../feature-design-event-store-projections.md)).
- **doc-consistency** — checks the heavily cross-linked docs and the C4 diagrams against their cited sources, honouring the [feature-design-c4-architecture](../feature-design-c4-architecture.md) "if this view and a cited source disagree, the source wins" rule.

### P4 — Packaging and the runtime-vs-dev MCP distinction; orchestration stance

- **One project plugin** (`babelstone-engine`) bundles P1–P3 once stable; do not package prematurely (prove loose first, then version with the repo).
- **Dev-time MCP is lean:** `github` (PR flow) + `context7` (library docs) suffice; `bd` is a CLI — allowlist it, no MCP needed. Unrelated plugins (analytics, browser, error-tracking) are noise for a backend banking engine.
- **The engine's own MCP server ([ADR-IC-010](../../integration_concepts/adrs/ADR-IC-010-mcp-server-runtime-and-sdk.md)) is a *runtime product deliverable*** — an untrusted agent channel into the engine's command/query surface, built in-house and placed in the monorepo by [ADR-IC-013](../../integration_concepts/adrs/ADR-IC-013-in-house-estate-build-and-repository-placement.md) — **not** dev tooling. The two must never be conflated: one ships to the bank and is IAM-gated; the other configures the developer's machine.
- **No second orchestration framework.** Claude Code subagents + Git worktrees cover parallel family-schema work; revisit only against a concrete wall (consistent with the [ADR-PC-010](./ADR-PC-010-dotnet-hand-rolled-engine.md) lean, fully-owned posture).

### P5 — Every ADR carries a `Verifiable commitments` section binding each load-bearing claim to a gate

The [ADR-PC-000](./ADR-PC-000-namespace-and-contract-shape-framework.md) template slot lists each load-bearing commitment, the pyramid level/gate that verifies it, and a stable test identifier. Illustrative shape:

```
## Verifiable commitments
| # | Commitment | Gate (level) | Test ID |
|---|---|---|---|
| C1 | append + outbox commit in one local txn (PC-001 §P2) | integration / Testcontainers | ES_ATOMIC_APPEND_OUTBOX |
| C2 | Money rounds HALF_EVEN exactly once at the Decimal→Cents boundary | unit + analyser | MONEY_BOUNDARY_FIXTURES |
| C3 | a handler that reads the clock / does I/O fails the build | CI determinism gate | DETERMINISM_GATE |
```

Not every ADR needs many; contract-shape ADRs may have one or two (e.g. "post-flag never gates"). A commitment with no gate is a known hole, listed as such — visibility is the point.

### P6 — Code is annotated with the ADR sections it satisfies; a coverage checker keeps spec and code connected

Implementing sites carry a lightweight anchor (`// ADR-PC-001 §P2`). A coverage checker (a §P1 hook + CI step) asserts:

- every `Verifiable commitment` resolves to ≥1 test that exists and runs, and (where applicable) ≥1 code anchor;
- every ADR anchor in code points to a **live** (non-superseded) ADR section;
- the spec-coverage auditor (§P3) sweeps periodically for ADRs with **no** implementing code and code paths governed by an ADR with **no** commitment test.

This catches drift in both directions — spec without code, and code that has quietly outgrown its spec.

### P7 — Fitness functions live at the right pyramid level; most already have a home

Commitments map onto the [07-testing-strategy](../../integration_concepts/07-testing-strategy.md) levels rather than spawning a parallel suite:

| Commitment class | Level / mechanism |
|---|---|
| Money rounding, determinism, aggregate invariants | Unit + Roslyn analysers ([ADR-PC-010 §P1–§P2, §P5](./ADR-PC-010-dotnet-hand-rolled-engine.md)) |
| Append+outbox atomicity, pin-per-event replay, idempotent batch ingest ([ADR-PC-017](./ADR-PC-017-legacy-batch-ingest-contract.md)) | Integration / Testcontainers |
| Post-flag-never-gates ([ADR-PC-012](./ADR-PC-012-gl-posting-signal-contract.md)/[014](./ADR-PC-014-customer-notification-emit-contract.md)/[015](./ADR-PC-015-ifrs9-signal-contract.md)), AML-edge-precondition ([ADR-PC-013](./ADR-PC-013-aml-kyc-upstream-precondition.md)), no-PII-on-bus | Contract / saga tests |
| Replay budgets 5s/30s ([event-store §8.2](../feature-design-event-store-projections.md)) | Benchmark gate (nightly, not per-push) |
| "Zero engine code per variant" ([01 §3](../01-product-architecture.md)) | Acceptance test: add a family schema → assert **zero `/engine` diff** |

The financial-math kernel gets a **golden-fixture corpus** derived from the [financial_concepts](../../financial_concepts/banking_products_financial_mathematics.md) worked examples (simple/compound interest, TAE, TANB/TANL split, flow-by-flow withholding), guarded by the §P3 financial-math-reviewer.

### P8 — Two guards, because they catch different drift; neither subsumes the other

- **Runtime boundary guard** — the [ADR-IC-009](../../integration_concepts/adrs/ADR-IC-009-testing-infrastructure.md) two-layer gate: schema-registry compatibility ([ADR-IC-002](../../integration_concepts/adrs/ADR-IC-002-schema-format-and-registry.md)) catches structural breaks at publish; Pact CDC catches behavioural breaks (a nulled `correlation_id`, an inverted amount sign) in the producer's CI. Applies to every contract crossing a bounded context — and with the estate now in-house ([ADR-IC-013](../../integration_concepts/adrs/ADR-IC-013-in-house-estate-build-and-repository-placement.md)), that includes engine↔ACL, engine↔MCP, and engine↔downstream-consumer.
- **Design-time conformance guard** — Verifiable-commitments + the ADR-conformance agent (§P3): catches a change that is fully contract-compatible yet contradicts an internal decision.

A contract test would pass a handler that newly reads the clock; the determinism gate + conformance agent fail it. A determinism gate says nothing about a consumer's expectation that `amount` is positive; Pact does. Both are mandatory.

### P9 — The explicit-drift workflow is the same shape as the existing ADR lifecycle

When implementation reveals a decision is wrong or incomplete, the change to the code and the change to the decision land **together**:

1. The conformance agent (or the §P1 §D5 hook) flags that the diff contradicts an Accepted ADR.
2. The author resolves it one of two ways — **fix the code** to conform, or **amend/supersede the ADR** (the §P2 `amend-adr`/`supersede-adr` skill) in the same PR. A time-bounded, deliberate gap is recorded in [04-open-questions](../04-open-questions.md).
3. The PR-body "ADRs touched/honoured" section (§P1) names the ADRs implemented or amended, so review starts from the decision, not the diff.

This mirrors the project's established order — ADR before code, bd issue before code — extended to: **no contradiction without a recorded decision.**

### P10 — Spec-first development loop

The default authoring loop for engine work, encoded in the §P2 skills:

> ADR (or amendment) → add/confirm the `Verifiable commitment` as a *failing* fitness function → implement until green → coverage checker confirms the anchor.

A failing commitment-test written first turns the ADR's prose into the executable definition of done, and makes "implemented" mean "the commitment is demonstrably met," not "the code looks right."

### P11 — Governance spans both ADR namespaces

Because the integration estate is built in-house ([ADR-IC-013](../../integration_concepts/adrs/ADR-IC-013-in-house-estate-build-and-repository-placement.md): saga orchestrator, outbox, MCP, notification, ACL — five components), the coverage checker, conformance agent, and explicit-drift gate apply to **ADR-IC** entries (orchestrator, outbox, ACL, MCP, observability, testing) exactly as to ADR-PC. The §D5 lifecycle and the dual-namespace numbering hygiene ([ADR-PC-000 §D1](./ADR-PC-000-namespace-and-contract-shape-framework.md)) already cover both; this ADR's mechanisms inherit that scope.

---

## Consequences

**What this choice makes easier:**

1. **Lean, fully-owned tooling.** One agent surface, no second orchestrator to keep current; the toolchain evolves with the contracts it enforces.
2. **Drift becomes mechanical, not a matter of memory.** Internal-design contradictions fail a gate or an agent review; boundary contradictions fail Pact/registry; genuine divergence fails the §D5 gate unless an ADR edit rides along.
3. **"Implemented" gains a precise meaning.** A commitment is met when its fitness function is green and its anchor resolves — not when the code reads plausibly.
4. **Demonstrable conformance for audit.** The Verifiable-commitments catalogue is a per-ADR, per-commitment evidence trail — the demonstrable-resilience/correctness posture DORA/PSD2 expect ([ADR-IC-009](../../integration_concepts/adrs/ADR-IC-009-testing-infrastructure.md) framing).
5. **The fast path is the conformant path.** The spec-first loop + the §P2 skills make writing a conformant family schema or event the easiest way to build, which is where the LLM-first velocity compounds safely.

**What this choice makes harder or impossible:**

1. **Authoring overhead per ADR.** Every ADR now owes a `Verifiable commitments` section, and every load-bearing commitment owes a test. Intentional — an untested commitment is a latent drift hole — but it is real work. Mitigation: seed the ~8 load-bearing invariants first; grow the catalogue as ADRs are implemented (S1).
2. **A commitment catalogue can rot.** A stale or wrongly-mapped commitment is itself a drift. Mitigation: the coverage checker fails on a commitment whose test does not exist/run, and on an anchor to a superseded ADR.
3. **The explicit-drift gate adds friction to "just fix it" changes.** A quick code change that happens to contradict an ADR now also demands an ADR edit. That friction is the point (it is the acknowledgment), but it must be cheap — the `amend-adr` skill (§P2) exists to keep it a one-command step.
4. **One plugin couples tooling versions to the repo.** Intentional — the toolchain should evolve with the contracts it enforces — but it means tooling is not independently reusable across projects without extraction.

**Residual risks:**

- **Commitment coverage is only as good as the catalogue.** A load-bearing decision nobody listed as a commitment is unguarded by P5–P7 (though the conformance agent may still catch it). Mitigation: the spec-coverage auditor (§P3) sweeps for ADRs with no commitments and for governed code with no commitment test; treat a no-commitment ADR as a finding.
- **Conformance-agent fallibility.** An LLM reviewer can miss a contradiction or raise a false one. Mitigation: it is a *layer*, not the sole guard — the mechanical gates (analysers, determinism gate, Pact, the coverage checker) carry the load-bearing invariants; the agent covers the long tail and proposes the amend/supersede.
- **Benchmark-gate flakiness.** Replay-budget fitness functions ([event-store §8.2](../feature-design-event-store-projections.md)) are timing-sensitive. Mitigation: run them nightly on a stable runner (not per-push), as [ADR-PC-011](./ADR-PC-011-in-house-load-test-harness.md) already does for the Q-AK load test.
- **Process bypass.** A change that skips the PR-body gate or merges without the conformance pass defeats the scheme. Mitigation: the gate is CI-enforced, not advisory; the §D5 hook blocks in-place edits to Accepted ADRs (§P1).
- **Toolchain drift from the invariants it enforces.** A hook or review agent could lag a changed analyser or rule. Mitigation: hooks mirror the *authoritative* CI gates ([ADR-IC-009](../../integration_concepts/adrs/ADR-IC-009-testing-infrastructure.md), [ADR-PC-010 §P5](./ADR-PC-010-dotnet-hand-rolled-engine.md)) rather than re-implementing them, so the gate moving is what catches a stale hook.

---

## Open Actions

1. **Build the toolchain in leverage order** — hooks (§P1, the safety floor incl. the §D5 immutability hook + PR-body gate) → `new-family-schema` + `new-event` (§P2, velocity) → `new-adr` + `amend-adr` (§P2, needed now) → the §P3 domain review agents + the ADR-conformance agent → fold into the `babelstone-engine` plugin (§P4) once stable.
2. **Confirm the dev-time MCP allowlist** — `github` + `context7` enabled; `bd` allowlisted; prune unrelated plugins from the project config (§P4).
3. **Amend [ADR-PC-000](./ADR-PC-000-namespace-and-contract-shape-framework.md) to add the `Verifiable commitments` section to both templates** — *done (the §D5 template slot); backfill into existing entries is incremental (#5).* 
4. **Seed the load-bearing commitment catalogue** — the ~8 invariants named in §P7 (append+outbox atomicity, Money boundary, determinism, post-flag-never-gates, pin-per-event, AML-edge, batch-ingest idempotency, replay budgets, zero-engine-code-per-variant) as the first fitness functions, before broad engine work begins.
5. **Build the coverage checker** (§P6) and wire it as a §P1 hook + CI step; build the spec-coverage auditor (§P3) as a periodic sweep.
6. **Stand up the explicit-drift gate** — the §D5 ADR-immutability hook, the conformance agent, and the PR-body "ADRs touched/honoured" requirement (§P1, §P3).
7. **Backfill `Verifiable commitments` into existing ADR-PC and in-house ADR-IC entries** incrementally as each is implemented — not a big-bang rewrite.

---

## Cross-references

- [ADR-PC-019](./ADR-PC-019-repository-strategy-monorepo.md) — the single tree this toolchain and governance run on; its atomic-change property lets a contract + consumers + commitment land together.
- [ADR-PC-010](./ADR-PC-010-dotnet-hand-rolled-engine.md) — LLM-codability + the hand-rolled/lean posture applied here to tooling; the determinism + `Money` analysers the §P1 hooks mirror; Open Action #4, the live precedent for acknowledged, dated drift.
- [ADR-PC-000 §D4–§D5](./ADR-PC-000-namespace-and-contract-shape-framework.md) — the shape default this ADR follows, the amend/supersede lifecycle the explicit-drift gate mechanises, and the `Verifiable commitments` template slot the §P5 commitments populate.
- [ADR-IC-013](../../integration_concepts/adrs/ADR-IC-013-in-house-estate-build-and-repository-placement.md) — the in-house estate (orchestrator, outbox, MCP, notification, ACL) this governance also covers; the grounding for §P11's both-namespaces scope.
- [ADR-IC-009](../../integration_concepts/adrs/ADR-IC-009-testing-infrastructure.md) / [07-testing-strategy](../../integration_concepts/07-testing-strategy.md) — the Testcontainers/Pact stack and the five-level pyramid the fitness functions reuse and map onto.
- [ADR-IC-008](../../integration_concepts/adrs/ADR-IC-008-event-catalog-governance-tooling.md) / [ADR-IC-002](../../integration_concepts/adrs/ADR-IC-002-schema-format-and-registry.md) — EventCatalog + schema registry, the boundary-contract layer of the runtime guard.
- [ADR-PC-011](./ADR-PC-011-in-house-load-test-harness.md) — the nightly Q-AK load test, the model for keeping timing-sensitive fitness functions off the per-push path.
- [ADR-PC-001 §P2](./ADR-PC-001-event-store-technology.md), [ADR-PC-009](./ADR-PC-009-per-instance-version-pinning.md), [ADR-PC-012](./ADR-PC-012-gl-posting-signal-contract.md)/[014](./ADR-PC-014-customer-notification-emit-contract.md)/[015](./ADR-PC-015-ifrs9-signal-contract.md), [ADR-PC-013](./ADR-PC-013-aml-kyc-upstream-precondition.md), [ADR-PC-017](./ADR-PC-017-legacy-batch-ingest-contract.md) — representative commitments this strategy guards.
- [01 §1, §3](../01-product-architecture.md) — one-engine-many-families and the wedge's two falsifiable claims, the highest-value acceptance fitness functions.
- [04-open-questions](../04-open-questions.md) — the register for deliberate, time-bounded acknowledged deferrals.

---

*Decided 2026-05-23 by jhosm.*
*Revised 2026-05-24: absorbed the LLM build toolchain (formerly ADR-PC-019 D2 / §P2–§P5) and gave it a first-class F1/F2 + soft-criteria evaluation (toolchain axis); re-grounded the both-namespaces scope on [ADR-IC-013](../../integration_concepts/adrs/ADR-IC-013-in-house-estate-build-and-repository-placement.md) (replacing a mistaken "ADR-PC-019 reframe" attribution); corrected the in-house estate list to five components (adding the notification service, [ADR-IC-011](../../integration_concepts/adrs/ADR-IC-011-async-saga-completion-notification.md)).*
