# /engine

The product engine: a single-deployable **C# (.NET 10)** process with a hand-rolled
event-sourcing core, plus its PostgreSQL migrations.

- **Build provenance:** in-house (product engine, "blue")
- **Runtime / stack:** .NET 10 — [ADR-PC-010](../docs/product-management/product_concepts/adrs/ADR-PC-010-dotnet-hand-rolled-engine.md), PostgreSQL [ADR-PC-001](../docs/product-management/product_concepts/adrs/ADR-PC-001-event-store-technology.md)
- **CODEOWNERS:** engine team
- **Path-scoped CI:** `dotnet build` + analysers + Testcontainers suite ([ADR-IC-009](../docs/product-management/integration_concepts/adrs/ADR-IC-009-testing-infrastructure.md)); a separate **periodic** mutation lane (`.github/workflows/mutation.yml`, Stryker.NET) guards test effectiveness off the per-push path.
- **Design docs (implementation companions):** [event-store-skeleton](./docs/event-store-skeleton.md) — the C# expression of Epic A (event store + outbox + handler dispatch + PII envelope + determinism gate); [mutation-testing](./docs/mutation-testing.md) — the Stryker score floor + event-sourcing mutants of interest + surviving-mutant triage (A.10).

Hosts a per-service **outbox** worker ([ADR-IC-004](../docs/product-management/integration_concepts/adrs/ADR-IC-004-outbox-pattern-mechanism.md)) — the outbox is not its own top-level path.

> Status: financial-math kernel in progress; Epic A event store underway —
> `Babelstone.EventStore.Migrations` (A.1) ships the `events` + `outbox` DDL and a
> hand-rolled migration runner. `Babelstone.slnx` builds and its unit + Roslyn-analyser
> tests run in path-scoped CI (the engine job in `.github/workflows/ci.yml`, now
> `--filter "Category!=Integration"`); the Testcontainers integration tier — including
> the A.1 schema/role suite tagged `Category=Integration` — runs in the lane that lands
> with E.6. Layout governed by
> [ADR-PC-019 §P1](../docs/product-management/product_concepts/adrs/ADR-PC-019-repository-strategy-monorepo.md).
