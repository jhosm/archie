# /notification

The notification estate is the **head of the customer-notice pipeline**: it owns the clock the
engine deliberately lacks ([ADR-PC-023](../docs/product-management/product_concepts/adrs/ADR-PC-023-temporal-signals-projection-derived.md)),
polls the deposit **maturity calendar**, and **detects when a customer notice is due** — today, the
14-day pre-maturity auto-renewal opt-out reminder. It stops at deciding a reminder is *due*: it does
**not** render the letter, resolve the recipient's PII, or send anything.

Rendering, PII resolution, and channel delivery (email / SMS / post, webhook callbacks, retry) are
the downstream **customer-communications system** ([ADR-PC-025](../docs/product-management/product_concepts/adrs/ADR-PC-025-customer-notification-emit-contract.md))
and the as-yet-unbuilt **delivery half** of this same estate
([ADR-IC-011](../docs/product-management/integration_concepts/adrs/ADR-IC-011-async-saga-completion-notification.md)) —
not built here. The directory is named for that whole pipeline role, broader than the scheduling
slice shipped so far.

- **Build provenance:** in-house estate — [ADR-IC-013](../docs/product-management/integration_concepts/adrs/ADR-IC-013-in-house-estate-build-and-repository-placement.md)
- **Runtime / stack:** .NET (stack-coherent with the engine) — [ADR-IC-011](../docs/product-management/integration_concepts/adrs/ADR-IC-011-async-saga-completion-notification.md)
- **Architecture:** family-agnostic core + family-owned contributions — [ADR-IC-019](../docs/product-management/integration_concepts/adrs/ADR-IC-019-family-agnostic-notification-platform.md)
- **CODEOWNERS:** engine team
- **Path-scoped CI:** `dotnet build` + contract tests

Runs as a per-service **outbox** worker ([ADR-IC-004](../docs/product-management/integration_concepts/adrs/ADR-IC-004-outbox-pattern-mechanism.md)) —
a long-running `BackgroundService`, the same shape the engine's outbox relay and the orchestrator's
consume loop run as.

## Layout

[ADR-IC-019](../docs/product-management/integration_concepts/adrs/ADR-IC-019-family-agnostic-notification-platform.md)
§D1/§D2 splits this estate into a **family-agnostic core** and **family-owned contributions** wired
together at a host edge — the same family-as-plugin shape the engine (`IFamilyHostModule`,
[ADR-PC-021](../docs/product-management/product_concepts/adrs/ADR-PC-021-application-layer-family-owned-deciders.md))
and the orchestrator (`ISagaModule`, [ADR-IC-018](../docs/product-management/integration_concepts/adrs/ADR-IC-018-family-owned-saga-modules.md))
already use.

**`src/Babelstone.Notification/`** — the family-**agnostic core LIBRARY** (`Microsoft.NET.Sdk`, not a
runnable exe). It must never know what a term deposit is, and references neither the engine kernel nor
any product family — gated by `NOTIFICATION_FAMILY_AGNOSTIC` ([ADR-IC-019](../docs/product-management/integration_concepts/adrs/ADR-IC-019-family-agnostic-notification-platform.md)
§D2/§P2; the `NotificationFamilyAgnosticTests` gate checks both the `.csproj` references *and* scans
source for embedded family literals):

- `NotificationWorker.cs` — the poll-loop `BackgroundService` that **owns the clock and cadence**
  (injected `TimeProvider`, [ADR-PC-023](../docs/product-management/product_concepts/adrs/ADR-PC-023-temporal-signals-projection-derived.md)
  §6): one scheduling pass per tick, exponential backoff (5-minute ceiling) on a read-surface failure.
- `NotificationSchedulePass.cs` — the per-tick engine: enumerate the registered family rules, stamp
  each returned decision with its composite id, admit it past the dedupe ledger.
- `IFamilyNotificationModule.cs` — the **family-contribution port** (`IFamilyNotificationModule` +
  `INotificationScheduleRule` + `NotificationModuleContext`), named by the
  [ADR-IC-019](../docs/product-management/integration_concepts/adrs/ADR-IC-019-family-agnostic-notification-platform.md)
  §D4 Amendment (2026-06-24). A faithful mirror of `IFamilyHostModule` / `ISagaModule`.
- `NotificationId.cs` + `NotificationDedupeLedger.cs` — the deterministic composite-id primitive
  (`instance_id + template_ref + occurrence` → name-based GUID; [ADR-PC-025](../docs/product-management/product_concepts/adrs/ADR-PC-025-customer-notification-emit-contract.md)
  slot 4) and the idempotency ledger, so a re-run or projection refresh never re-notifies.
- `ReminderDecision.cs` — the `ReminderDecision` / `RaisedReminder` records: structural interpolation
  amounts (integer cents) and references only — **no PII** ([ADR-PC-025](../docs/product-management/product_concepts/adrs/ADR-PC-025-customer-notification-emit-contract.md)).
- `DepositReadClient.cs` — **family-agnostic**, read-only access to deposit facts over the
  storage-opaque [ADR-PC-027](../docs/product-management/product_concepts/adrs/ADR-PC-027-deposit-read-surface-canonical-resource.md)
  read contract (`GET /v1/deposits/{id}` and the `GET /v1/deposits/maturities?from=&to=` maturity-calendar
  range scan), mapping the snake_case wire JSON into the core-local `DepositView` / `DepositMaturityView`
  — never the engine kernel + a family's internal projection types ([ADR-IC-019](../docs/product-management/integration_concepts/adrs/ADR-IC-019-family-agnostic-notification-platform.md)
  §D2/§D3).

