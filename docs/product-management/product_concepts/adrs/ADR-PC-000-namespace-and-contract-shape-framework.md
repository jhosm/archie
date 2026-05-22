# ADR-PC-000: ADR-PC Namespace Conventions and Contract-Shape Framework

| Field | Value |
|---|---|
| Status | Accepted |
| Date | 2026-05-22 |
| Applies to | ADR-PC-001 through ADR-PC-018 (and all future ADR-PC entries) |
| Shape | Conventions (defines the templates used by ADR-PC) |

---

## Context

The product engine described in [01 product-architecture](../01-product-architecture.md) has its own concern surface that the integration architecture under [integration_concepts/](../../integration_concepts/00-introduction-and-decisions.md) does not cover. Where ADR-IC-NNN decides infrastructure shared by every domain in the bank (broker, registry, gateway, ACL, observability, …) under [ADR-IC-000](../../integration_concepts/adrs/ADR-IC-000-common-evaluation-criteria.md)'s framework, the engine's own load-bearing decisions are a peer set: source of truth and state, configuration surface, engine runtime, boundary signal contracts, and coexistence with the legacy core.

This ADR establishes the **ADR-PC** namespace as a peer to ADR-IC, fixes the conventions that bind all ADR-PC entries, and resolves a question that ADR-IC did not have to face: how to write an ADR whose deliverable is *not* a tool pick.

### Why a second namespace at all

Two reasons. First, **ownership boundaries**. ADR-IC is governed jointly with the rest of the bank's integration estate; ADR-PC is governed by the engine team. Mixing the two in one namespace would force every engine-internal decision through an integration-team approval that has no useful opinion on it, and vice versa. Second, **discoverability**. A future reader looking for "how does the engine pick its event store" should not have to scan an index that conflates "engine event store" with "shared API gateway." Two namespaces, two indices, each scoped to its own audience.

### Why two shapes within one namespace

Roughly two-thirds of the planned ADR-PC entries pick a concrete tool or runtime mechanism (event store, projection implementation, snapshot mechanism, schema-validator runtime, …). For those, ADR-IC-000's evaluation framework applies without amendment: same constraints (zero budget, 1–2 person team, EU regulatory baseline), same hard filters, same soft criteria.

Roughly one-third of the planned entries do *not* pick a tool. Their deliverable is a **boundary contract** — the shape of the events the engine emits to the GL system, the payload the engine sends to AML/KYC, the format of the batch file the engine ingests from the legacy core, the wire shape of the customer-notification signal. For these, F1 (cost) and F2 (regulatory fit) have nothing to score: the engine team is not buying or installing anything; it is committing to a payload schema and an ownership model that two teams will build against independently.

Forcing the contract-shape ADRs through the F1/F2/S1–S4 template would produce empty cells, fake comparisons against straw-man "alternatives," and reviewers learning to skip the evaluation table. The cleanest fix is to acknowledge the two shapes openly and give each a template that asks the right questions.

A small residual category — operational discipline (DR posture, per-instance version pinning) — fits neither template cleanly. The rule below ("declare shape per ADR; default to tool-selection") handles those without a third template.

---

## Decision

### D1 · Namespace and number space

ADR-PC numbers are independent of ADR-IC numbers. `ADR-PC-001` and `ADR-IC-001` may coexist; they refer to different decisions in different namespaces and are never aliased. Cross-references always include the namespace prefix (`ADR-PC-007`, not `ADR-007`).

Both namespaces share a numbering hygiene rule, applied **within each namespace independently**:

> When picking a new ADR-PC number, check both the on-disk filenames (`ls docs/product-management/product_concepts/adrs/`) and the planned-but-unwritten ADR-PC entries in the issue tracker (`bd list | grep ADR-PC`). The two share one number space *within the ADR-PC namespace*. The same rule applies independently to ADR-IC.

This rule exists because a real collision occurred in the ADR-IC namespace between an on-disk `ADR-IC-010` and a tracker-reserved `ADR-IC-010`, resolved by renumbering. The dual-check is what prevents a recurrence. The collision risk does not cross namespaces — `ADR-PC-005` and `ADR-IC-005` are not in tension.

### D2 · Tool-selection shape: reuse ADR-IC-000 verbatim

