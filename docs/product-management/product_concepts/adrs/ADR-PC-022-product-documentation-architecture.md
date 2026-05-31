# ADR-PC-022: Product Documentation Architecture — Diátaxis Overlay + Generated Reference

| Field | Value |
|---|---|
| Status | Proposed |
| Date | 2026-06-01 |
| Deciders | jhosm |
| Shape | Tool-selection |
| Common criteria | [ADR-IC-000](../../integration_concepts/adrs/ADR-IC-000-common-evaluation-criteria.md) (reused per [ADR-PC-000](./ADR-PC-000-namespace-and-contract-shape-framework.md) D2; this is the [ADR-PC-000 §D3](./ADR-PC-000-namespace-and-contract-shape-framework.md) "operational discipline" residual category — an engineering-practice decision, declared tool-selection per the [§D4](./ADR-PC-000-namespace-and-contract-shape-framework.md) default, the same class as [ADR-PC-019](./ADR-PC-019-repository-strategy-monorepo.md) / [ADR-PC-020](./ADR-PC-020-llm-toolchain-and-conformance-governance.md)) |
| Depends on | [ADR-PC-019](./ADR-PC-019-repository-strategy-monorepo.md) (the monorepo tree the docs live in, with `/docs` already a top-level path), [ADR-PC-020](./ADR-PC-020-llm-toolchain-and-conformance-governance.md) (the explicit-drift gate + Verifiable-commitments regime this decision must not undermine), [ADR-IC-008](../../integration_concepts/adrs/ADR-IC-008-event-catalog-governance-tooling.md) (EventCatalog-as-generated-governance — the precedent for generated, source-controlled reference), [ADR-IC-009](../../integration_concepts/adrs/ADR-IC-009-testing-infrastructure.md) (the path-scoped CI lane the `docs-verify` gate joins), [ADR-PC-006](./ADR-PC-006-cue-schema-language.md) / [ADR-PC-007](./ADR-PC-007-signed-yaml-oci-pack.md) (the CUE + pack artefacts a renderer reads) |
| Resolves | bd `babelstone-sfnt.1` (Epic R · R.1 — documentation architecture) |

---

## Context

The repository began as a documentation-only reference library and is now a hybrid docs + code monorepo ([ADR-PC-019](./ADR-PC-019-repository-strategy-monorepo.md)). The `docs/product-management/` corpus is organised on a **concern axis** — three self-contained series answering three questions (financial_concepts: *what math is correct*; product_concepts: *what configurable product implements it*; integration_concepts: *how it integrates*) plus two ADR namespaces. The series are read sequentially (00–11, 00–04) and bound together by an ADR index whose "Supports docs" column, the [commitment catalogue](./commitment-catalogue.md) Test-ID anchors, and the [CLAUDE.md](../../../../CLAUDE.md) hard-coded cross-link path rules all depend on that sequential layout staying exactly where it is.

