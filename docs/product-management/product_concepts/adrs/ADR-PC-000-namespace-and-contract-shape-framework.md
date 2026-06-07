# ADR-PC-000: ADR-PC Namespace Conventions and Contract-Shape Framework

| Field | Value |
|---|---|
| Status | Accepted |
| Date | 2026-05-22 |
| Applies to | ADR-PC-001 through ADR-PC-020 (and all future ADR-PC entries) |
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

When in doubt, **default to tool-selection**. The discipline of running the F1/F2 hard filters often surfaces a tool-pick hidden inside what looked like a contract decision (e.g., a "reporting signal contract" turns out to also pick a message format, which is a tool decision).

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

- The contract-shape template does not enforce a particular schema language or a particular event-catalogue tool. An ADR-PC contract-shape entry that references AsyncAPI or Avro is implicitly assuming the ADR-IC catalogue tooling ([ADR-IC-008](../../integration_concepts/adrs/retired/ADR-IC-008-event-catalog-governance-tooling.md)) and the ADR-IC schema registry ([ADR-IC-002](../../integration_concepts/adrs/ADR-IC-002-schema-format-and-registry.md)). If those ADRs are ever superseded, every contract-shape ADR-PC needs a sweep to confirm it still composes with the replacement.
- "Default to tool-selection when in doubt" assumes reviewers will catch a misclassification. A misclassified ADR that fabricates straw-man alternatives to fill its hard-filter table degrades the framework. Mitigation: the ADR-PC index lists shape per ADR, so a reviewer skim against `bd show <id>`'s described deliverable will surface shape mismatches early.

---

## Amendment — 2026-05-23: `Verifiable commitments` template slot (per ADR-PC-020)

