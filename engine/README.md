# /engine

The product engine: a single-deployable **C# (.NET 9)** process with a hand-rolled
event-sourcing core, plus its PostgreSQL migrations.

- **Build provenance:** in-house (product engine, "blue")
- **Runtime / stack:** .NET 9 — [ADR-PC-010](../docs/product-management/product_concepts/adrs/ADR-PC-010-dotnet-hand-rolled-engine.md), PostgreSQL [ADR-PC-001](../docs/product-management/product_concepts/adrs/ADR-PC-001-event-store-technology.md)
- **CODEOWNERS:** engine team
- **Path-scoped CI:** `dotnet build` + analysers + Testcontainers suite ([ADR-IC-009](../docs/product-management/integration_concepts/adrs/ADR-IC-009-testing-infrastructure.md))

Hosts a per-service **outbox** worker ([ADR-IC-004](../docs/product-management/integration_concepts/adrs/ADR-IC-004-outbox-pattern-mechanism.md)) — the outbox is not its own top-level path.

> Status: skeleton — no source yet. Layout governed by [ADR-PC-019 §P1](../docs/product-management/product_concepts/adrs/ADR-PC-019-repository-strategy-monorepo.md).
