# /notification

The async **saga-completion notification** service — delivers completion signals
without coupling callers to saga internals.

- **Build provenance:** in-house estate — [ADR-IC-013](../docs/product-management/integration_concepts/adrs/ADR-IC-013-in-house-estate-build-and-repository-placement.md)
- **Runtime / stack:** .NET (stack-coherent with the engine) — [ADR-IC-011](../docs/product-management/integration_concepts/adrs/ADR-IC-011-async-saga-completion-notification.md)
- **CODEOWNERS:** engine team
- **Path-scoped CI:** `dotnet build` + contract tests

Hosts a per-service **outbox** worker ([ADR-IC-004](../docs/product-management/integration_concepts/adrs/ADR-IC-004-outbox-pattern-mechanism.md)).

## Layout

`src/Babelstone.Notification/` is the worker host (`Microsoft.NET.Sdk.Worker`, the same
hosted-`BackgroundService` shape the engine's outbox relay and the orchestrator's consume loop
run as):

- `Program.cs` — the composition root: resolves the read-model connection at the
  [ADR-PC-004](../docs/product-management/product_concepts/adrs/ADR-PC-004-pii-crypto-shredding.md) Amendment A1
  credential boundary, registers the projection store + reader + worker, and wires OpenTelemetry
  ([ADR-IC-007](../docs/product-management/integration_concepts/adrs/ADR-IC-007-observability-stack.md)) on the
  shared `Babelstone.Engine` ActivitySource (`service.name=babelstone-notification`).
- `TermDepositProjectionReader.cs` — typed, read-only access to the engine's term-deposit
  projections over the [ADR-IC-005](../docs/product-management/integration_concepts/adrs/ADR-IC-005-cqrs-read-model-storage.md)
  read surface (PostgreSQL, the sole read-model store): `maturity_calendar`, `accrual_schedule`,
  and `withholding_ledger` — all registered today in the family's `TermDepositProjectionModule`.
- `NotificationWorker.cs` — the standing host shell.

> Status: **skeleton host + read access** (bd `babelstone-60n8.1`). The host stands up and can read
> the three projections; there is **no scheduler timing and no event emission yet** — those are the
> downstream children (bd `babelstone-60n8.2` maturity timing loop, `babelstone-60n8.3`
> `NotificationDue` emission contract). Extraction-ready subtree per
> [ADR-PC-019 §P2](../docs/product-management/product_concepts/adrs/ADR-PC-019-repository-strategy-monorepo.md);
> placement per [ADR-IC-013](../docs/product-management/integration_concepts/adrs/ADR-IC-013-in-house-estate-build-and-repository-placement.md).