**`src/Babelstone.Notification.Host/`** — the runnable **composition-root exe**
(`Microsoft.NET.Sdk.Worker`), the [ADR-IC-019](../docs/product-management/integration_concepts/adrs/ADR-IC-019-family-agnostic-notification-platform.md)
§A2/§D4 exemption holder and the **only place that names a family**. `Program.cs` resolves the engine
API endpoint (`Engine:BaseUrl`, a service endpoint not a credential), registers the typed read client,
`TimeProvider.System`, the dedupe ledger and the scheduler options, holds the **explicit list** of
`IFamilyNotificationModule` contributions (explicit-list-now / assembly-scan-later, [ADR-PC-021](../docs/product-management/product_concepts/adrs/ADR-PC-021-application-layer-family-owned-deciders.md)
§A3) with a duplicate-family guard, and wires OpenTelemetry ([ADR-IC-007](../docs/product-management/integration_concepts/adrs/ADR-IC-007-observability-stack.md))
on the shared `Babelstone.Engine` ActivitySource (`service.name=babelstone-notification`) from the
SDK-free `Babelstone.Telemetry` leaf — not the engine kernel.

**`src/Babelstone.Notification.Delivery/`** — the **delivery half** (bd `babelstone-60n8.4` /
`babelstone-60n8.7`): the ADR-IC-004 per-service **delivery outbox** drained by an
[ADR-IC-011](../docs/product-management/integration_concepts/adrs/ADR-IC-011-async-saga-completion-notification.md)
**HMAC-SHA256-signed webhook** client — at-least-once, §D4 exponential backoff (±25% jitter,
Retry-After honoured, permanent-4xx abandon, exhaustion dead-letter), idempotency anchored on the
composite `notification_id` ([ADR-PC-025](../docs/product-management/product_concepts/adrs/ADR-PC-025-customer-notification-emit-contract.md)
slot 4, consumer-side dedupe). ONE transport for BOTH legs, parameterised by `trigger_kind`: the
SCHEDULED leg arrives through the core's `INotificationDeliverySink` port; the EVENT_DRIVEN leg drains an
`INotificationDueSource` bus seam into the SAME outbox and renders the instance-pinned template per
attempt with **render-time PII resolution** over the engine's resolve surface (ADR-PC-025 §PII — PII
rides one POST transiently, never the outbox, never the bus). Family-agnostic like the core (gated by
`NOTIFICATION_FAMILY_AGNOSTIC`); composed by one host line
(`AddNotificationWebhookDelivery`, dormant until `Notification:Webhook:EndpointUrl` is configured).

**Family contributions** live family-side, not here — the term-deposit maturity-reminder rule (its
opt-out-window width pack-sourced via configuration, [ADR-PC-007](../docs/product-management/product_concepts/adrs/ADR-PC-007-signed-yaml-oci-pack.md))
is `families/term-deposit/src/Babelstone.Families.TermDeposit.Notification/`, which references this core
only (the `family → core` arrow).

> Status: **scheduler + webhook delivery + durable delivery store shipped; bus consumer deferred.** The
> estate owns the clock, polls the maturity calendar, raises deduplicated *due* reminders (bd
> `babelstone-60n8.2` + the [ADR-IC-019](../docs/product-management/integration_concepts/adrs/ADR-IC-019-family-agnostic-notification-platform.md)
> §D1 family-agnostic split), and **delivers** them over the ADR-IC-011 signed webhook through the
> ADR-IC-004 outbox (bd `babelstone-60n8.4`), with the EVENT_DRIVEN leg sharing the same transport
> (bd `babelstone-60n8.7`). The delivery outbox is **durable** (bd `babelstone-60n8.10`): configure
> `Notification:Delivery:ConnectionString` (or `ConnectionStrings:NotificationDelivery` /
> `BABELSTONE_NOTIFICATION_DELIVERY_CONNECTION`) and `PostgresDeliveryOutbox` replaces the in-memory
> v1 behind the port — the estate's own forward-only migration series (`Migrations/Sql/`, runtime role
> `babelstone_notification`) applies at boot, obligations survive a crash, and §D4 exhaustion
> dead-letters write the `NotificationDeliveryExhausted` announcement **in the same transaction**
> (ADR-IC-011 §P3 step 7), drained to the Redpanda backbone by the exhaustion relay when
> `Kafka:BootstrapServers` (+ `Bus:SchemaRegistryUrl`) is configured — the governed schema is
> `contracts/avro/operations/NotificationDeliveryExhausted.avsc`. What is **not** built yet: the
> engine-side EVENT_DRIVEN `NotificationDue` emission and its Redpanda consumer (the
> `INotificationDueSource` seam ships a Null default until then), and the engine's
> `GET /v1/pii/resolve` surface (the client tolerates its absence — notices render structurally). The
> SCHEDULER's dedupe ledger stays in-memory v1 (the emission child's concern, bd `babelstone-60n8.3`).
> Extraction-ready subtree per
> [ADR-PC-019 §P2](../docs/product-management/product_concepts/adrs/ADR-PC-019-repository-strategy-monorepo.md);
> placement per [ADR-IC-013](../docs/product-management/integration_concepts/adrs/ADR-IC-013-in-house-estate-build-and-repository-placement.md).
