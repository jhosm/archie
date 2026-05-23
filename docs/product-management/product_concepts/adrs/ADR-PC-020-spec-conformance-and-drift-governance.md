# ADR-PC-020: Spec-Conformance and Drift Governance — Verifiable Commitments, Traceability, and an Explicit-Drift Gate

| Field | Value |
|---|---|
| Status | Accepted |
| Date | 2026-05-23 |
| Deciders | jhosm |
| Shape | Tool-selection |
| Common criteria | [ADR-IC-000](../../integration_concepts/adrs/ADR-IC-000-common-evaluation-criteria.md) (reused per [ADR-PC-000](./ADR-PC-000-namespace-and-contract-shape-framework.md) D2; this is the [ADR-PC-000 §D3](./ADR-PC-000-namespace-and-contract-shape-framework.md) "operational discipline" residual category — an engineering-practice decision, declared tool-selection per the [§D4](./ADR-PC-000-namespace-and-contract-shape-framework.md) default) |
| Depends on | [ADR-PC-019](./ADR-PC-019-monorepo-and-llm-build-toolchain.md) (the build toolchain this extends — hooks/skills/agents to enforce conformance), [ADR-PC-000 §D5](./ADR-PC-000-namespace-and-contract-shape-framework.md) (the amend/supersede lifecycle this gate makes mechanical), [ADR-IC-009](../../integration_concepts/adrs/ADR-IC-009-testing-infrastructure.md) (the Testcontainers + Pact CDC stack that hosts the runtime fitness functions), [ADR-IC-008](../../integration_concepts/adrs/ADR-IC-008-event-catalog-governance-tooling.md) / [ADR-IC-002](../../integration_concepts/adrs/ADR-IC-002-schema-format-and-registry.md) (EventCatalog + schema-registry, the boundary contract layer), [07-testing-strategy](../../integration_concepts/07-testing-strategy.md) (the five-level pyramid the commitments map onto) |
| Guards (representative) | [ADR-PC-001 §P2](./ADR-PC-001-event-store-technology.md), [ADR-PC-009](./ADR-PC-009-per-instance-version-pinning.md), [ADR-PC-010 §P1–§P5](./ADR-PC-010-dotnet-hand-rolled-engine.md), [ADR-PC-012](./ADR-PC-012-gl-posting-signal-contract.md) / [ADR-PC-014](./ADR-PC-014-customer-notification-emit-contract.md) / [ADR-PC-015](./ADR-PC-015-ifrs9-signal-contract.md) (post-flag-never-gates), [ADR-PC-013](./ADR-PC-013-aml-kyc-upstream-precondition.md), [ADR-PC-017](./ADR-PC-017-legacy-batch-ingest-contract.md), [event-store §8.2](../feature-design-event-store-projections.md) (replay budgets), [01 §3](../01-product-architecture.md) (the wedge's two falsifiable claims) |
| Resolves | bd `archie-10r.21` (ADR-PC-020: Spec-conformance and drift governance) |

---

## Context

This engine is specified before it is built, and built primarily by an LLM ([ADR-PC-010](./ADR-PC-010-dotnet-hand-rolled-engine.md) LLM-codability criterion; [ADR-PC-019](./ADR-PC-019-monorepo-and-llm-build-toolchain.md)). The specification is large: ~19 ADR-PC + ~13 ADR-IC entries, the concept docs, the feature-design notes, the event contract ([02 §2.4](../02-v1-scope-term-deposits.md)), the financial mathematics, and the C4 model — most cross-linked, many carrying *falsifiable* commitments. With the integration estate now also in scope to build (the in-house ADR-IC decisions — saga orchestrator [IC-003], outbox [IC-004], ACL [IC-012], MCP server [IC-010]), the body of decisions the implementation must honour spans **both** ADR namespaces.

The risk this ADR addresses is **drift**: the implementation silently diverging from a decision — a handler that reads the clock (violating [ADR-PC-010 §P5](./ADR-PC-010-dotnet-hand-rolled-engine.md) determinism), a consumer reject that unwinds a deposit (violating the post-flag-never-gates contract of [ADR-PC-012](./ADR-PC-012-gl-posting-signal-contract.md)/[014](./ADR-PC-014-customer-notification-emit-contract.md)/[015](./ADR-PC-015-ifrs9-signal-contract.md)), a second non-atomic write at constitution (violating [ADR-PC-001 §P2](./ADR-PC-001-event-store-technology.md)/[009](./ADR-PC-009-per-instance-version-pinning.md)) — none of which a compiler or a boundary contract test would necessarily catch. The goal is twofold: **prevent silent drift where mechanically possible, and where divergence is genuine, force it to be acknowledged** — never silent.

Two existing assets make this tractable, and this ADR builds on rather than replaces them:

1. **The commitments are already falsifiable.** Project discipline (bd memory `product-concepts-no-calendar-effort`) forces concept-doc claims to be *falsifiable internal commitments* — replay budgets (cold replay 5s with-a-plan / 30s irregular, [event-store §8.2](../feature-design-event-store-projections.md)), "zero engine code per variant", "≤ 5 working days PM commit to production" ([01 §3](../01-product-architecture.md)) — not vague intent. A falsifiable claim is one step from an executable check.
2. **The acknowledgment machinery already exists.** The [ADR-PC-000 §D5](./ADR-PC-000-namespace-and-contract-shape-framework.md) amend/supersede lifecycle, the "source wins" rule ([feature-design-c4-architecture](../feature-design-c4-architecture.md)), and the live precedent of [ADR-PC-010 Open Action #4](./ADR-PC-010-dotnet-hand-rolled-engine.md) (the §10.4 "no in-house build" tension tracked as a dated, acknowledged deferral) show the project already records divergence explicitly. This ADR makes that mechanical and unskippable.

This entry is the [ADR-PC-000 §D3](./ADR-PC-000-namespace-and-contract-shape-framework.md) **residual category** (engineering-practice discipline, declared tool-selection per §D4), like [ADR-PC-019](./ADR-PC-019-monorepo-and-llm-build-toolchain.md) and [ADR-PC-011](./ADR-PC-011-in-house-load-test-harness.md). The honest consequence, surfaced up front as [ADR-PC-009](./ADR-PC-009-per-instance-version-pinning.md) did: **F1 and F2 do not discriminate.** The mechanisms (ADR annotations, a coverage checker, the conformance review agent, the explicit-drift gate) are dev-time discipline and free tooling; the runtime fitness functions reuse the already-chosen Pact + Testcontainers stack ([ADR-IC-009](../../integration_concepts/adrs/ADR-IC-009-testing-infrastructure.md)); nothing is bought and nothing touches a regulated runtime surface. The load-bearing question is **which governance approach actually prevents silent drift at 1–2-person, LLM-first scale** — decided on S2 coherence plus a drift-prevention-correctness analysis, not on the hard filters.

**Candidates evaluated:**

| # | Candidate | Notes |
|---|---|---|
| A | **Layered conformance** — machine-checkable Verifiable-commitments per ADR (fitness functions) + code↔ADR traceability + a design-time conformance gate + the explicit-drift (amend/supersede) rule | Defence in depth: prevent where mechanical, detect where not, acknowledge where genuine. Reuses the [ADR-IC-009](../../integration_concepts/adrs/ADR-IC-009-testing-infrastructure.md) test stack and the [ADR-PC-019](./ADR-PC-019-monorepo-and-llm-build-toolchain.md) toolchain. |
| B | **Contract-tests-only** — rely on the [ADR-IC-009](../../integration_concepts/adrs/ADR-IC-009-testing-infrastructure.md) two-layer gate (schema registry + Pact CDC) as the sole drift guard | Catches drift that crosses a bounded-context boundary. Adds nothing for internal-design decisions (determinism, rounding, atomicity, one-engine-many-families). |
| C | **Review-only / convention** — rely on human + agent review against the ADRs, no machine-checkable commitments, no traceability | Cheapest to start. Enforcement = vigilance, which is exactly what erodes over a long LLM-authored build. |
| D | **Formal specification / model-checking** — encode invariants in TLA⁺ / a proof system and machine-verify | Strongest guarantee in principle. Disproportionate for a 1–2-person team; most commitments are about *code behaviour*, already exercisable as tests against real infrastructure. |

---

## Evaluation

### Hard filter results

#### F1 · Cost / licensing

| Candidate | Licence / cost | Verdict |
|---|---|---|
| A · layered | ADR annotations + a coverage checker (engine-team code); conformance via the [ADR-PC-019](./ADR-PC-019-monorepo-and-llm-build-toolchain.md) agent; runtime fitness functions on the already-chosen Pact/Testcontainers ([ADR-IC-009](../../integration_concepts/adrs/ADR-IC-009-testing-infrastructure.md)). Zero incremental cost. | **Pass** |
| B · contract-only | Already-chosen stack. Zero cost. | **Pass** |
| C · review-only | Zero cost. | **Pass** |
| D · formal | Tooling is open (TLA⁺ etc.), but the cost is *engineering time* at 1–2-person scale, not licence. | **Pass** (no licence bar) |

Uniform pass — F1 does not discriminate (no candidate buys anything).

#### F2 · Regulatory fit (GDPR / DORA / PSD2)

A governance strategy carries no PII and is not itself a regulated runtime artefact. There is one regulatory-adjacent property worth naming: DORA expects resilience and correctness behaviours to be **demonstrably tested, not assumed** ([ADR-IC-009](../../integration_concepts/adrs/ADR-IC-009-testing-infrastructure.md) frames its fault-injection tests as DORA artefacts), and PSD2/audit expects decisions about money handling to be traceable. Every candidate *permits* that; they differ in how *completely* the obligation is met — a correctness/coverage property, not a hard-filter pass/fail.

| Candidate | Verdict | Note |
|---|---|---|
| A · layered | **Pass** | Makes "this commitment is tested" first-class and auditable per ADR — the strongest demonstrable-conformance posture. |
| B · contract-only | **Pass** | Demonstrates boundary contracts; silent on internal-decision conformance. |
| C · review-only | **Pass** | No mechanical evidence trail; conformance is asserted, not demonstrated. |
| D · formal | **Pass** | Strongest evidence for the invariants modelled; says nothing about the unmodelled remainder. |

All clear the hard filters. The decision is entirely in S2 and the drift-prevention analysis — the expected shape for the [ADR-PC-000 §D3](./ADR-PC-000-namespace-and-contract-shape-framework.md) residual category.

---

### Soft criteria

#### A · Layered conformance — **CHOSEN**

**S1 · Operational complexity for 1–2 people.** Moderate, and front-loadable. The expensive part — fitness functions — mostly *already exists* as obligations: the [07-testing-strategy](../../integration_concepts/07-testing-strategy.md) pyramid, the [ADR-PC-001](./ADR-PC-001-event-store-technology.md) projection-rebuild drills, the Q-AK load test ([ADR-PC-011](./ADR-PC-011-in-house-load-test-harness.md)). The net-new work is *cataloguing* each commitment and asserting it has a gate, plus a thin coverage checker and ADR annotations. It scales down: seed the ~8 load-bearing invariants first, grow the catalogue as ADRs are implemented.

**S2 · Ecosystem coherence — decisive.** The strategy slots into machinery already chosen: fitness functions run on the [ADR-IC-009](../../integration_concepts/adrs/ADR-IC-009-testing-infrastructure.md) Testcontainers/Pact stack at the right pyramid level; the explicit-drift gate mechanises the [ADR-PC-000 §D5](./ADR-PC-000-namespace-and-contract-shape-framework.md) lifecycle that already governs ADR changes; the conformance agent and the §D5 hook are [ADR-PC-019](./ADR-PC-019-monorepo-and-llm-build-toolchain.md) toolchain items; the monorepo makes a contract + its consumers + its commitment-test changeable in one atomic commit. Nothing is bolted on sideways.

**S3 · Exit cost.** Low. Verifiable-commitments are prose-plus-test-IDs in the ADRs; annotations are comments; the coverage checker is a small script. Abandoning the strategy leaves ordinary tests and ordinary ADRs behind — no lock-in.

**S4 · Longevity.** Inherits the longevity of its substrates (the test stack, the ADR corpus, the monorepo). No single-vendor dependency.

**Decisive reason — drift prevention is layered because drift is layered.** Two *different* classes of drift need two *different* guards, and a third mechanism for the residue:
- **Boundary drift** (a schema-valid but semantically-wrong change crossing a bounded context) → the **runtime two-layer gate**: schema-registry compatibility ([ADR-IC-002](../../integration_concepts/adrs/ADR-IC-002-schema-format-and-registry.md)) + Pact CDC ([ADR-IC-009](../../integration_concepts/adrs/ADR-IC-009-testing-infrastructure.md)).
- **Internal-design drift** (legal code that contradicts a decision — a clock read in a handler, a rounding at the wrong place, a second write at constitution) → the **design-time conformance gate**: Verifiable-commitments-as-fitness-functions + the ADR-conformance review agent. No contract test sees this class.
- **Genuine divergence** (the decision itself should change) → the **explicit-drift rule**: it cannot land without an amend/supersede in the same change, so drift becomes a dated ADR edit, never a silent code fact.

#### B · Contract-tests-only — **rejected**

The [ADR-IC-009](../../integration_concepts/adrs/ADR-IC-009-testing-infrastructure.md) gate is necessary and retained — but it guards *boundaries*. It says nothing about decisions that live entirely inside a component: determinism ([ADR-PC-010 §P5](./ADR-PC-010-dotnet-hand-rolled-engine.md)), round-once-at-the-Money-boundary ([§P1–§P2](./ADR-PC-010-dotnet-hand-rolled-engine.md)), append+outbox atomicity ([ADR-PC-001 §P2](./ADR-PC-001-event-store-technology.md)), pin-per-event ([ADR-PC-009](./ADR-PC-009-per-instance-version-pinning.md)), one-engine-many-families ([01 §1](../01-product-architecture.md)). A change can be fully contract-compatible and still violate any of these. B leaves the larger drift surface unguarded; it is a layer of A, not an alternative to it.

#### C · Review-only / convention — **rejected**

C relies on a reviewer (human or agent) remembering ~30 ADRs and noticing a contradiction every time. Over a long, fast, LLM-authored build that vigilance is exactly what erodes — and an undetected internal-design drift is precisely the failure mode A exists to prevent. C also leaves no audit trail of *which* commitments are actually tested, weakening the DORA/PSD2 demonstrable-conformance posture. The conformance agent is valuable, but only *on top of* machine-checkable commitments that give it a concrete checklist rather than open-ended inference.

#### D · Formal specification / model-checking — **rejected (kept as a targeted future option)**

The strongest guarantee for what it models, but disproportionate at 1–2-person scale, and a poor fit for commitments that are about *runtime behaviour against real infrastructure* (PostgreSQL `ON CONFLICT`, outbox `SKIP LOCKED`, Redpanda rebalance) — those are most credibly verified by [ADR-IC-009](../../integration_concepts/adrs/ADR-IC-009-testing-infrastructure.md)-style integration tests, not a model. Reserved for a *specific* invariant later if one proves worth it (e.g. saga-compensation money-conservation as a property-based/TLA⁺ check), consistent with [07-testing-strategy §4.3](../../integration_concepts/07-testing-strategy.md) putting property-based tests at the top of the pyramid, "where you end up, not where you start." Adopting it wholesale now contradicts the lean, fully-owned posture of [ADR-PC-010](./ADR-PC-010-dotnet-hand-rolled-engine.md)/[ADR-PC-019](./ADR-PC-019-monorepo-and-llm-build-toolchain.md).

**Decisive reason for A over B/C/D:** drift is layered, so the guard must be layered. B guards only boundaries; C guards only by vigilance; D guards only the modelled fragment. A composes the runtime boundary gate, the design-time conformance gate, and the explicit-drift rule so that every class of drift is either prevented, detected, or forced into the open.

---

## Decision

This ADR makes **two coupled decisions**.

### D1 — Adopt layered spec-conformance: Verifiable commitments + traceability + a design-time conformance gate, composed with the runtime [ADR-IC-009](../../integration_concepts/adrs/ADR-IC-009-testing-infrastructure.md) contract gate.

Each ADR (PC and IC) gains a machine-checkable list of its load-bearing commitments, each bound to the test/gate that proves it (fitness functions). Implementing code is annotated with the ADR sections it satisfies, and a coverage checker asserts the two stay connected. The [ADR-PC-019](./ADR-PC-019-monorepo-and-llm-build-toolchain.md) ADR-conformance agent reviews every change against the governing ADRs for the internal-design class that no contract test catches. The runtime two-layer gate (schema registry + Pact) is retained as the boundary guard. Decided on **S2 coherence** (reuses the chosen test stack, the §D5 lifecycle, and the monorepo's atomic-change property) and **drift-prevention correctness** (boundary, internal-design, and genuine divergence each have a guard).

### D2 — The explicit-drift gate: no change may contradict an Accepted ADR without an amendment or supersession in the same change.

Divergence from an Accepted decision is permitted — the project expects decisions to evolve — but only *on the record*. A change that contradicts an Accepted ADR must carry, in the same PR, either a dated amendment to that ADR or a superseding ADR (per [ADR-PC-000 §D5](./ADR-PC-000-namespace-and-contract-shape-framework.md)); a deliberate, time-bounded deferral is recorded in [04-open-questions](../04-open-questions.md) (the existing register, the home of the [ADR-PC-010 Open Action #4](./ADR-PC-010-dotnet-hand-rolled-engine.md)-style acknowledged tension). Enforced by the §D5 immutability hook + the conformance agent + the [ADR-PC-019](./ADR-PC-019-monorepo-and-llm-build-toolchain.md) PR-body "ADRs touched/honoured" gate. This is the structural form of "if it drifts, it is explicitly acknowledged."

**Rejected: contract-tests-only** — guards boundaries, blind to internal-design drift; retained as a layer, not the whole. **Rejected: review-only** — enforcement by vigilance is what erodes; kept only as the conformance agent *on top of* machine-checkable commitments. **Rejected (deferred): formal methods** — disproportionate now; reserved for a specific invariant later.

---

## Implementation Principles

### P1 — Every ADR carries a `Verifiable commitments` section binding each load-bearing claim to a gate

A new section (proposed as an [ADR-PC-000](./ADR-PC-000-namespace-and-contract-shape-framework.md) template addition — see Open Actions) lists each load-bearing commitment, the pyramid level/gate that verifies it, and a stable test identifier. Illustrative shape:

```
## Verifiable commitments
| # | Commitment | Gate (level) | Test ID |
|---|---|---|---|
| C1 | append + outbox commit in one local txn (PC-001 §P2) | integration / Testcontainers | ES_ATOMIC_APPEND_OUTBOX |
| C2 | Money rounds HALF_EVEN exactly once at the Decimal→Cents boundary | unit + analyser | MONEY_BOUNDARY_FIXTURES |
| C3 | a handler that reads the clock / does I/O fails the build | CI determinism gate | DETERMINISM_GATE |
```

Not every ADR needs many; contract-shape ADRs may have one or two (e.g. "post-flag never gates"). A commitment with no gate is a known hole, listed as such — visibility is the point.

### P2 — Code is annotated with the ADR sections it satisfies; a coverage checker keeps spec and code connected

Implementing sites carry a lightweight anchor (`// ADR-PC-001 §P2`). A coverage checker (a [ADR-PC-019 §P2](./ADR-PC-019-monorepo-and-llm-build-toolchain.md) hook + CI step) asserts:

- every `Verifiable commitment` resolves to ≥1 test that exists and runs, and (where applicable) ≥1 code anchor;
- every ADR anchor in code points to a **live** (non-superseded) ADR section;
- the spec-coverage auditor ([ADR-PC-019 §P4](./ADR-PC-019-monorepo-and-llm-build-toolchain.md)) sweeps periodically for ADRs with **no** implementing code (decided-but-unbuilt) and code paths governed by an ADR with **no** commitment test.

This catches drift in both directions — spec without code, and code that has quietly outgrown its spec.

### P3 — Fitness functions live at the right pyramid level; most already have a home

Commitments map onto the [07-testing-strategy](../../integration_concepts/07-testing-strategy.md) levels rather than spawning a parallel suite:

| Commitment class | Level / mechanism |
|---|---|
| Money rounding, determinism, aggregate invariants | Unit + Roslyn analysers ([ADR-PC-010 §P1–§P2, §P5](./ADR-PC-010-dotnet-hand-rolled-engine.md)) |
| Append+outbox atomicity, pin-per-event replay, idempotent batch ingest ([ADR-PC-017](./ADR-PC-017-legacy-batch-ingest-contract.md)) | Integration / Testcontainers |
| Post-flag-never-gates ([ADR-PC-012](./ADR-PC-012-gl-posting-signal-contract.md)/[014](./ADR-PC-014-customer-notification-emit-contract.md)/[015](./ADR-PC-015-ifrs9-signal-contract.md)), AML-edge-precondition ([ADR-PC-013](./ADR-PC-013-aml-kyc-upstream-precondition.md)), no-PII-on-bus | Contract / saga tests |
| Replay budgets 5s/30s ([event-store §8.2](../feature-design-event-store-projections.md)) | Benchmark gate (nightly, not per-push) |
| "Zero engine code per variant" ([01 §3](../01-product-architecture.md)) | Acceptance test: add a family schema → assert **zero `/engine` diff** |

The financial-math kernel gets a **golden-fixture corpus** derived from the [financial_concepts](../../financial_concepts/banking_products_financial_mathematics.md) worked examples (simple/compound interest, TAE, TANB/TANL split, flow-by-flow withholding), guarded by the [ADR-PC-019 §P4](./ADR-PC-019-monorepo-and-llm-build-toolchain.md) financial-math-reviewer.

### P4 — Two guards, because they catch different drift; neither subsumes the other

- **Runtime boundary guard** — the [ADR-IC-009](../../integration_concepts/adrs/ADR-IC-009-testing-infrastructure.md) two-layer gate: schema-registry compatibility ([ADR-IC-002](../../integration_concepts/adrs/ADR-IC-002-schema-format-and-registry.md)) catches structural breaks at publish; Pact CDC catches behavioural breaks (a nulled `correlation_id`, an inverted amount sign) in the producer's CI. Applies to every contract crossing a bounded context — and with the estate now in-house, that includes engine↔ACL, engine↔MCP, and engine↔downstream-consumer.
- **Design-time conformance guard** — Verifiable-commitments + the ADR-conformance agent: catches a change that is fully contract-compatible yet contradicts an internal decision (the determinism example). 

A contract test would pass a handler that newly reads the clock; the determinism gate + conformance agent fail it. A determinism gate says nothing about a consumer's expectation that `amount` is positive; Pact does. Both are mandatory.

### P5 — The explicit-drift workflow is the same shape as the existing ADR lifecycle

When implementation reveals a decision is wrong or incomplete, the change to the code and the change to the decision land **together**:

1. The conformance agent (or the §D5 hook) flags that the diff contradicts an Accepted ADR.
2. The author resolves it one of two ways — **fix the code** to conform, or **amend/supersede the ADR** ([ADR-PC-000 §D5](./ADR-PC-000-namespace-and-contract-shape-framework.md): dated amendment, or a new ADR with a supersession link) in the same PR. A time-bounded, deliberate gap is recorded in [04-open-questions](../04-open-questions.md).
3. The PR-body "ADRs touched/honoured" section ([ADR-PC-019 §P2](./ADR-PC-019-monorepo-and-llm-build-toolchain.md)) names the ADRs implemented or amended, so review starts from the decision, not the diff.

This mirrors the project's established order — ADR before code, bd issue before code — extended to: **no contradiction without a recorded decision.**

### P6 — Spec-first development loop

The default authoring loop for engine work, encoded in the [ADR-PC-019](./ADR-PC-019-monorepo-and-llm-build-toolchain.md) skills:

> ADR (or amendment) → add/confirm the `Verifiable commitment` as a *failing* fitness function → implement until green → coverage checker confirms the anchor.

A failing commitment-test written first turns the ADR's prose into the executable definition of done, and makes "implemented" mean "the commitment is demonstrably met," not "the code looks right."

### P7 — Governance spans both ADR namespaces

Because the integration estate is in-house ([ADR-PC-019](./ADR-PC-019-monorepo-and-llm-build-toolchain.md) reframe), the coverage checker, conformance agent, and explicit-drift gate apply to **ADR-IC** entries (orchestrator, outbox, ACL, MCP, observability, testing) exactly as to ADR-PC. The §D5 lifecycle and the dual-namespace numbering hygiene ([ADR-PC-000 §D1](./ADR-PC-000-namespace-and-contract-shape-framework.md)) already cover both; this ADR's mechanisms inherit that scope.

---

## Consequences

**What this choice makes easier:**

1. **Drift becomes mechanical, not a matter of memory.** Internal-design contradictions fail a gate or an agent review; boundary contradictions fail Pact/registry; genuine divergence fails the §D5 gate unless an ADR edit rides along.
2. **"Implemented" gains a precise meaning.** A commitment is met when its fitness function is green and its anchor resolves — not when the code reads plausibly.
3. **Demonstrable conformance for audit.** The Verifiable-commitments catalogue is a per-ADR, per-commitment evidence trail — the demonstrable-resilience/correctness posture DORA/PSD2 expect ([ADR-IC-009](../../integration_concepts/adrs/ADR-IC-009-testing-infrastructure.md) framing).
4. **Decisions stay live.** Annotations pointing only at non-superseded ADRs, and the decided-but-unbuilt sweep, keep the spec and the code from quietly diverging in either direction.
5. **The fast path is the conformant path.** The spec-first loop + the [ADR-PC-019](./ADR-PC-019-monorepo-and-llm-build-toolchain.md) skills make writing a conformant family schema or event the easiest way to build, which is where the LLM-first velocity compounds safely.

**What this choice makes harder or impossible:**

1. **Authoring overhead per ADR.** Every ADR now owes a `Verifiable commitments` section, and every load-bearing commitment owes a test. Intentional — an untested commitment is a latent drift hole — but it is real work. Mitigation: seed the ~8 load-bearing invariants first; grow the catalogue as ADRs are implemented (S1).
2. **A commitment catalogue can rot.** A stale or wrongly-mapped commitment is itself a drift. Mitigation: the coverage checker fails on a commitment whose test does not exist/run, and on an anchor to a superseded ADR.
3. **The explicit-drift gate adds friction to "just fix it" changes.** A quick code change that happens to contradict an ADR now also demands an ADR edit. That friction is the point (it is the acknowledgment), but it must be cheap — the `amend-adr` skill ([ADR-PC-019 §P3](./ADR-PC-019-monorepo-and-llm-build-toolchain.md)) exists to keep it a one-command step.

**Residual risks:**

- **Commitment coverage is only as good as the catalogue.** A load-bearing decision nobody listed as a commitment is unguarded by P1–P3 (though the conformance agent may still catch it). Mitigation: the spec-coverage auditor sweeps for ADRs with no commitments and for governed code with no commitment test; treat a no-commitment ADR as a finding.
- **Conformance-agent fallibility.** An LLM reviewer can miss a contradiction or raise a false one. Mitigation: it is a *layer*, not the sole guard — the mechanical gates (analysers, determinism gate, Pact, the coverage checker) carry the load-bearing invariants; the agent covers the long tail and proposes the amend/supersede.
- **Benchmark-gate flakiness.** Replay-budget fitness functions ([event-store §8.2](../feature-design-event-store-projections.md)) are timing-sensitive. Mitigation: run them nightly on a stable runner (not per-push), as [ADR-PC-011](./ADR-PC-011-in-house-load-test-harness.md) already does for the Q-AK load test.
- **Process bypass.** A change that skips the PR-body gate or merges without the conformance pass defeats the scheme. Mitigation: the gate is CI-enforced, not advisory; the §D5 hook blocks in-place edits to Accepted ADRs ([ADR-PC-019 §P2](./ADR-PC-019-monorepo-and-llm-build-toolchain.md)).

---

## Open Actions

1. **Amend [ADR-PC-000](./ADR-PC-000-namespace-and-contract-shape-framework.md) to add the `Verifiable commitments` section to both templates** (tool-selection and contract-shape). *Doc-edit to an Accepted ADR — deferred pending owner sign-off, not made unilaterally in this ADR, mirroring [ADR-PC-010 Open Action #4](./ADR-PC-010-dotnet-hand-rolled-engine.md).*
2. **Seed the load-bearing commitment catalogue** — the ~8 invariants named in P3 (append+outbox atomicity, Money boundary, determinism, post-flag-never-gates, pin-per-event, AML-edge, batch-ingest idempotency, replay budgets, zero-engine-code-per-variant) as the first fitness functions, before broad engine work begins.
3. **Build the coverage checker** (P2) and wire it as a [ADR-PC-019 §P2](./ADR-PC-019-monorepo-and-llm-build-toolchain.md) hook + CI step; build the spec-coverage auditor ([ADR-PC-019 §P4](./ADR-PC-019-monorepo-and-llm-build-toolchain.md)) as a periodic sweep.
4. **Stand up the explicit-drift gate** — the §D5 ADR-immutability hook, the conformance agent, and the PR-body "ADRs touched/honoured" requirement ([ADR-PC-019 §P2, §P4](./ADR-PC-019-monorepo-and-llm-build-toolchain.md)).
5. **Backfill `Verifiable commitments` into existing ADR-PC and in-house ADR-IC entries** incrementally as each is implemented — not a big-bang rewrite.

---

## Cross-references

- [ADR-PC-019](./ADR-PC-019-monorepo-and-llm-build-toolchain.md) — the build toolchain (hooks/skills/agents, conformance agent, coverage checker, amend-adr skill) that implements this governance; the monorepo's atomic-change property that lets a contract + consumers + commitment land together.
- [ADR-PC-000 §D4–§D5](./ADR-PC-000-namespace-and-contract-shape-framework.md) — the shape default this ADR follows, and the amend/supersede lifecycle the explicit-drift gate mechanises; Open Action #1 amends its templates.
- [ADR-IC-009](../../integration_concepts/adrs/ADR-IC-009-testing-infrastructure.md) / [07-testing-strategy](../../integration_concepts/07-testing-strategy.md) — the Testcontainers/Pact stack and the five-level pyramid the fitness functions reuse and map onto.
- [ADR-IC-008](../../integration_concepts/adrs/ADR-IC-008-event-catalog-governance-tooling.md) / [ADR-IC-002](../../integration_concepts/adrs/ADR-IC-002-schema-format-and-registry.md) — EventCatalog + schema registry, the boundary-contract layer of the runtime guard.
- [ADR-PC-010 §P5, Open Action #4](./ADR-PC-010-dotnet-hand-rolled-engine.md) — the determinism gate (a model internal-design fitness function) and the live precedent for acknowledged, dated drift.
- [ADR-PC-011](./ADR-PC-011-in-house-load-test-harness.md) — the nightly Q-AK load test, the model for keeping timing-sensitive fitness functions off the per-push path.
- [ADR-PC-001 §P2](./ADR-PC-001-event-store-technology.md), [ADR-PC-009](./ADR-PC-009-per-instance-version-pinning.md), [ADR-PC-012](./ADR-PC-012-gl-posting-signal-contract.md)/[014](./ADR-PC-014-customer-notification-emit-contract.md)/[015](./ADR-PC-015-ifrs9-signal-contract.md), [ADR-PC-013](./ADR-PC-013-aml-kyc-upstream-precondition.md), [ADR-PC-017](./ADR-PC-017-legacy-batch-ingest-contract.md) — representative commitments this strategy guards.
- [01 §1, §3](../01-product-architecture.md) — one-engine-many-families and the wedge's two falsifiable claims, the highest-value acceptance fitness functions.
- [04-open-questions](../04-open-questions.md) — the register for deliberate, time-bounded acknowledged deferrals.

---

*Decided 2026-05-23 by jhosm.*
