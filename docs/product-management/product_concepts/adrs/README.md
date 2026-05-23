# Product Engine Architectural Decision Records

This folder holds the Architectural Decision Records (ADRs) for the **product engine itself** — the engine described in [product_concepts/01](../01-product-architecture.md). The concept documents and feature-design notes describe **what** the engine does; the ADRs decide **which tool** or **which contract** materialises each piece.

This is the peer namespace to [integration_concepts/adrs/](../../integration_concepts/adrs/README.md). That namespace governs the shared integration estate (broker, gateway, schema registry, ACL, observability, …); this one governs the engine's own concern surface: source of truth and state, configuration surface, engine runtime, boundary signal contracts, and coexistence with the legacy core.

[ADR-PC-000](./ADR-PC-000-namespace-and-contract-shape-framework.md) fixes the conventions used by every other ADR in this folder:

- **Tool-selection ADRs** reuse the [ADR-IC-000 evaluation framework](../../integration_concepts/adrs/ADR-IC-000-common-evaluation-criteria.md) without amendment — two hard filters (F1 cost, F2 regulatory fit) and four soft criteria (S1 operational complexity, S2 ecosystem coherence, S3 exit cost, S4 community longevity).
- **Contract-shape ADRs** use a complementary template (Decision / Consequences / Residual Risks) with six required slots — payload shape, semantics, ordering / delivery, idempotency, error model, ownership / versioning — and no F1/F2 evaluation table.
- Each ADR declares its `Shape:` in the front-matter table. When in doubt, default to tool-selection.

---

## ADR index

| # | Title | Shape | Chosen / Decision | Supports docs |
|---|---|---|---|---|
| [000](./ADR-PC-000-namespace-and-contract-shape-framework.md) | ADR-PC Namespace Conventions and Contract-Shape Framework | Conventions | Two templates: tool-selection (reuses ADR-IC-000) + contract-shape (six required slots); ADR-PC number space independent of ADR-IC | all |
| [001](./ADR-PC-001-event-store-technology.md) | Event Store Technology | Tool-selection | **PostgreSQL-based event store** — co-located with the [ADR-IC-004](../../integration_concepts/adrs/ADR-IC-004-outbox-pattern-mechanism.md) outbox in the same database so event-append and outbox-write commit in one local transaction; reserved `partition_key` envelope field as the v4 sharding seam. Amended 2026-05-23: library deferral filled with a **hand-rolled module** per ADR-PC-010 | [01 §2](../01-product-architecture.md), [event-store](../feature-design-event-store-projections.md), [two-modes §6](../feature-design-two-modes-asymmetry.md) |
| [006](./ADR-PC-006-cue-schema-language.md) | Family-Schema Language and Validator Runtime | Tool-selection | **CUE + purpose-built Go validator** — CUE constraint language for native cross-field expressiveness at depths 3–4; validated by a single static Go binary embedding `cuelang.org/go`, invoked out-of-process by authoring/CI and the engine; JSON Schema retained as the named fallback | [authoring §5](../feature-design-configuration-authoring.md), [surface §3.2 §3.10](../feature-design-configuration-surface.md) |
| [007](./ADR-PC-007-signed-yaml-oci-pack.md) | Pack Manifest Format and Distribution | Tool-selection | **Signed YAML in OCI artefact, CUE-validated** — auditor-readable YAML data + `.cue` constraint schemas; OCI artefact in the existing registry; cosign-signed; pulled by digest; per-instance pinning via `pack_version` envelope column | [01 §5](../01-product-architecture.md), [surface §3.5–§3.9](../feature-design-configuration-surface.md) |
| [010](./ADR-PC-010-dotnet-hand-rolled-engine.md) | Engine Implementation Language and Framework | Tool-selection | **C# (.NET 9) + hand-rolled event-sourcing core** — no Marten/Wolverine runtime dependency; the engine implements the [ADR-PC-001](./ADR-PC-001-event-store-technology.md) §P1–§P5 contract, the [ADR-IC-004](../../integration_concepts/adrs/ADR-IC-004-outbox-pattern-mechanism.md) outbox, and the [ADR-IC-003](../../integration_concepts/adrs/ADR-IC-003-saga-orchestrator.md) orchestrator directly; Marten/Wolverine kept as working reference implementations | [01 §6](../01-product-architecture.md), [event-store §10.4](../feature-design-event-store-projections.md), [two-modes §5.6 §8](../feature-design-two-modes-asymmetry.md) |

> ADR-PC-002 through ADR-PC-005, ADR-PC-008, ADR-PC-009, and ADR-PC-011 through ADR-PC-018 are tracked under bd epic `archie-10r` and will be filed against this index as each is accepted. The number reservations exist in bd; the on-disk files do not yet. Per ADR-PC-000 D1, the dual-check (`ls` + `bd list | grep ADR-PC`) is required before picking a new ADR-PC number.

---

## ADR conventions

The full convention statement lives in [ADR-PC-000](./ADR-PC-000-namespace-and-contract-shape-framework.md). Highlights restated here:

### Verdict format (tool-selection ADRs, from ADR-IC-000)

- **Pass** — the candidate satisfies the filter without qualification.
- **Pass (conditional)** — the candidate satisfies the filter only if a specific mitigation is documented in the same table cell and restated in Consequences or Residual Risks.
- **Fail** — the candidate is disqualified. A waiver requires explicit justification.

### Status lifecycle

- **Proposed** — drafted and open for review. No commitment yet.
- **Accepted** — committed and binding on downstream work.
- **Superseded by ADR-PC-NNN** — replaced. The superseded ADR stays in the folder; readers follow the link.
- **Rejected** — considered and not adopted. Kept as evidence the option was evaluated.

A change to an Accepted ADR is rare and requires either an amendment (dated entry appended) or supersession (a new ADR with a new number).

### Numbering

ADR-PC numbers are independent of ADR-IC numbers. Within the ADR-PC namespace, numbers are sequential and never reused. When picking a new number, check both the on-disk filenames (`ls product_concepts/adrs/`) and the planned-but-unwritten ADR-PC entries in the issue tracker (`bd list | grep ADR-PC`). The two share one number space within this namespace.

### File naming

`ADR-PC-NNN-short-kebab-case-slug.md`. The slug names the chosen tool or the decision topic, not the alternatives considered.

### Cross-linking

- ADR-PC to concept doc in the same series: `../NN-name.md`.
- ADR-PC to an ADR-IC: `../../integration_concepts/adrs/ADR-IC-NNN-…md`.
- ADR-PC to a financial-concepts doc: `../../financial_concepts/banking_products_financial_mathematics.md`.

These match the patterns codified in [CLAUDE.md](../../../../CLAUDE.md) and [AGENTS.md](../../../../AGENTS.md).
