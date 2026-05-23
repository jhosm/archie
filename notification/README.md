# /notification

The async **saga-completion notification** service — delivers completion signals
without coupling callers to saga internals.

- **Build provenance:** in-house estate — [ADR-IC-013](../docs/product-management/integration_concepts/adrs/ADR-IC-013-in-house-estate-build-and-repository-placement.md)
- **Runtime / stack:** .NET (stack-coherent with the engine) — [ADR-IC-011](../docs/product-management/integration_concepts/adrs/ADR-IC-011-async-saga-completion-notification.md)
- **CODEOWNERS:** engine team
- **Path-scoped CI:** `dotnet build` + contract tests

Hosts a per-service **outbox** worker ([ADR-IC-004](../docs/product-management/integration_concepts/adrs/ADR-IC-004-outbox-pattern-mechanism.md)).

> Status: skeleton — no source yet. Extraction-ready subtree per [ADR-PC-019 §P2](../docs/product-management/product_concepts/adrs/ADR-PC-019-repository-strategy-monorepo.md); placement per [ADR-IC-013](../docs/product-management/integration_concepts/adrs/ADR-IC-013-in-house-estate-build-and-repository-placement.md).
