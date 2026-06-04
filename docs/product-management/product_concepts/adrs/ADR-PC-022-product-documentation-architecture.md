# ADR-PC-022: Product Documentation Architecture — Generated Reference, and the Spec/Decision Genre Discipline

| Field | Value |
|---|---|
| Status | Proposed |
| Date | 2026-06-04 |
| Deciders | jhosm |
| Shape | Tool-selection |
| Common criteria | [ADR-IC-000](../../integration_concepts/adrs/ADR-IC-000-common-evaluation-criteria.md) (reused per [ADR-PC-000 §D2](./ADR-PC-000-namespace-and-contract-shape-framework.md); this is the [ADR-PC-000 §D3](./ADR-PC-000-namespace-and-contract-shape-framework.md) "operational discipline" residual category — an engineering-practice decision declared tool-selection per the [§D4](./ADR-PC-000-namespace-and-contract-shape-framework.md) default, the same class as [ADR-PC-019](./ADR-PC-019-repository-strategy-monorepo.md) / [ADR-PC-020](./ADR-PC-020-llm-toolchain-and-conformance-governance.md)) |
| Depends on | [ADR-PC-019](./ADR-PC-019-repository-strategy-monorepo.md) (the monorepo tree the docs live in), [ADR-PC-020](./ADR-PC-020-llm-toolchain-and-conformance-governance.md) (the explicit-drift gate + Verifiable-commitments regime this decision is judged against), [ADR-IC-008](../../integration_concepts/adrs/ADR-IC-008-event-catalog-governance-tooling.md) (EventCatalog-as-generated-governance — the precedent for generated, source-controlled reference), [ADR-IC-009](../../integration_concepts/adrs/ADR-IC-009-testing-infrastructure.md) (the path-scoped CI lane the `docs-verify` gate joins), [ADR-PC-006](./ADR-PC-006-cue-schema-language.md) / [ADR-PC-007](./ADR-PC-007-signed-yaml-oci-pack.md) (the CUE + pack artefacts a renderer reads), [ADR-PC-000 §D5](./ADR-PC-000-namespace-and-contract-shape-framework.md) (the lifecycle this decision's genre discipline extends — via a pending dated amendment, bd `babelstone-sfnt.12`) |
| Resolves | bd `babelstone-sfnt.9` (Epic R · R.9 — documentation architecture, re-scoped). Repurposes the framing of bd `babelstone-sfnt.1` (R.1 — the original overlay proposal) |
| Related | [ADR-PC-014](./retired/ADR-PC-014-customer-notification-emit-contract.md) (the worked example of the genre problem — see §Context), [ADR-PC-023](./ADR-PC-023-temporal-signals-projection-derived.md) (the source of PC-014's contradicting amendment), [ADR-PC-000 §D3 signal-contract amendment](./ADR-PC-000-namespace-and-contract-shape-framework.md) (the 2026-06-03 "instances remain the contracts" design this decision honours) |

> **Re-proposal note (2026-06-04).** This ADR was first drafted 2026-06-01 as *"Diátaxis Overlay + Generated Reference"* — a three-part navigation overlay (`guides/` + generated `reference/` + persona `reading-paths/`) realised by Epic R (R.1–R.8, PR #81). That PR was **not merged**: in use, the overlay made the corpus *harder* to read, not easier — it added front doors to a base whose **current truth was itself hard to extract**. The original proposal never reached `Accepted`, so this is a rewrite of a live proposal, not a §D5 supersession. The audit trail of the original four-architecture exploration is preserved in §Evaluation; what changed is the *diagnosis* (§Context).

> **Update — Open Action 5 resumed (2026-06-04).** The deferred onboarding/tutorials rebuild (problem **B**; §P3, Open Action 5) is now being **resumed**, because §P3's precondition — *"a base that has been made legible first"* — is met: the generated `reference/` quadrant is `Live` (commitments #1–#2) and the PC-014 → [ADR-PC-025](./ADR-PC-025-customer-notification-emit-contract.md) clean reissue plus the `retired/` moves have landed on `main`. The rebuild begins as a **Diátaxis product-docs tree at `docs/product-docs/`** (PR #88 — a proof-of-concept for the pack + rate-sheet workflow), aimed at product users (config authors): a *distinct audience* from this concern-axis design corpus.
>
> This is **additive, not a reversal** — it leaves Decision parts **1** (keep the generated reference) and **3** (the genre discipline) untouched and executes the deferred clause of part **2** / §P3. It is explicitly **not** the rejected 2026-06-01 overlay: the new tree carries only **tutorials / how-to / explanation** (no second `reference/` — it *links into* the generated one), restates **no** normative content (**link-don't-restate**), and re-introduces **no `reading-paths/`** persona overlay (future personas get more pages in the same three quadrants, never a persona tree). A separate audience-facing deliverable is not a navigation layer over the corpus, so it does not re-incur the "front doors over an illegible base" objection (§Evaluation) that sank the original. Follow-ups: bd `babelstone-sfnt.15` / `.16` / `.17`, `babelstone-fk7m.8`.

---

## Context

The repository began as a documentation-only reference library and is now a hybrid docs + code monorepo ([ADR-PC-019](./ADR-PC-019-repository-strategy-monorepo.md)). The `docs/product-management/` corpus is organised on a **concern axis** — three self-contained series (financial_concepts: *what math is correct*; product_concepts: *what configurable product implements it*; integration_concepts: *how it integrates*) plus two ADR namespaces. It is excellent **explanation** and design rationale.

The first proposal (2026-06-01) read the gap as a **missing-doc-types** problem: no tutorials, no how-to guides, reference scattered through prose, and no role-shaped front door. It answered with an overlay. Building and living with that overlay surfaced a sharper, different problem — and that re-diagnosis is what this ADR records.

### Two problems were conflated; they are orthogonal

> **The motivating correction.** The pain the maintainer actually felt was *"it is very difficult to understand what is the current truth."* That is **not** the onboarding gap the overlay set out to fix. Two distinct problems were fused:
>
> - **(A) Current-truth legibility** — can a reader tell what is true *now*, without reconstructing it? This is the felt pain.
> - **(B) Onboarding / missing doc types** — can a newcomer *do* something from the corpus? This is what the overlay targeted.
>
> The overlay improved (B) and made (A) **worse**: more entry points (a guide, a reading-path, a reference page, a concept doc, an ADR) to the same un-deduplicated truth means *more* "which one is current?", not less. This ADR separates the two — **keeps** what serves (A), **drops** what served (B) at (A)'s expense, and **defers** (B) to a later, clean rebuild.

### The genre diagnosis (problem A, at the root)

The corpus fuses **two document genres with opposite relationships to time**:

- A **decision record** is a journal entry — *"on date D, in context C, we chose X."* It is inherently historical; rewriting it to current truth destroys its reason to exist. History is its point.
- A **specification** is a wiki page — it must always read as **current truth**; its history belongs in Git.

The [§D3 contract-shape template](./ADR-PC-000-namespace-and-contract-shape-framework.md) deliberately carries a **contract specification inside an immutable decision record** (the six filled slots). On a one-paragraph decision, immutability ([§D5](./ADR-PC-000-namespace-and-contract-shape-framework.md)) is painless. On a contract that later changes, it is not: the only §D5-permitted edits are an appended **Amendment** or a supersession, so the reader of an amended contract-shape ADR must **replay `Decision + amendments` in their head** to reconstruct the present.

**The worked example — [ADR-PC-014](./retired/ADR-PC-014-customer-notification-emit-contract.md)** (customer-notification emit contract). Its `## Decision` says the engine emits `SCHEDULED` `NotificationDue` events and runs an internal scheduler. Its **Amendment A1** (top of file) and an inline **`*Revised 2026-06-03*`** line (in a residual risk) say it no longer does — [ADR-PC-023](./ADR-PC-023-temporal-signals-projection-derived.md) moved temporal triggers downstream. To answer *"does the engine emit scheduled notifications?"* a reader must read the Decision (**yes**), hold Amendment A1 (**no**), and recompute. That replay burden **is** the unreadability.

> **This is n = 1 today.** Of the contract-shape family — [PC-012](./ADR-PC-012-gl-posting-signal-contract.md) (GL), PC-014 (notifications), [PC-015](./ADR-PC-015-ifrs9-signal-contract.md) (IFRS 9), [PC-024](./ADR-PC-024-constitution-precondition-contract.md) (preconditions), and the coexistence contracts [PC-016](./ADR-PC-016-legacy-current-account-adapter.md)/[PC-017](./ADR-PC-017-legacy-batch-ingest-contract.md)/[PC-018](./ADR-PC-018-channel-routing-coexistence.md) — **only PC-014 carries a contradicting amendment.** The rest are single-decision contracts that read clean. The remedy below is sized to n = 1, not to a corpus-wide affliction that does not exist.

### The design this decision must honour, not reverse

[ADR-PC-000](./ADR-PC-000-namespace-and-contract-shape-framework.md)'s **2026-06-03 amendment** ("the signal-contract design principle") states, one day before this decision: *"A reader debugging a specific seam needs the specific contract, and §D5 keeps Accepted decisions immutable. The generalisations name the pattern; the instances remain the contracts."* That is an on-the-record, deliberate re-affirmation of the contract-**in**-the-ADR design. Per the [ADR-PC-020 §D3 explicit-drift gate](./ADR-PC-020-llm-toolchain-and-conformance-governance.md), a remedy that silently reversed it would itself be the silent drift the whole methodology exists to forbid. The remedy below is therefore chosen to **strengthen** that design (every Accepted contract stays a single clean read), not to gut it.

---

## Evaluation

The original exploration generated four documentation architectures as fully-committed alternatives, scored by an independent three-lens panel (a *maintainer* lens weighting drift-resistance + authoring cost, a *newcomer* lens weighting navigability + persona coverage, an *auditor* lens weighting traceability + governance fit):

| # | Candidate | Top-level organising axis | Panel overall (1–5) |
|---|---|---|---|
| A | **Diátaxis Overlay** — concern-axis series stay authoritative; add a typed `guides/` + `reference/` layer + a link-only persona reading-paths index. | Concern axis + doc-type | **4.10** |
| B | **Audience Handbooks** — persona is the *top-level* structure; one self-contained handbook per reader. | Persona | 3.23 |
| C | **Journeys (JTBD)** — the user's *goal* is the top-level axis; personas become tags. | Goal / workflow | 3.23 |
| D | **Generated Reference** — `reference/` is 100% generated from the machine-readable contracts; humans hand-write only tutorials + explanation + how-to. | Provenance (generated vs hand-authored) | 3.50 |

The panel's decisive finding: **the two best for the cold newcomer (B, C) are the two worst on drift-resistance and audit**, because both create *a large hand-authored prose surface that restates normative content outside the drift gate*. Conversely, **D's drift-resistance (5.0) is the strongest property on the board** — generated reference cannot drift *by construction*.

**What the lived outcome added to the panel.** F1/F2 are uniform (markdown is in-tree and carries no PII; this is the [§D3](./ADR-PC-000-namespace-and-contract-shape-framework.md) residual category), so the decision turns on the soft axis — and use refined it. The panel had already convicted B/C of building a restatement surface. The maintainer's rejection of the *winning* candidate A extends that verdict: **even a link-only overlay is a legibility cost** when it multiplies front doors over a base whose current truth is not yet legible. Navigation cannot fix legibility; it can only add indirection on top of it. That leaves exactly one candidate that serves problem (A) *structurally* rather than cosmetically — **D**, generated reference — because derived, CI-diffed reference is the one part of the corpus that *cannot* present a stale "current truth." D survives on its own merits; A/B/C's newcomer value is real but addresses the deferred problem (B), and is not bought at the cost of (A).

---

## Decision

The documentation architecture is **the generated-reference quadrant, plus a genre discipline that keeps decision-records legible — and *not* a navigation overlay.** Concretely, three parts:

### 1. Keep the generated `reference/` quadrant — on its own merits

The 100%-generated reference tree (events from Avro, family-schemas from CUE, the MCP tool surface, the cross-namespace ADR index, the pack-format layout), rendered by `make docs-gen` and gated byte-identical by `make docs-verify` in a path-scoped CI lane, is **adopted** — justified standalone as candidate D (drift-resistance 5.0), **not** as half of an A+D hybrid bound by a "link-only invariant." It is the one asset of the 2026-06-01 work that directly serves current-truth legibility: it removes the largest hand-maintained drift surface (payloads, schema fields, tool signatures retyped into prose) and replaces it with a view that cannot lie about the present. (Brought over from PR #81 and confirmed standalone by bd `babelstone-sfnt.10`.)

### 2. Drop the navigation overlay

The persona `reading-paths/` and the hand-authored `guides/` (tutorials + how-to) are **dropped** (bd `babelstone-sfnt.11`). They served the onboarding problem (B) and, in doing so, multiplied front doors over an un-deduplicated base — worsening (A). Onboarding is not abandoned; it is **deferred** to a later rebuild on a base that has been made legible first (the run-it-yourself material, including the `make demo-mcp` walkthrough, is recoverable from PR #81's history when that rebuild happens).

### 3. Adopt the genre discipline for current-truth legibility

Current truth is made legible **without relocating contract bodies out of their ADRs** — honouring the 2026-06-03 "instances remain the contracts" design:

- **Rendering-first (the structural fix).** A generated *collapsed current-truth view* flattens an amended contract-shape ADR's `Decision + amendments + inline Revised` lines into one present-tense reading under `reference/` — drift-proof, CI-diffed, the ADR source left immutable. This removes the replay burden for **every** amended ADR with zero content moved. (Deferred to proven need, bd `babelstone-sfnt.14`; PC-014 is resolved directly below in the interim.)
- **Supersede-clean on first contradicting amendment (the convention).** When an Accepted contract-shape ADR would take its **first contradicting** (not merely additive) amendment, the conformant move is a **clean reissue via supersede** — the old ADR flips to `Superseded by …` (its Decision preserved as the historical record), the new ADR carries the current contract as a single clean read. This is recorded as a dated [§D5](./ADR-PC-000-namespace-and-contract-shape-framework.md) amendment to [ADR-PC-000](./ADR-PC-000-namespace-and-contract-shape-framework.md) (bd `babelstone-sfnt.12`) — **the governance change lives there, not here** ([§D4](./ADR-PC-000-namespace-and-contract-shape-framework.md): a tool-selection ADR must not staple a conventions change into itself). **[ADR-PC-014](./retired/ADR-PC-014-customer-notification-emit-contract.md) is the pilot reissue** (bd `babelstone-sfnt.13`). PC-014 already took its contradicting Amendment A1 (2026-06-03) *before* this convention existed; the pilot is the **retroactive cleanup** of that one pre-existing case, not evidence the convention was breached.
- **No corpus-wide extraction.** Moving contract bodies into living specs (the heavier alternative) is **not adopted**: the pain is n = 1, and extraction would reverse the day-old [§D3](./ADR-PC-000-namespace-and-contract-shape-framework.md) design, strip §D5 immutability from the contract body, and open a pin↔spec divergence surface nothing currently gates. It remains a documented future option behind a proven-need trigger, requiring its own §D3/§D5 carve-out and a non-restatement gate if ever taken up.

**The through-line:** *make current truth legible by deriving it (generated reference, collapsed view) or by re-issuing it clean (supersede) — never by adding a layer over it (overlay) or by moving it somewhere a gate cannot watch (extraction).*

---

## Implementation Principles

### P1 — Generated reference is the kept quadrant; the spine is untouched

`reference/` is rendered from the machine-readable sources and lives under `docs/product-management/reference/`; the three concern-axis series and both ADR namespaces are **not moved or restated**. One renderer per source kind (Avro `.avsc` → event pages; CUE → family-schemas; the MCP tool defs → tool surface; ADR front-matter → cross-namespace index; the pack CUE + [ADR-PC-007](./ADR-PC-007-signed-yaml-oci-pack.md) layout → pack-format). The overlay's three-sibling scaffold collapses to **one** generated sibling.

### P2 — Reference is generated, banner-marked, and CI-gated (the un-driftable quadrant)

Two `make` targets mirror the [contracts-check](../../../../Makefile) idiom: `make docs-gen` regenerates `reference/`; `make docs-verify` regenerates into a scratch tree and diffs against the committed tree — **a non-empty diff fails**. A path-scoped CI lane ([ADR-IC-009](../../integration_concepts/adrs/ADR-IC-009-testing-infrastructure.md), [ADR-PC-019 §P1](./ADR-PC-019-repository-strategy-monorepo.md)) runs `docs-verify` whenever a reference source *or* the generator changes — the same structural guarantee [ADR-IC-008](../../integration_concepts/adrs/ADR-IC-008-event-catalog-governance-tooling.md) gives the EventCatalog. Every generated page opens with a `<!-- DO NOT EDIT — generated by make docs-gen from <source> -->` banner.

### P3 — The overlay is dropped; onboarding is deferred, not solved here

`guides/` and `reading-paths/` are removed, and any README / [CLAUDE.md](../../../../CLAUDE.md) / AGENTS.md references to them are repointed at the generated reference + the concept docs (bd `babelstone-sfnt.11`). The newcomer front door and run-it-yourself tutorials are a **separate future effort**, to be rebuilt on the legible base — explicitly out of scope here so that fixing (A) is not held hostage to (B).

### P4 — Current-truth legibility is a genre discipline, not a structure change

Legibility is achieved by **deriving** current truth (generated reference §P1–§P2; the collapsed view, deferred) and by **re-issuing clean on contradiction** (supersede). Contract bodies **stay in their ADRs**. The governing convention is a dated [ADR-PC-000 §D5](./ADR-PC-000-namespace-and-contract-shape-framework.md) amendment (bd `babelstone-sfnt.12`), referenced here, not authored here.

### P5 — Scope is n = 1; the day-old design is honoured

Only [ADR-PC-014](./retired/ADR-PC-014-customer-notification-emit-contract.md) demonstrably carries the replay problem; it is the pilot (bd `babelstone-sfnt.13`). Corpus-wide body extraction is **deferred behind a proven-need trigger** (an ADR must actually accrete a contradicting amendment), not adopted as a default — keeping faith with the 2026-06-03 [§D3](./ADR-PC-000-namespace-and-contract-shape-framework.md) "instances remain the contracts" design.

### P6 — Off the build critical path; soft-related, never blocking

Epic R is P2, parallel, carrying soft `relates_to` (never `blocks`) edges to the build epics whose surfaces it documents. Documentation never gates a build merge; a build merge that changes a reference source merely re-runs `docs-gen`.

---

## Consequences

**What this choice makes easier:**

1. **Current truth becomes legible by derivation.** Generated reference cannot present a stale present; a clean PC-014 reissue removes the one replay-the-log read in the corpus. The reader stops reconstructing the present in their head.
2. **The governance stays conformant *and* coherent.** Supersede-clean keeps contracts in their ADRs, so the 2026-06-03 design is honoured, not reversed — no silent drift against [ADR-PC-020 §D3](./ADR-PC-020-llm-toolchain-and-conformance-governance.md). The genre discipline lives in [ADR-PC-000](./ADR-PC-000-namespace-and-contract-shape-framework.md) where conventions belong.
3. **The largest hand-maintained drift surface disappears** (P2) — and `make docs-verify` proves it in CI.
4. **The corpus gets simpler, not layered.** One generated sibling replaces three; the maintainer's "stop adding layers over an illegible base" objection is answered by removal, not addition.

**What this choice makes harder or gives up:**

1. **No newcomer front door, for now.** Dropping the overlay re-opens the onboarding gap (B). Accepted deliberately: (A) is fixed first; (B) is a tracked future rebuild on the clean base.
2. **The generated reference is a standing build artefact.** Its renderers must track their source formats; mitigated because each reads an artefact the build already maintains ([ADR-PC-006](./ADR-PC-006-cue-schema-language.md), [ADR-IC-008](../../integration_concepts/adrs/ADR-IC-008-event-catalog-governance-tooling.md)) and `docs-verify` fails loudly when a source shape outruns its renderer.

**Residual risks:**

- **The collapsed-view renderer is deferred.** Until it lands (bd `babelstone-sfnt.14`), a *second* amended contract-shape ADR would re-introduce a replay read; the supersede-clean convention (P4) is the interim answer, and n = 1 makes the window small. Named so the reopen is a tracked decision, not a surprise.
- **Supersede has a code-anchor caveat.** Reissuing an ADR that code anchors would break the [ADR-PC-020](./ADR-PC-020-llm-toolchain-and-conformance-governance.md) "code-anchor → live ADR" check; verified safe for PC-014 (no anchors), but the convention (bd `babelstone-sfnt.12`) must flag it for any built ADR.

---

## Verifiable commitments

These commitments are documentation-scoped (not engine load-bearing), so they live as an inline table here rather than in the engine-focused [commitment catalogue](./commitment-catalogue.md) (per [ADR-PC-000 §A2](./ADR-PC-000-namespace-and-contract-shape-framework.md): the catalogue seeds the *engine's* invariants; this ADR adds its own gates without enlarging that seed). Both are `Live`, gated by the `docs-verify` CI lane.

| # | Commitment (with §-anchor) | Gate (pyramid level) | Test ID | Status |
|---|---|---|---|---|
| 1 | `reference/` is byte-identical to a fresh `make docs-gen` from its sources (§P2) — generated reference cannot drift. | analyser / CI (`docs-verify` lane) | `DOCS_REFERENCE_NO_DRIFT` | Live (ci.yml `docs-verify` lane; `make docs-verify`) |
| 2 | Every page under `reference/` carries the do-not-edit generated banner (§P2). | analyser / CI (`docs-verify` lane) | `DOCS_REFERENCE_BANNERED` | Live (ci.yml `docs-verify` banner step) |

> The genre-discipline commitment (supersede-clean on a contradicting amendment) is a **governance** rule and is bound where it is authored — the dated [ADR-PC-000 §D5](./ADR-PC-000-namespace-and-contract-shape-framework.md) amendment (bd `babelstone-sfnt.12`), enforced by the existing `adr-immutability` gate (a reissue requires a `Superseded by …` line). The overlay-only commitments of the 2026-06-01 draft (`DOCS_SPINE_UNTOUCHED`, `DOCS_OVERLAY_LINK_ONLY`) are **dropped** with the overlay.

---

## Open Actions

1. ✅ **Generated-reference engine + `docs-verify` lane kept** (bd `babelstone-sfnt.10`) — cherry-picked from PR #81, confirmed standalone (the `docs-verify` CI lane is green); commitments #1/#2 are **Live**.
2. **Drop the overlay** (bd `babelstone-sfnt.11`) — delete `guides/` + `reading-paths/`; repoint README / CLAUDE.md / AGENTS.md.
3. **Amend [ADR-PC-000 §D5](./ADR-PC-000-namespace-and-contract-shape-framework.md)** (bd `babelstone-sfnt.12`) — bless supersede-clean-on-contradiction; honour (do not rebut) the 2026-06-03 "instances remain the contracts" passage.
4. **Supersede [ADR-PC-014](./retired/ADR-PC-014-customer-notification-emit-contract.md) clean — the pilot** (bd `babelstone-sfnt.13`).
5. **Partly resumed (2026-06-04):** the **onboarding/tutorials rebuild on the clean base** is now under way — see the *Update* note near the top — as the Diátaxis product-docs tree in PR #88. Still **DEFERRED:** the collapsed current-truth-view renderer (bd `babelstone-sfnt.14`) and corpus-wide extraction behind a proven-need trigger.

---

## Cross-references

- [ADR-PC-000](./ADR-PC-000-namespace-and-contract-shape-framework.md) — the §D3 contract-shape template (the genre fusion's origin), the §D5 lifecycle this decision's discipline extends, and the 2026-06-03 "instances remain the contracts" design it honours.
- [ADR-PC-014](./retired/ADR-PC-014-customer-notification-emit-contract.md) — the worked example of the replay-the-log problem; the pilot reissue.
- [ADR-PC-023](./ADR-PC-023-temporal-signals-projection-derived.md) — the source of PC-014's contradicting Amendment A1.
- [ADR-PC-019](./ADR-PC-019-repository-strategy-monorepo.md) — the monorepo + path-scoped-CI discipline the `docs-verify` lane joins.
- [ADR-PC-020](./ADR-PC-020-llm-toolchain-and-conformance-governance.md) — the explicit-drift gate + Verifiable-commitments regime this decision is judged against.
- [ADR-IC-008](../../integration_concepts/adrs/ADR-IC-008-event-catalog-governance-tooling.md) — EventCatalog-as-generated-governance: the precedent that governed reference is generated and source-controlled.
- [ADR-IC-009](../../integration_concepts/adrs/ADR-IC-009-testing-infrastructure.md) — the path-scoped CI the `docs-verify` lane runs under.
- [ADR-PC-006](./ADR-PC-006-cue-schema-language.md) / [ADR-PC-007](./ADR-PC-007-signed-yaml-oci-pack.md) — the CUE + pack artefacts the family-schema and pack-format renderers read.
- [README](../../../../README.md) — the top-level document map.

---

*Re-proposed 2026-06-04 by jhosm (rewrites the 2026-06-01 overlay proposal; never Accepted, so not a §D5 supersession).*
