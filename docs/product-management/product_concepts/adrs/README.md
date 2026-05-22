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

> ADR-PC-001 through ADR-PC-018 are tracked under bd epic `archie-10r` and will be filed against this index as each is accepted. The number reservations exist in bd; the on-disk files do not yet. Per ADR-PC-000 D1, the dual-check (`ls` + `bd list | grep ADR-PC`) is required before picking a new ADR-PC number.

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