For ADR-PC entries whose deliverable is a tool, library, or runtime mechanism, the evaluation framework from [ADR-IC-000](../../integration_concepts/adrs/ADR-IC-000-common-evaluation-criteria.md) is reused **without amendment**:

- Hard filters: **F1** (cost) and **F2** (regulatory fit — GDPR, DORA, PSD2).
- Soft criteria: **S1** (operational complexity for 1–2 people), **S2** (ecosystem coherence), **S3** (exit cost), **S4** (community and longevity).
- Verdict format: `Pass` / `Pass (conditional)` / `Fail`; a conditional pass names its mitigation in the same cell and restates it in Consequences or Residual Risks.

ADR-PC tool-selection ADRs cite ADR-IC-000 by reference rather than restating the framework. If the constraints behind the framework change (e.g., budget moves off zero, regulatory baseline shifts), the amendment lands in ADR-IC-000 and propagates to both namespaces — there is one yardstick, not two.

A tool-selection ADR-PC follows the structure declared in [ADR-IC-000 §"Verdict format"](../../integration_concepts/adrs/ADR-IC-000-common-evaluation-criteria.md): Context, Evaluation (hard-filter table + soft-criteria prose), Decision (chosen + rejected with decisive reasons), Consequences.

### D3 · Contract-shape: a complementary template

For ADR-PC entries whose deliverable is a boundary contract — payload schema, event taxonomy, API surface, ordering or idempotency commitment between the engine and a counterparty — the F1/F2 hard-filter table is dropped. Rigor moves from "did the tool satisfy the constraints" to "is the contract complete enough that two teams can build against it independently."

A contract-shape ADR-PC follows this structure:

```
# ADR-PC-NNN: <decision topic>

| Field | Value |
|---|---|
| Status | Accepted / Proposed / Superseded by … |
| Date | YYYY-MM-DD |
| Shape | Contract-shape |
| Counterparty | <the system or team on the other side of the contract> |

## Context

What boundary this contract crosses; which engine concern it serves; which
out-of-scope system it talks to; which open question or section of the brief
motivates the decision.

## Decision

The contract itself, with all six required slots filled. Brevity is fine —
each slot may be one paragraph — but no slot may be silently omitted. If a
slot does not apply, say so explicitly and why.

1. **Payload shape** — the event or message schema (or a reference to the
   AsyncAPI / Avro / JSON-Schema artefact that holds it).
2. **Semantics** — what each field means, what state transition the message
   represents, what the receiver is expected to do.
3. **Ordering and delivery guarantees** — at-least-once vs effectively-once;
   per-partition order vs no order; gap-detection responsibility.
4. **Idempotency** — the idempotency key (`event_id`, `correlation_id`, or
   composite), how long it must remain unique, and on which side dedupe runs.
5. **Error model** — how the receiver signals "I cannot accept this" and how
   the engine reacts; whether failures are gated (block the producing flow)
   or post-flagged (record and continue).
6. **Ownership and versioning** — which team owns the contract; how breaking
   changes are introduced (new event type, parallel topic, deprecation
   window); how consumer-driven contract tests gate change
   (per [ADR-IC-009](../../integration_concepts/adrs/ADR-IC-009-testing-infrastructure.md)).

## Consequences

What this contract makes easy / hard / impossible — for both the engine
and the counterparty. Forward-compatibility properties. Coupling that this
choice locks in.

## Residual risks

What the contract does NOT commit to. Open questions that this ADR
intentionally defers. Failure modes that the contract permits.
```

The contract-shape template explicitly **does not** carry an F1/F2 evaluation table. A contract-shape ADR that finds itself wanting an evaluation table has probably misclassified its shape — see D4.

### D4 · Shape declaration is per-ADR; default to tool-selection

Each ADR-PC declares its shape in the front-matter table: `Shape: Tool-selection`, `Shape: Contract-shape`, or `Shape: Conventions` (reserved for namespace-level ADRs like this one).

When in doubt, **default to tool-selection**. The discipline of running the F1/F2 hard filters often surfaces a tool-pick hidden inside what looked like a contract decision (e.g., "AML/KYC signal contract" turns out to also pick a message format, which is a tool decision).

