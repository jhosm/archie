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

- `Program.cs` — the composition root: resolves the engine API endpoint (`Engine:BaseUrl`, a service
  endpoint not a credential), registers the typed read client + worker, and wires OpenTelemetry
  ([ADR-IC-007](../docs/product-management/integration_concepts/adrs/ADR-IC-007-observability-stack.md)) on the
  shared `Babelstone` ActivitySource (`service.name=babelstone-notification`), from the SDK-free
  `Babelstone.Telemetry` leaf — not the engine kernel.
- `DepositReadClient.cs` — **family-agnostic**, read-only access to deposit facts over the
  storage-opaque [ADR-PC-027](../docs/product-management/product_concepts/adrs/ADR-PC-027-deposit-read-surface-canonical-resource.md)
  read contract (`GET /v1/deposits/{id}`), mapping the snake_case wire JSON into the core-local
  `DepositView` — never the engine kernel + a family's internal projection types
  ([ADR-IC-019](../docs/product-management/integration_concepts/adrs/ADR-IC-019-family-agnostic-notification-platform.md)
  §D2/§D3). The maturity date + accrued-interest / withholding rollups it returns drive the maturity
  notice.
- `NotificationWorker.cs` — the standing host shell.

> Status: **skeleton host + read access** (bd `babelstone-60n8.1`, relocated onto the family-agnostic
> read contract by `babelstone-60n8.5`). The host stands up and can read a deposit over the ADR-PC-027
> contract; there is **no scheduler timing and no event emission yet** — those are the downstream
> children (bd `babelstone-60n8.2` maturity timing loop, `babelstone-60n8.3` `NotificationDue` emission
> contract). The core carries **no engine-kernel or family reference** — gated by
> `NOTIFICATION_FAMILY_AGNOSTIC` ([ADR-IC-019](../docs/product-management/integration_concepts/adrs/ADR-IC-019-family-agnostic-notification-platform.md)
> §P2). Extraction-ready subtree per
> [ADR-PC-019 §P2](../docs/product-management/product_concepts/adrs/ADR-PC-019-repository-strategy-monorepo.md);
> placement per [ADR-IC-013](../docs/product-management/integration_concepts/adrs/ADR-IC-013-in-house-estate-build-and-repository-placement.md).
