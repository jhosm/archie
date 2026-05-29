# /contracts

The governed **contract surface** — the asset the whole build exists to preserve:

- **Avro** payload schemas ([ADR-IC-002](../docs/product-management/integration_concepts/adrs/ADR-IC-002-schema-format-and-registry.md))
- **CUE** constraint schemas — the family-schema language ([cue/](./cue/), [ADR-PC-006](../docs/product-management/product_concepts/adrs/ADR-PC-006-cue-schema-language.md))
- **EventCatalog** source ([ADR-IC-008](../docs/product-management/integration_concepts/adrs/ADR-IC-008-event-catalog-governance-tooling.md))

- **Build provenance:** in-house (product engine, "blue")
- **CODEOWNERS:** engine team
- **Path-scoped CI:** schema-compatibility checks + EventCatalog build

A contract change lands atomically with every producer/consumer that binds to it —
the decisive S2 reason for the monorepo ([ADR-PC-019](../docs/product-management/product_concepts/adrs/ADR-PC-019-repository-strategy-monorepo.md)).

> Status: the CUE family-schema language has landed ([cue/](./cue/), Epic C.1);
> Avro payload schemas + EventCatalog source are not yet present. Layout
> governed by [ADR-PC-019 §P1](../docs/product-management/product_concepts/adrs/ADR-PC-019-repository-strategy-monorepo.md).