An ADR that begins as tool-selection and discovers empty cells in its hard-filter table — because the deliverable is not in fact a tool — should switch to contract-shape rather than fabricate scoreable alternatives. The shape declaration is the visible mark of that switch; reviewers should treat a mid-ADR shape change as a signal to read carefully, not as a defect.

Hybrid cases (e.g., ADR-PC-016 "Legacy current-account adapter implementation" — both an implementation approach and a contract) pick whichever shape captures the load-bearing decision. The non-load-bearing dimension is captured in Consequences or in a short subsection inside Decision, not by stapling both templates together.

### D5 · File naming, status lifecycle, cross-linking

These conventions match ADR-IC (per [integration_concepts/adrs/README.md](../../integration_concepts/adrs/README.md#adr-conventions)) and are restated here for ADR-PC by reference:

- **File naming.** `ADR-PC-NNN-short-kebab-case-slug.md`. The slug names the chosen tool or the decision topic, not the alternatives considered. Example: `ADR-PC-001-event-store-technology.md`, not `ADR-PC-001-postgres-vs-kurrent.md`.
- **Status lifecycle.** `Proposed` → `Accepted`; later either `Superseded by ADR-PC-NNN` or `Rejected`. Editing an Accepted ADR's Decision section in place is not the supported workflow; amend (dated entry appended) or supersede (new ADR with a new number).
- **Cross-linking.** From an ADR-PC to a concept doc in the same series: `../NN-name.md`. From an ADR-PC to an ADR-IC: `../../integration_concepts/adrs/ADR-IC-NNN-…md`. From an ADR-PC to a financial-concepts doc: `../../financial_concepts/banking_products_financial_mathematics.md`. These match the patterns in [CLAUDE.md](../../../../CLAUDE.md) and [AGENTS.md](../../../../AGENTS.md).
- **Indexing.** Every ADR-PC has an entry in [the namespace index](./README.md). The index carries title, chosen tool or decision summary, shape, and the concept docs the ADR supports — the same columns as the ADR-IC index, with a *Shape* column added.

---

## Consequences

**What this framework makes easier:**

- ADR-PC tool-selection decisions are comparable to ADR-IC decisions — same yardstick, same verdict format. A reader who has internalised ADR-IC-000 can read an ADR-PC tool-selection ADR without reorienting.
- Contract-shape ADRs have a structural place to live without distorting the evaluation framework. The six required slots in the contract-shape template force reviewers to check completeness, which is the right rigor for boundary contracts.
- Two parallel namespaces let the engine team and the integration team move at independent cadences. An ADR-IC update does not block ADR-PC drafting and vice versa.

**What this framework trades away:**

- Two shapes mean two templates, and template choice is now a decision each ADR-PC author has to make. The default-to-tool-selection rule and the "empty cells in the hard-filter table" diagnostic are intended to make the choice obvious in nearly every case; the residual ambiguity (hybrid ADRs) is handled in prose, not by a third template.
- The framework reuses ADR-IC-000 by reference. A future amendment to ADR-IC-000 silently changes ADR-PC tool-selection behaviour. This is the desired coupling (one yardstick across the estate), but it means ADR-PC reviewers must read ADR-IC-000 changes as if they were ADR-PC changes too.

**Residual risks:**

- The contract-shape template does not enforce a particular schema language or a particular event-catalogue tool. An ADR-PC contract-shape entry that references AsyncAPI or Avro is implicitly assuming the ADR-IC catalogue tooling ([ADR-IC-008](../../integration_concepts/adrs/ADR-IC-008-event-catalog-governance-tooling.md)) and the ADR-IC schema registry ([ADR-IC-002](../../integration_concepts/adrs/ADR-IC-002-schema-format-and-registry.md)). If those ADRs are ever superseded, every contract-shape ADR-PC needs a sweep to confirm it still composes with the replacement.
- "Default to tool-selection when in doubt" assumes reviewers will catch a misclassification. A misclassified ADR that fabricates straw-man alternatives to fill its hard-filter table degrades the framework. Mitigation: the ADR-PC index lists shape per ADR, so a reviewer skim against `bd show <id>`'s described deliverable will surface shape mismatches early.
