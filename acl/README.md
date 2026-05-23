# /acl

The **anti-corruption layer** service(s) — a dedicated service per bounded context
with its **own database** (idempotency keys, ID mappings, in-flight operations,
indeterminate-state dead-letter). Hand-rolled translation to/from the Core.

- **Build provenance:** in-house estate — [ADR-IC-013](../docs/product-management/integration_concepts/adrs/ADR-IC-013-in-house-estate-build-and-repository-placement.md)
- **Runtime / stack:** .NET (stack-coherent with the engine; resilience via Polly per [ADR-IC-012](../docs/product-management/integration_concepts/adrs/ADR-IC-012-anti-corruption-layer-implementation.md))
- **CODEOWNERS:** engine team
- **Path-scoped CI:** `dotnet build` + contract tests

Hosts a per-service **outbox** worker ([ADR-IC-004](../docs/product-management/integration_concepts/adrs/ADR-IC-004-outbox-pattern-mechanism.md)).

> Status: skeleton — no source yet. Extraction-ready subtree per [ADR-PC-019 §P2](../docs/product-management/product_concepts/adrs/ADR-PC-019-repository-strategy-monorepo.md); placement per [ADR-IC-013](../docs/product-management/integration_concepts/adrs/ADR-IC-013-in-house-estate-build-and-repository-placement.md).