[ADR-PC-020](./ADR-PC-020-llm-toolchain-and-conformance-governance.md) adopts an LLM-first build toolchain with **layered spec-conformance**, running on the [ADR-PC-019](./ADR-PC-019-repository-strategy-monorepo.md) monorepo: the implementation is kept faithful to the ADR corpus by binding each load-bearing decision to an executable check ("architecture fitness functions"), and any divergence is forced through the [§D5](#d5--file-naming-status-lifecycle-cross-linking) amend/supersede lifecycle rather than landing silently in code. For that to work, the *load-bearing commitments of each ADR must be enumerable and bound to the test that proves them* ([ADR-PC-020 §P5](./ADR-PC-020-llm-toolchain-and-conformance-governance.md)). This amendment adds that slot to the templates this ADR defines.

### A1 · Both templates gain a `## Verifiable commitments` section

Every **tool-selection** ([§D2](#d2--tool-selection-shape-reuse-adr-ic-000-verbatim)) and **contract-shape** ([§D3](#d3--contract-shape-a-complementary-template)) ADR-PC carries a `## Verifiable commitments` section. Placement: **after Consequences** (tool-selection) or **after Residual risks** (contract-shape) — i.e. near the end, before any later Amendment or Cross-references block — so it reads as the checklist that the body's argument earns, not as part of the argument. The section is a table:

```
## Verifiable commitments
| # | Commitment | Gate (pyramid level) | Test ID | Status |
|---|---|---|---|---|
| C1 | <the falsifiable claim, with the §-anchor it derives from> | unit / integration / contract / saga / benchmark / analyser | <stable identifier> | Live / Planned / Gap |
```

- **Commitment** — a falsifiable claim the implementation must satisfy (e.g. "append + outbox commit in one local transaction", "a handler that reads the clock fails the build"), tagged with the Decision/Principle section it derives from.
- **Gate (pyramid level)** — where the check lives on the [07-testing-strategy](../../integration_concepts/07-testing-strategy.md) pyramid, or "analyser"/"benchmark" for build-time and timing gates.
- **Test ID** — a stable identifier the [ADR-PC-020 §P6](./ADR-PC-020-llm-toolchain-and-conformance-governance.md) coverage checker resolves to an existing, running test.
- **Status** — `Live` (gate exists and passes), `Planned` (gate to be built before the decision is implemented), or `Gap` (no gate yet — a **known hole, listed deliberately**; visibility is the point, per [ADR-PC-020 §P5](./ADR-PC-020-llm-toolchain-and-conformance-governance.md)).

Brevity is expected: a contract-shape ADR may have one or two commitments (often just "post-flag never gates" / an idempotency-key rule); a tool-selection ADR lists only the load-bearing few, not every sentence. An ADR with **no** load-bearing commitment says so explicitly (a one-line "no executable commitments — this decision is realised entirely by [the cited downstream ADR]") rather than omitting the section.

### A2 · Scope: the two buildable-decision templates only; Conventions ADRs are exempt

The slot applies to ADRs whose deliverable is a tool/mechanism or a boundary contract — the things an implementation can *drift from*. **Conventions ADRs** (`Shape: Conventions`, like this one) define process, not buildable behaviour, and carry no `Verifiable commitments` section. The F1/F2 hard-filter framework ([§D2](#d2--tool-selection-shape-reuse-adr-ic-000-verbatim)) and the six required contract slots ([§D3](#d3--contract-shape-a-complementary-template)) are unchanged; this is an *additional* section, not a revision of either.

### A3 · This amends the templates; it does not supersede this ADR

[§D2](#d2--tool-selection-shape-reuse-adr-ic-000-verbatim), [§D3](#d3--contract-shape-a-complementary-template), and [§D4](#d4--shape-declaration-is-per-adr-default-to-tool-selection) remain binding as written; the `Verifiable commitments` section is appended to the structures they prescribe. Backfilling the section into the already-Accepted ADR-PC and in-house ADR-IC entries is **incremental, not big-bang** — done as each decision is implemented ([ADR-PC-020 Open Action #7](./ADR-PC-020-llm-toolchain-and-conformance-governance.md)) — so existing Accepted ADRs are not invalidated by lacking it yet. The front-matter `Applies to` range is bumped to ADR-PC-020 as housekeeping (the parenthetical "all future ADR-PC entries" already covered it).

---

## Amendment — 2026-05-24: a central catalogue may be the source of truth; the ADR section then *references* it

The [ADR-PC-020](./ADR-PC-020-llm-toolchain-and-conformance-governance.md) load-bearing seed (Open Action #4) introduces a central [commitment catalogue](./commitment-catalogue.md) aggregating the load-bearing fitness functions across ADRs — and across the two non-ADR sources (replay budgets, zero-engine-code-per-variant) that §A2 exempts from a per-ADR section. This refines the §A1 shape:

- **When a commitment is recorded in the catalogue, the catalogue is its single source of truth** for the claim, the gate, and the `Live`/`Planned`/`Gap` status. The ADR's `## Verifiable commitments` section is then a **reference** — a short list of the governing Test IDs (the join key) with the local §-anchor and a link to the catalogue — *not* a restated table. This keeps the mutable fields (status especially) in one place, so an ADR and the catalogue cannot drift apart.
- **The §A1 table form remains correct** for an ADR whose commitment is not (or not yet) catalogued centrally. The "no executable commitments — say so in one line" rule (§A1) is unchanged.

Both forms satisfy §A1's intent: every buildable-decision ADR enumerates its load-bearing commitments, each bound to a Test ID the [ADR-PC-020 §P6](./ADR-PC-020-llm-toolchain-and-conformance-governance.md) coverage checker resolves. This amends the §A1 *shape* guidance; it does not supersede this ADR, and §D2/§D3/§D4 are untouched.

---

## Amendment — 2026-06-03: the signal-contract design principle (facts vs verdicts; no clock-manufactured signals)

The [§D3](#d3--contract-shape-a-complementary-template) contract-shape template has produced a recognisable **family** of boundary-contract ADRs — [ADR-PC-012](./ADR-PC-012-gl-posting-signal-contract.md) (GL), [ADR-PC-014](./retired/ADR-PC-014-customer-notification-emit-contract.md) (notifications), [ADR-PC-015](./ADR-PC-015-ifrs9-signal-contract.md) (IFRS 9), and the precondition generalisation [ADR-PC-024](./ADR-PC-024-constitution-precondition-contract.md) plus the temporal-emission discipline [ADR-PC-023](./ADR-PC-023-temporal-signals-projection-derived.md). They share one load-bearing stance, named here so the author of the next boundary-contract ADR reaches for it deliberately and a reader navigates the family. This amends the [§D3](#d3--contract-shape-a-complementary-template) *guidance* (a design heuristic for the contract-shape template); it adds **no template slot** and **supersedes nothing** — §D1–§D5 are untouched.

**The principle.** *The engine commits to facts; the counterparty owns the verdict; and the engine emits nothing a clock manufactures.* In three faces:

- **Outbound — emit raw facts, never the counterparty's interpretation.** The engine emits *what happened* (a business event, an arrears update, a notification-due fact), never the downstream *decision* about it — never a GL posting ([ADR-PC-012](./ADR-PC-012-gl-posting-signal-contract.md)), an IFRS 9 stage ([ADR-PC-015](./ADR-PC-015-ifrs9-signal-contract.md)), or a delivery outcome ([ADR-PC-014](./retired/ADR-PC-014-customer-notification-emit-contract.md)). The counterparty owns its model (chart of accounts, SICR/ECL, channel/retry) and derives the verdict.
- **Inbound — act on a verdict, never compute it.** A precondition arrives as a verdict an upstream system has already decided; the engine **records** it for the audit trail and lets the constitution **proceed only if it holds**, but **never runs the check itself** (no transaction history, no eligibility logic). Commercial eligibility ([ADR-PC-024](./ADR-PC-024-constitution-precondition-contract.md)) — new-money, salary-domiciliation, mortgage-linked — is the engine's one precondition contract: the product config declares which verdicts a product requires, and the decider refuses (`DepositConstitutionFailed`) when one is absent or false. (Financial-crime AML/KYC is **not** an engine concern; it is adjudicated upstream, out of scope per [00 §4](../00-product-vision.md).)
- **Temporal — emit nothing a clock manufactures.** A signal is caused by a domain event (fact-driven) or it is a downstream read over a projection — never an engine clock/scheduler ([ADR-PC-023](./ADR-PC-023-temporal-signals-projection-derived.md)). [ADR-PC-015](./ADR-PC-015-ifrs9-signal-contract.md) already embodies this on the outbound side: *absolute* days-past-due + an as-of read, not a clock-driven "DPD is now N" event.

**Why the instances stay distinct ADRs, not sections of the generalisations.** Each contract-shape ADR records a *specific* decision with its own six-slot contract, counterparty, and residual risks a generalisation cannot hold — and several carry the load-bearing *exceptions* (PC-014 still owns its whole delivery contract while PC-023 governs only its timing; PC-012 and PC-015 are pure outbound facts neither generalisation touches). A reader debugging a specific seam needs the specific contract, and [§D5](#d5--file-naming-status-lifecycle-cross-linking) keeps Accepted decisions immutable. The generalisations name the *pattern*; the instances remain the *contracts*. A new boundary-contract ADR **cites this principle and links up to it**; it is not folded into it.

---

## Amendment — 2026-06-03: `Withdrawn` status

The [§D5](#d5--file-naming-status-lifecycle-cross-linking) status lifecycle (`Proposed → Accepted → Superseded by ADR-PC-NNN / Rejected`) has no term for an Accepted decision that is **retracted because its subject has moved out of scope** — it is neither replaced by a newer decision (Superseded) nor "considered and not adopted" (Rejected). ADR-PC adds **`Withdrawn`** for this case:

> **Withdrawn (YYYY-MM-DD)** — the decision was Accepted but is retracted because its subject is now out of scope for the engine. The ADR is **kept in the folder** for history, its Decision section left intact as the record of what was once decided, with a dated **withdrawal note** at the top explaining the retraction and pointing to where the scoping now lives (typically [00 §4](../00-product-vision.md)). A Withdrawn ADR **binds nothing**; any still-valid sub-conclusions are re-homed, and named, in the withdrawal note so they are not silently reopened.

First use: [ADR-PC-013](./retired/ADR-PC-013-aml-kyc-upstream-precondition.md) (AML/KYC), withdrawn because AML/KYC adjudication is upstream and out of scope per [00 §4](../00-product-vision.md). This amends the [§D5](#d5--file-naming-status-lifecycle-cross-linking) lifecycle vocabulary; §D1–§D4 are untouched.

## Amendment — 2026-06-04: supersede-clean on a contract-shape ADR's first contradicting amendment

[ADR-PC-022](./ADR-PC-022-product-documentation-architecture.md) named a legibility failure specific to the [§D3](#d3--contract-shape-a-complementary-template) contract-shape family: because the template deliberately carries a current-truth **contract** inside an immutable **decision record**, an Accepted contract-shape ADR that later takes a *contradicting* amendment forces the reader to replay `Decision + amendments` in their head to reconstruct the present. [ADR-PC-014](./retired/ADR-PC-014-customer-notification-emit-contract.md) is the worked example — its Amendment A1 negates the `SCHEDULED`-emission half of its Decision. This amendment names the conformant response so the next author reaches for it deliberately. It refines the [§D5](#d5--file-naming-status-lifecycle-cross-linking) *guidance on which mechanism to choose*; it adds no status and supersedes nothing.

**The convention.** *When an Accepted contract-shape ADR would take its **first contradicting** amendment — one that negates part of the `## Decision`, not merely clarifies or extends it — the conformant move is a **clean reissue via supersede**, not a stacked amendment.* The old ADR's Status flips to `Superseded by ADR-PC-NNN` (its Decision left intact as the historical record, per [§D5](#d5--file-naming-status-lifecycle-cross-linking)); the new ADR carries the **current contract as a single clean read**, with the change folded in as present tense. This is a recognised, non-pathological use of `Superseded` **even when the core decision still stands and only a sub-point moved** — the driver is legibility, not reversal.

**Additive amendments are unchanged.** A clarification, an added constraint, a new slot, a Verifiable-commitments backfill, a reference-add (the 2026-05-24 catalogue amendment) — anything for which the original Decision *still holds* — remains a [§D5](#d5--file-naming-status-lifecycle-cross-linking) amendment. Only a *contradiction* triggers the supersede preference. The test is the `amend-adr` question — *does the original `## Decision` still hold?* Yes → amend; **no → supersede-clean** (this convention sharpens the "no" branch for the contract-shape family).

**This honours — does not reverse — the 2026-06-03 "instances remain the contracts" design.** That amendment keeps each boundary contract as its own distinct ADR, *"because a reader debugging a specific seam needs the specific contract, and §D5 keeps Accepted decisions immutable."* Supersede-clean **strengthens** both clauses: the contract stays *in* the ADR (it is reissued as another contract-shape ADR — never extracted into a generalisation or a separate living spec), and §D5 immutability is preserved exactly (the old Decision is never edited, only marked `Superseded` and frozen as the record of what was once decided). The reader of the live ADR always sees one coherent contract; the reader of history follows the supersession chain. Moving a contract **body out of the ADR** into a living spec (the heavier "genre-extraction" alternative) is **not adopted** — it would remove the contract from the §D5-immutable, single-home regime this very design protects; it remains a documented future option that would need its own §D3/§D5 carve-out and a pin↔spec non-restatement gate before it could be used.

**Code-anchor caveat.** A `Superseded` ADR that **code anchors** trips the [ADR-PC-020](./ADR-PC-020-llm-toolchain-and-conformance-governance.md) "code-anchor → live ADR" check. For a *built* contract-shape ADR, either re-point the anchors to the reissue in the same change, or — if the contradiction is narrow enough that a reader is not misled — prefer an amend-in-place and record that judgement in the PR.

**First application.** [ADR-PC-014](./retired/ADR-PC-014-customer-notification-emit-contract.md) already took its contradicting Amendment A1 (2026-06-03) *before* this convention existed; its clean reissue (bd `babelstone-sfnt.13`) is the **retroactive cleanup** of that one pre-existing case, and the convention's worked example (it carries no code anchors, so the caveat above does not bite). Driven by [ADR-PC-022](./ADR-PC-022-product-documentation-architecture.md). This amends the [§D5](#d5--file-naming-status-lifecycle-cross-linking) guidance; §D1–§D4 are untouched.

## Amendment — 2026-06-04: Superseded and Withdrawn ADRs live in `adrs/retired/`

A `Superseded` or `Withdrawn` ADR **binds nothing** ([§D5](#d5--file-naming-status-lifecycle-cross-linking)); both are kept for history, but neither is part of the *live* decision set a reader navigates. Mixed in with the active ADRs they grow the namespace index with rows that no longer apply. They are therefore relocated to a **`retired/` subfolder** of each namespace's `adrs/` directory.

**The rule.** When an ADR's Status becomes `Superseded by …` or `Withdrawn`, **move its file into `adrs/retired/` in the same change** (`git mv`), alongside the supersession or withdrawal itself (`supersede-adr` / the `Withdrawn`-status flow). Two link sweeps ride with the move, both mechanical:

- **Inbound** — every link *to* the moved ADR gains the `retired/` segment (`./ADR-PC-014-….md` → `./retired/ADR-PC-014-….md`; `adrs/ADR-PC-014-….md` → `adrs/retired/ADR-PC-014-….md`).
- **The moved file's own links** — it now sits one level deeper, so each relative link gains one `../` (a link to *another* retired ADR stays same-directory).

A repo-wide relative-link check (every relative `./…` link resolves to a file) is the safety net. The [generated reference](./ADR-PC-022-product-documentation-architecture.md) `adr-index` scans `retired/` and renders the retired rows with their `Superseded`/`Withdrawn` status, so the cross-namespace landscape still shows the full set — the index, not the folder, is where "every ADR" lives.

**Tooling is `retired/`-aware**, so the move is never a silent gap: the `adr-index` generator scans `adrs/` *and* `adrs/retired/`; [`spec-coverage-check.sh`](../../../../.github/scripts/spec-coverage-check.sh)'s code-anchor resolver looks in both, so a code anchor to a retired ADR is still caught as pointing to a `Superseded`/`Withdrawn` ADR; and [`adr-immutability-check.sh`](../../../../.github/scripts/adr-immutability-check.sh) matches the `retired/` path and compares Decision *prose* (link hrefs normalised), so relocating a *referenced* ADR is not read as an in-place Decision edit.

First applied: [ADR-PC-013](./retired/ADR-PC-013-aml-kyc-upstream-precondition.md) (Withdrawn) and [ADR-PC-014](./retired/ADR-PC-014-customer-notification-emit-contract.md) (Superseded by [ADR-PC-025](./ADR-PC-025-customer-notification-emit-contract.md)). Applies to both namespaces (ADR-PC and ADR-IC); ADR-IC has no retired ADRs yet. This amends the [§D5](#d5--file-naming-status-lifecycle-cross-linking) file-organization convention; §D1–§D4 are untouched.
