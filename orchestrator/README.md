# /orchestrator

The in-house **saga orchestrator** — a Redpanda consumer that drives multi-step
sagas with compensation, persisting saga state as rows in its application database.

- **Build provenance:** in-house estate ("estate by role, in-house by provenance") — [ADR-IC-013](../docs/product-management/integration_concepts/adrs/ADR-IC-013-in-house-estate-build-and-repository-placement.md)
- **Runtime / stack:** .NET — the decisive S2 reason in [ADR-IC-003](../docs/product-management/integration_concepts/adrs/ADR-IC-003-saga-orchestrator.md) is that the orchestrator "speaks the same language as every other service in the stack" (the .NET engine, [ADR-PC-010](../docs/product-management/product_concepts/adrs/ADR-PC-010-dotnet-hand-rolled-engine.md))
- **CODEOWNERS:** engine team
- **Path-scoped CI:** `dotnet build` + contract tests

> Status: skeleton — no source yet. Extraction-ready subtree per [ADR-PC-019 §P2](../docs/product-management/product_concepts/adrs/ADR-PC-019-repository-strategy-monorepo.md); placement per [ADR-IC-013](../docs/product-management/integration_concepts/adrs/ADR-IC-013-in-house-estate-build-and-repository-placement.md).
