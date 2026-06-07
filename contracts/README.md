# /contracts

The governed **contract surface** — the asset the whole build exists to preserve:

- **Avro** payload schemas ([avro/](./avro/), [ADR-IC-002](../docs/product-management/integration_concepts/adrs/ADR-IC-002-schema-format-and-registry.md))
- **CUE** constraint schemas — the family-schema language ([cue/](./cue/), [ADR-PC-006](../docs/product-management/product_concepts/adrs/ADR-PC-006-cue-schema-language.md))
- **Event catalogue** source — AsyncAPI per event ([catalog/](./catalog/), [ADR-IC-015](../docs/product-management/integration_concepts/adrs/ADR-IC-015-event-catalog-governance-tooling-backstage.md))

- **Build provenance:** in-house (product engine, "blue")
- **CODEOWNERS:** engine team
- **Path-scoped CI:** schema-compatibility checks + the AsyncAPI catalogue gate

A contract change lands atomically with every producer/consumer that binds to it —
the decisive S2 reason for the monorepo ([ADR-PC-019](../docs/product-management/product_concepts/adrs/ADR-PC-019-repository-strategy-monorepo.md)).

> Status: the CUE family-schema language has landed ([cue/](./cue/), Epic C.1);
> the first Avro payload schemas have landed ([avro/](./avro/), Epic E.4 — the four
> term-deposit events); the event-catalogue source has landed ([catalog/](./catalog/),
> Epic G.4 — an AsyncAPI 3.0 file per event, gated by `scripts/asyncapi-catalog-validate.sh`).
> Layout governed by
> [ADR-PC-019 §P1](../docs/product-management/product_concepts/adrs/ADR-PC-019-repository-strategy-monorepo.md).