The corpus is excellent **explanation** and design rationale. What it lacks — by [Diátaxis](https://diataxis.fr) doc-type — is everything else: there are **zero tutorials** (learning-oriented, run-it-yourself), **no how-to guides** (a specific goal), and reference material is scattered through prose rather than collected for lookup. A newcomer — an integrator at the incumbent bank, or a new family developer — can read the whole corpus and still not be able to *do* anything from it. The corpus also implies distinct audiences (the [C4 inventory](../feature-design-c4-architecture.md), the ACL/saga docs, the MCP channel, the pack toolchain each serve a different reader) but offers no role-shaped front door that sequences a reading path for any one of them.

> **The motivating question.** How do we add the missing doc types and a persona-shaped, progressively-disclosed front door **without** fracturing the single-source concern-axis spine or creating a normative-restatement surface that sits outside the [ADR-PC-020](./ADR-PC-020-llm-toolchain-and-conformance-governance.md) explicit-drift gate?

This entry is the [ADR-PC-000 §D3](./ADR-PC-000-namespace-and-contract-shape-framework.md) **residual category** ("operational discipline … fits neither template cleanly … default to tool-selection"), the same class as [ADR-PC-019](./ADR-PC-019-repository-strategy-monorepo.md) (repository strategy) and [ADR-PC-020](./ADR-PC-020-llm-toolchain-and-conformance-governance.md) (toolchain governance). The honest consequence, surfaced up front: **F1 and F2 do not discriminate.** Markdown costs nothing, is already in the tree, and documentation structure carries no PII and is not a DORA/PSD2 runtime artefact. The load-bearing question is not "which tool" but **which documentation layout serves the cold newcomer while preserving the drift-gated, single-source spine** — settled on the soft criteria plus a project-specific *governance-fit* dimension, not on the hard filters.

### Personas — the audiences the corpus already implies

Five personas ground the navigation layer (the canonical vocabulary; reading-paths reference these tags, nothing else):

| Persona | Job-to-be-done | Evidence in the corpus |
|---|---|---|
| **Integrator / solution architect** | Wire the engine into the bank's estate | [integration_concepts 00–11](../../integration_concepts/00-introduction-and-decisions.md) (ACL, saga, edge API, event catalogue) |
| **Family / engine developer** | Add a product family — decider + folds | [`families/`](../../../../families), [ADR-PC-021](./ADR-PC-021-application-layer-family-owned-deciders.md), [financial_concepts](../../financial_concepts/banking_products_financial_mathematics.md) |
| **Pack author / compliance** | Author + audit a `pt.YYYY.N` pack | [ADR-PC-006](./ADR-PC-006-cue-schema-language.md) / [ADR-PC-007](./ADR-PC-007-signed-yaml-oci-pack.md), [`pack-validate/`](../../../../pack-validate) |
| **Agent-channel consumer** | Drive the bank-as-MCP-server surface | [`mcp-server/`](../../../../mcp-server), [integration_concepts 11](../../integration_concepts/11-chat-agent-channel-strategy.md) |
| **Operator** | Run the stack; observe + recover | [`infra/`](../../../../infra), [integration_concepts 06](../../integration_concepts/06-observability-and-tracing.md), [ADR-PC-005](./ADR-PC-005-dr-rto-rpo.md) |

### Candidates evaluated

Four documentation architectures were generated as fully-committed, distinct alternatives and scored by an independent three-lens panel (a *maintainer* lens weighting drift-resistance and authoring cost, a *newcomer* lens weighting navigability and persona coverage, and an *auditor* lens weighting regulatory traceability and fit with the ADR/pack governance):

| # | Candidate | Top-level organising axis | Panel overall (1–5) |
|---|---|---|---|
| A | **Diátaxis Overlay** — concern-axis series stay the authoritative "explanation" body; add a typed `guides/` + `reference/` layer and a **link-only** persona reading-paths index. Personas = navigation, never physical structure. | Concern axis (unchanged) + doc-type for new material | **4.10** |
| B | **Audience Handbooks** — persona is the *top-level* structure; one self-contained handbook per reader, each progressively disclosing into its own guides + reference; a `_shared/` transclusion spine fights duplication. | Persona | 3.23 |
| C | **Journeys (JTBD)** — the user's *goal* is the top-level axis (constitute-a-deposit, author-a-pack, wire-the-ACL); personas become tags; progressive disclosure within each journey. | Goal / workflow | 3.23 |
| D | **Generated Reference** — `reference/` is 100 % generated from the machine-readable contracts (Avro / CUE / MCP / ADR index); humans hand-write only tutorials + explanation + how-to. | Provenance (generated vs hand-authored) | 3.50 |

The panel surfaced one decisive finding, sharper than the raw ranking: **the two candidates best for the cold newcomer (B and C, both 4.4 on the newcomer lens) are exactly the two worst on drift-resistance and audit (both 2.3 drift; auditor lens 2.6–2.8).** The cause is identical in both — they create a *large new hand-authored prose surface that restates normative content and sits outside the drift gate*. Conversely, the two strongest on governance (A corpus-fit 5.0; D drift-resistance 5.0) are weaker exactly where personas + progressive disclosure are supposed to help. The right answer is therefore not a pure pick but a **composition** that takes each rival's strength under A's spine while keeping the one rule that sank B and C.

---

## Evaluation

### Hard filter results

#### F1 · Cost / licensing

| Candidate | Licence / cost | Verdict |
|---|---|---|
| A · overlay | Markdown in-tree; one small generator for the reference subset. Zero incremental cost. | **Pass** |
| B · handbooks | Same, plus transclusion tooling. Zero licence cost. | **Pass** |
| C · journeys | Markdown in-tree. Zero cost. | **Pass** |
| D · generated | Generator + `make` target; reuses the existing CUE/Avro toolchain. Zero licence cost. | **Pass** |

Uniform pass — F1 does not discriminate (no candidate buys anything).

#### F2 · Regulatory fit (GDPR / DORA / PSD2)

Documentation layout carries no PII and is not a runtime artefact. The one regulatory-adjacent property a banking reviewer cares about is **auditable traceability**: the [ADR-PC-020](./ADR-PC-020-llm-toolchain-and-conformance-governance.md) explicit-drift gate, the §D5 immutability of Accepted ADR decisions, and the [commitment catalogue](./commitment-catalogue.md) single-source rule require that the corpus have exactly one authoritative home per normative claim, and that the decision-to-doc chain of custody (the ADR index "Supports docs" backlinks) stays intact. This is where the candidates split.

| Candidate | GDPR | DORA / PSD2 (auditable traceability) | Verdict |
|---|---|---|---|
| A · overlay | No PII. | Spine untouched; navigation links, never restates → single source preserved. | **Pass** |
| B · handbooks | No PII. | Per-handbook prose **restates** normative content into a surface outside the drift gate — a second home that can drift from the ADR/pack spine. | **Pass (conditional)** — requires a no-restatement lint + transclusion discipline that the architecture cannot mechanically guarantee |
| C · journeys | No PII. | Narrative journeys behind live links go stale silently; no mechanical gate catches a journey contradicting the ADR it links to. | **Pass (conditional)** — same residual; staleness uncatchable by any gate |
| D · generated | No PII. | Reference cannot drift *by construction* (regenerated + diffed in CI) — the strongest traceability on the board. | **Pass** |

A and D pass cleanly; B and C pass only conditionally on a discipline the architecture cannot enforce. Per the [ADR-IC-000](../../integration_concepts/adrs/ADR-IC-000-common-evaluation-criteria.md) convention a conditional pass must name its mitigation in-cell and carry it into Consequences — but for a *structural* decision, an unenforceable mitigation is itself the finding: it is the reason B and C are rejected as the *primary axis* below, while their genuine strengths are retained as a navigation layer that carries no normative restatement.

### Soft criteria

#### A · Overlay (as the spine) + D (for reference) — **CHOSEN as a hybrid**

**S1 · Operational complexity for 1–2 people.** Lowest among governance-safe options. The concern-axis series, both ADR namespaces, the commitment catalogue, and every existing cross-link stay byte-for-byte where they are — no relink, no renumber, no index rewrite. New material is purely additive under three new top-level siblings. The reference subset is *generated*, so the largest body of lookup material maintains itself; humans touch only tutorials, how-to, explanation, and a link-only index.

**S2 · Ecosystem coherence — decisive.** The corpus's defining property is that it is **contract-dense and single-source** ([ADR-PC-020](./ADR-PC-020-llm-toolchain-and-conformance-governance.md): one home per claim, drift forced explicit). The hybrid is the only candidate that *strengthens* that property instead of straining it: generated reference removes the largest hand-maintained-lookup drift surface that exists today (event payloads, schema fields, MCP tool signatures retyped into prose), and the link-only navigation invariant means the new newcomer-facing layer adds **zero** new normative homes. The [ADR-IC-008](../../integration_concepts/adrs/ADR-IC-008-event-catalog-governance-tooling.md) EventCatalog already establishes the precedent that governed reference is generated and source-controlled, not hand-written.

**S3 · Exit cost.** Low and reversible. The overlay is three additive directories; deleting them leaves the original corpus intact. If a future scale demands persona handbooks (B) or journeys (C) as primary structure, the reading-paths index is already the curated link-sequence those would be built from — the hybrid is a strict prerequisite for either, not a detour.

**S4 · Longevity.** Neutral — markdown + a small generator outlive any layout choice; the generator reads artefacts the build already maintains.

**Decisive project-specific reason — governance fit.** In a repository whose central discipline is an explicit-drift gate over a single-source spec corpus, a documentation architecture is judged first on whether it *honours that discipline*. A and D do (A leaves the spine untouched; D makes reference un-driftable). B and C, for all their newcomer appeal, manufacture exactly the silent-drift surface the whole governance regime exists to prevent. The hybrid keeps B's persona front-door and C's goal-shaped sequencing **as navigation** — link-only, restating nothing — so the newcomer wins are bought without the governance cost.

#### B · Audience Handbooks — **rejected as primary; its persona front-door retained as the link-only reading-paths index**

B has the best persona coverage and the matching top-level axis a newcomer arrives with (role before concern). But making persona the *physical* structure forces either duplication (a second normative home per fact → drift) or heavy transclusion tooling whose failure modes degrade the very newcomer experience B optimises for, and the restated prose sits wholly outside the drift gate. Rejected as the organising axis; its insight — *give the reader a role-shaped entry point* — is preserved exactly, as `reading-paths/` link-sequences that sequence existing + new material without copying it.

#### C · Journeys (JTBD) — **rejected as primary; its goal-shaped sequencing retained inside reading-paths and tutorials**

C's goal-shaped funnel is best-in-class progressive disclosure and is grounded in a proven precedent ([integration_concepts §05](../../integration_concepts/05-constitution-saga-walkthrough.md), the shipped MCP walking skeleton). But narrative journeys behind live links are the worst failure mode for a newcomer specifically — a journey that silently contradicts the ADR it links to, with no gate to catch it. Rejected as the organising axis; its insight — *sequence by what the reader is trying to do* — is preserved in the goal-shaped tutorials (`guides/tutorials/`) and the journey-flavoured persona paths, all link-only over the normative spine.

#### D · Generated Reference — **adopted for the reference quadrant; rejected as the top-level axis**

D's structural (not disciplinary) drift-resistance is the strongest property on the board and directly fixes A's one soft spot (A's hand-written synthesised reference tables were the panel's flagged underbelly). But organising the *whole* corpus by provenance (generated vs hand-authored) is an authoring concern the reader does not share — it scored worst on navigability and persona coverage precisely because "is this page generated?" is not a question a newcomer asks. Adopted for what it is good at (reference) and nowhere else.

**Decisive reason for the hybrid over any pure candidate:** the corpus's first constraint is its single-source, drift-gated governance; its acute gap is the cold newcomer. A preserves the governance, D removes the one drift surface A retained, and B/C's newcomer wins are obtainable as pure navigation. No single candidate clears both; their composition does.

---

## Decision

### A Diátaxis overlay (spine) + a generated reference quadrant + a link-only persona/journey navigation index.

The three concern-axis series and both ADR namespaces stay **physically untouched** — the authoritative "explanation" body and the single source of truth. Three additive top-level siblings are introduced under `docs/product-management/`:

- **`guides/`** — the missing hand-authored doc types: `tutorials/` (learning-oriented, run-it-yourself, numbered) and `how-to/` (goal-oriented).
- **`reference/`** — **100 % generated** lookup material, rendered from the machine-readable contracts and regenerated-and-diffed in CI; every page carries a *do-not-edit* banner.
- **`reading-paths/`** — a thin, **link-only** index keyed by the five personas, each a curated shallow→deep sequence across existing + new material.

The **load-bearing invariant**, binding all three: *the overlay links to normative content; it never restates it.* The drift-gated spine ([ADR-PC-020](./ADR-PC-020-llm-toolchain-and-conformance-governance.md)) remains the single home for every normative claim; reference is generated so it cannot drift; guides and reading-paths cite and sequence, they do not copy.

**Rejected: audience handbooks (B) as primary** — persona-as-structure forces a normative-restatement surface outside the drift gate; its persona front-door is retained as the link-only reading-paths index. **Rejected: journeys (C) as primary** — narrative staleness behind live links is uncatchable by any gate; its goal-shaped sequencing is retained in tutorials and persona paths. **Adopted narrowly: generated reference (D)** for the reference quadrant only; rejected as the top-level axis because provenance is an authoring concern, not a reader's.

This decision is realised by Epic R (bd `babelstone-sfnt`): the scaffold (R.2), the generator + CI gate (R.3/R.4), the reading-paths (R.5), tutorials (R.6), how-to guides (R.7), and the glossary single-home (R.8).

---

## Implementation Principles

### P1 — Three additive siblings; the concern-axis spine is never moved

```
docs/product-management/
  financial_concepts/      ← UNTOUCHED (explanation: the math)
  product_concepts/        ← UNTOUCHED (00–04 + feature-design-* + adrs/ + commitment-catalogue)
  integration_concepts/    ← UNTOUCHED (00–11 + adrs/)
  guides/                  ← NEW · hand-authored
    README.md                 explains the overlay + Diátaxis typing; links the reading-paths
    tutorials/                learning-oriented, numbered for sequence
    how-to/                   goal-oriented, one task each
  reference/               ← NEW · 100% GENERATED (do-not-edit banner; make docs-verify gates it)
    README.md                 generated index, grouped by source kind
    events/                   from contracts/avro/**/*.avsc
    family-schemas/           from contracts/cue/**/*.cue
    mcp-tools/                from the mcp-server tool surface
    adr-index/                from ADR front-matter (both namespaces)
    pack-format/              from the pack/rate-sheet CUE + ADR-PC-007 layout
  reading-paths/           ← NEW · link-only persona index
    README.md                 the persona router (the newcomer front door)
    <one path per persona>    curated shallow→deep link-sequences
```

No existing file moves; no cross-link is rewritten; the ADR index, the commitment catalogue, and the [CLAUDE.md](../../../../CLAUDE.md) path rules keep working unchanged. The overlay obeys the existing relative-link conventions ([ADR-PC-000 §D5](./ADR-PC-000-namespace-and-contract-shape-framework.md) / CLAUDE.md): a guide linking a sibling-folder concept doc uses `../integration_concepts/NN-name.md`, and so on.

### P2 — Reference is generated, banner-marked, and CI-gated (the un-driftable quadrant)

A generator renders `reference/` from the machine-readable sources — one renderer per source kind (Avro `.avsc` → event pages with payload tables; CUE → family-schema pages; the MCP tool defs → tool surface; ADR front-matter → cross-namespace index; the pack CUE + [ADR-PC-007](./ADR-PC-007-signed-yaml-oci-pack.md) layout → pack-format reference). Two `make` targets mirror the [contracts-check](../../../../Makefile) idiom:

- `make docs-gen` — regenerate `reference/`.
- `make docs-verify` — regenerate into a scratch tree and diff against the committed `reference/`; **a non-empty diff fails**.

A path-scoped CI lane ([ADR-IC-009](../../integration_concepts/adrs/ADR-IC-009-testing-infrastructure.md), [ADR-PC-019 §P1](./ADR-PC-019-repository-strategy-monorepo.md)) runs `docs-verify` whenever a reference source *or* the generator changes, so stale generated reference cannot reach `main` — the same structural guarantee [ADR-IC-008](../../integration_concepts/adrs/ADR-IC-008-event-catalog-governance-tooling.md) gives the EventCatalog. Every generated page opens with a `<!-- DO NOT EDIT — generated by make docs-gen from <source> -->` banner.

### P3 — The link-only invariant (no normative restatement in the overlay)

`guides/` and `reading-paths/` **cite and sequence** the normative spine; they do not copy a normative decision, a contract slot, a financial formula, or a defined term into themselves. Vocabulary lives once (the R.8 glossary single-home under `reference/`); guides link to it rather than redefining. This is the property that keeps the newcomer-facing layer *outside* the set of things that can drift — and the reason B/C were rejected as primary. It is enforced by review and by the generated-banner discipline; the residual (prose how-to going stale against the code it describes) is a listed, accepted risk below, mitigated by keeping how-to thin and link-heavy.

### P4 — Five personas, defined once, referenced as tags

The persona vocabulary (integrator, family-developer, pack-author/compliance, agent-channel-consumer, operator — Context table) is defined once in `reading-paths/README.md`. Reading-paths and tutorial front-matter *reference* these tags; they are not redefined per document. Adding a persona is a single edit to the vocabulary plus a new link-sequence — no structural change.

### P5 — Off the build critical path; soft-related, never blocking

Epic R is P2, parallel, and carries soft `relates_to` (never `blocks`) edges to the build epics whose surfaces it documents (Epic Q owns the `docs-verify` CI lane; future Epics E/F/C/G/J supply the tutorials' subject matter). Documentation never gates a build merge; a build merge that changes a reference source merely re-runs `docs-gen`.

---

## Consequences

**What this choice makes easier:**

1. **A cold newcomer gets a front door.** The persona reading-paths give the role-shaped, progressively-disclosed entry point the corpus completely lacks today, and the tutorials give the first-ever run-it-yourself path from "what is this" to "I did the thing."
2. **The largest hand-maintained drift surface disappears.** Event payloads, schema fields, MCP signatures, and the ADR index are generated from their sources — they cannot drift, and `make docs-verify` proves it in CI.
3. **The governance spine is untouched.** Every existing cross-link, the ADR "Supports docs" backlinks, the commitment-catalogue anchors, and the CLAUDE.md path rules keep working byte-for-byte; the drift gate's chain of custody is preserved.
4. **Cheap, reversible, and a prerequisite for more.** The overlay is three additive directories; if a future scale justifies full handbooks or journeys, the reading-paths index is the curated link-set they are built from.

**What this choice makes harder or impossible:**

1. **The link-only invariant is a convention, not a wall.** A guide author *can* paste a normative paragraph in violation of P3; no mechanical gate fully catches prose restatement. Mitigation: keep guides thin and link-heavy; the glossary single-home (R.8) removes the most common reason to restate; review enforces the rest. The decision deliberately accepts this residual rather than pay B/C's structural drift cost.
2. **The generator is a standing build artefact.** `reference/` renderers must track their source formats (Avro, CUE, the MCP tool surface, ADR front-matter). Mitigation: each renderer reads an artefact the build already maintains and validates ([ADR-PC-006](./ADR-PC-006-cue-schema-language.md), [ADR-IC-008](../../integration_concepts/adrs/ADR-IC-008-event-catalog-governance-tooling.md)); `docs-verify` fails loudly when a source shape outruns its renderer.

**Residual risks:**

- **How-to prose staleness.** A how-to guide can fall behind the code it describes without tripping `docs-verify` (which only gates the *generated* reference). Mitigation: how-to guides link to generated reference and concept docs for the load-bearing detail, keeping the hand-authored part procedural and thin; staleness is a documentation bug, not a silent spec divergence.
- **Persona sprawl.** Five personas is a deliberate ceiling; adding more dilutes the reading-paths into a maintenance burden. Mitigation: P4 makes adding one cheap but visible; the ceiling is a review norm.

---

## Verifiable commitments

These commitments are documentation-scoped (not engine load-bearing), so they live as an inline table here rather than in the engine-focused [commitment catalogue](./commitment-catalogue.md) (per [ADR-PC-000 §A2](./ADR-PC-000-namespace-and-contract-shape-framework.md), the catalogue is the seed of the *engine's* ~8 invariants; this ADR adds its own gates without enlarging that seed). A `Gap` is a deliberate, listed hole — visibility is the point.

| # | Commitment (with §-anchor) | Gate (pyramid level) | Test ID | Status |
|---|---|---|---|---|
| 1 | `reference/` is byte-identical to a fresh `make docs-gen` from its sources (§P2) — generated reference cannot drift. | analyser / CI (`docs-verify` lane) | `DOCS_REFERENCE_NO_DRIFT` | Live (ci.yml `docs-verify` lane; `make docs-verify`) |
| 2 | Every page under `reference/` carries the do-not-edit generated banner (§P2). | analyser / CI (`docs-verify` lane) | `DOCS_REFERENCE_BANNERED` | Live (ci.yml `docs-verify` banner step) |
| 3 | The concern-axis series + both ADR namespaces are unmodified by Epic R (§P1) — the spine is additive-only. | review / CI path-scope (no diffs under the three series from a docs-overlay PR) | `DOCS_SPINE_UNTOUCHED` | Planned |
| 4 | The overlay restates no normative content (§P3) — guides/reading-paths cite, never copy, the spec corpus. | review (no mechanical gate fully proves prose non-restatement) | `DOCS_OVERLAY_LINK_ONLY` | Gap (deliberate — review-enforced; the structural reason B/C were rejected) |

---

## Open Actions

1. **Scaffold the overlay** (R.2) — the §P1 three siblings + READMEs + the CLAUDE.md/AGENTS.md "overlay links, never duplicates" convention.
2. **Build the generator + CI gate** (R.3/R.4) — `make docs-gen`/`docs-verify`, the five renderers, the path-scoped lane; flip `DOCS_REFERENCE_NO_DRIFT` / `DOCS_REFERENCE_BANNERED` to Live.
3. **Author the reading-paths, tutorials, how-to, glossary** (R.5–R.8).
4. **Wire the soft relates_to edges** to Epics E/F/C/G/J once those are materialised in bd (§P5).

---

## Cross-references

- [ADR-PC-019](./ADR-PC-019-repository-strategy-monorepo.md) — the monorepo (`/docs` a top-level path) the overlay extends; the path-scoped-CI discipline the `docs-verify` lane joins.
- [ADR-PC-020](./ADR-PC-020-llm-toolchain-and-conformance-governance.md) — the explicit-drift gate + Verifiable-commitments regime this decision is judged against and must not undermine.
- [ADR-IC-008](../../integration_concepts/adrs/ADR-IC-008-event-catalog-governance-tooling.md) — EventCatalog-as-generated-governance: the precedent that governed reference is generated and source-controlled.
- [ADR-IC-009](../../integration_concepts/adrs/ADR-IC-009-testing-infrastructure.md) — the path-scoped CI gates the `docs-verify` lane runs under.
- [ADR-PC-006](./ADR-PC-006-cue-schema-language.md) / [ADR-PC-007](./ADR-PC-007-signed-yaml-oci-pack.md) — the CUE + pack artefacts the family-schema and pack-format renderers read.
- [feature-design-c4-architecture](../feature-design-c4-architecture.md) — the role partition that grounds the five personas.
- [README](../../../../README.md) — the top-level document map the overlay's front door complements.

---

*Decided 2026-06-01 by jhosm.*
