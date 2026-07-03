# /lifecycle-driver

The lifecycle-command driver is the **downstream, clock-owning actor that fires the engine's due
lifecycle commands** — a deposit's *maturity*, a personal loan's monthly *installment* — on their due
dates ([ADR-PC-036](../docs/product-management/product_concepts/adrs/ADR-PC-036-lifecycle-command-driver.md)).
The engine deliberately holds **no clock** ([ADR-PC-023](../docs/product-management/product_concepts/adrs/ADR-PC-023-temporal-signals-projection-derived.md)):
a handler that reads the clock fails the build, and no clock-driven engine event type may exist
(`NO_CLOCK_DRIVEN_ENGINE_SIGNAL`, BENG004). The *fact* of maturing or paying an installment is recorded
perfectly well by the engine's pure decider behind the
[ADR-PC-029](../docs/product-management/product_concepts/adrs/ADR-PC-029-engine-command-ingress.md)
command endpoint — what was **missing** is the actor that *issues that command on the due date*. This is
that actor.

In plain terms: this is a new always-on service that **owns the clock**, ticks on a cadence, reads a
forward calendar projection to find which occurrences are due today, and turns each into an **HTTP POST**
to the engine's existing command endpoint. It lives in its **own** host — not inside the read-only
notification context, not inside the engine (a timer there trips BENG004) — and it reaches the engine
**only** over the command surface, never the byte store and never by making the engine read a clock.

- **Build provenance:** in-house estate — [ADR-IC-013](../docs/product-management/integration_concepts/adrs/ADR-IC-013-in-house-estate-build-and-repository-placement.md)
- **Runtime / stack:** .NET (stack-coherent with the engine) — [ADR-IC-011](../docs/product-management/integration_concepts/adrs/ADR-IC-011-async-saga-completion-notification.md)
- **Mechanism:** the shared [`Babelstone.Cadence`](../cadence/) poll-loop machinery (worker + per-tick pass
  + idempotency), the same mechanism the notification scheduler proved out — [ADR-IC-019](../docs/product-management/integration_concepts/adrs/ADR-IC-019-family-agnostic-notification-platform.md)
- **CODEOWNERS:** engine team
- **Path-scoped CI:** `dotnet build` + the Docker-free dispatch-ledger / schedule-pass / command-sink tests

Runs as a clock-owning poll-loop worker — a long-running `BackgroundService`, the same shape the engine's
outbox relay, the orchestrator's consume loop, and the notification scheduler run as.

## Why a separate sibling host

[ADR-PC-036](../docs/product-management/product_concepts/adrs/ADR-PC-036-lifecycle-command-driver.md)
§Decision 2 (candidate A, with B folded in) places the driver in its **own deployable**, deliberately:

- **Not in the engine assembly** — a clock/timer inside the engine trips `NO_CLOCK_DRIVEN_ENGINE_SIGNAL`
  (BENG004). The driver is *downstream*: it owns the clock and reaches the engine only by POSTing the
  [ADR-PC-029](../docs/product-management/product_concepts/adrs/ADR-PC-029-engine-command-ingress.md)
  command surface, so the decider's purity and the lifecycle legality gate still run.
- **Not in `Babelstone.Notification`** — the driver moves money; isolating its scoped money-command
  credential from the read-only disclosures context is candidate B's one durable point, honoured here by a
  separate host + a scoped service principal.

It **mirrors** the notification worker host shape (`Microsoft.NET.Sdk.Worker`, a `BackgroundService`
poll loop) and **shares** the [`Babelstone.Cadence`](../cadence/) machinery with it — the clock-owning
worker, the per-tick `ISchedulePass`, the cadence/backoff knobs — so the proven notification cadence and
this driver's cadence are one tested mechanism. The driver only adds the lifecycle-specific *sink*: POST a
command, rather than raise a reminder.

## Layout

**`src/Babelstone.Lifecycle/`** — the driver **core LIBRARY** (`Microsoft.NET.Sdk`, not a runnable exe):

- `LifecycleWorker.cs` — the clock-owning poll-loop `BackgroundService`, a thin
  [`Babelstone.Cadence.CadenceWorker`](../cadence/) subclass (its own log category). The clock lives here,
  in a downstream sibling host ([ADR-PC-023](../docs/product-management/product_concepts/adrs/ADR-PC-023-temporal-signals-projection-derived.md)
  §6), never in the engine.
- `LifecycleSchedulePass.cs` — the per-tick engine (an `ISchedulePass`): enumerate the registered family
  rules, derive each due occurrence's number-pinned dispatch id, **claim** it on the dispatch ledger (skip a
  re-tick, a restart replay, or a competing replica's in-flight claim), **POST** through the sink, and — only
  on success — **record** the dispatch as the claim commits. Claim-then-POST-then-record so a transient
  engine outage never strands a due command (the un-recorded claim releases; the next pass retries; the
  engine dedupes).
- `ILifecycleCommandRule.cs` — the **family-contribution port** (`ILifecycleCommandRule` +
  `LifecycleCommandDecision` + `DispatchedCommand` + `ILifecycleCommandSink`), the write-side mirror of the
  notification core's `INotificationScheduleRule`. A family rule reads its own forward calendar and says
  which occurrences are due; it never reimplements the idempotency.
- `ILifecycleDispatchLedger.cs` / `PostgresLifecycleDispatchLedger.cs` — the **durable dispatch ledger**
  ([ADR-PC-038](../docs/product-management/product_concepts/adrs/ADR-PC-038-lifecycle-driver-leader-election-and-durable-ledger.md)):
  one Postgres `lifecycle_dispatch_ledger` row per due occurrence, keyed on the canonical,
  **server-derived, number-pinned** dispatch id
  (`LifecycleCommandKey.Derive(instance_id, command_kind, stable_occurrence_key)` — referenced from the
  engine hosting seam, **not** reinvented; LCD-1,
  [ADR-PC-036](../docs/product-management/product_concepts/adrs/ADR-PC-036-lifecycle-command-driver.md)
  §Decision 1+3). The per-occurrence **atomic claim** — `FOR UPDATE SKIP LOCKED` plus a per-instance,
  salt-namespaced `pg_try_advisory_xact_lock`, the same competing-consumers pattern as the saga dispatcher
  and the outbox relay — **is** the multi-replica single-firing guard (no elected leader,
  `LIFECYCLE_DRIVER_SINGLE_FIRING`/LCD-4), and the table's persistence is the crash-survival + queryable
  `dispatched_at` audit trail (`LIFECYCLE_DISPATCH_LEDGER_DURABLE`/LCD-5). The engine's `command_dedup` is
  the authoritative idempotency backstop regardless. `InMemoryLifecycleDispatchLedger.cs` is the
  claim-faithful Docker-free test double; `Migrations/` is the driver host's **own** forward-only migration
  series (embedded `Sql/NNNN_*.sql`, applied at boot).
- `HttpLifecycleCommandSink.cs` — the production `ILifecycleCommandSink`: POSTs the engine's
  [ADR-PC-029](../docs/product-management/product_concepts/adrs/ADR-PC-029-engine-command-ingress.md)
  command endpoint, presenting the canonical key as the `Idempotency-Key` header and the **scoped,
  non-interactive SCA service principal** (`X-SCA-Service-Principal`) on a money-mover route. A non-success
  engine response is backpressure (it throws). This is the **only** runtime path the driver takes to the
  engine.

**`src/Babelstone.Lifecycle.Host/`** — the runnable **composition-root exe**
(`Microsoft.NET.Sdk.Worker`). `Program.cs` resolves the engine command endpoint (`Engine:BaseUrl`, a
service endpoint not a credential) and the **dispatch-ledger database** (`Lifecycle:LedgerConnectionString`
/ `ConnectionStrings:LifecycleLedger` / `BABELSTONE_LIFECYCLE_LEDGER_CONNECTION` — fail-loud, plus an
optional distinct migration-role connection `Lifecycle:LedgerMigrationConnectionString`), applies the
ledger's own forward-only migration series at boot (a hosted service registered before the worker),
registers the typed command-POST `HttpClient`, `TimeProvider.System`, the durable Postgres dispatch ledger
and the cadence knobs, registers the per-tick pass over the family rules, and runs the clock-owning
worker. Family `ILifecycleCommandRule` contributions plug in here with zero core diff.

**Family rules** are the sibling work that lands on this host, and now have
([bd `babelstone-6cpq.8`](../docs/product-management/product_concepts/adrs/ADR-PC-036-lifecycle-command-driver.md) /
`babelstone-6cpq.9`):

- `MaturityRule` (term-deposit maturity, the one-shot case over the `maturity_calendar`) — reads the deposit
  read model as-of today, fires `Mature` once per Active deposit that has reached maturity, on/after the
  maturity date only, under the canonical `("mature", 1)` key. It INHERITS the built renewal opt-out /
  saga-start gates (bd `babelstone-mtto.3`) and encodes none.
- `InstallmentRule` (personal-loan installment, the recurring case over the `installment_calendar`) — reads
  the forward installment calendar, fires `PayInstallment` for the next-unpaid occurrence per Active loan
  under the number-pinned `("pay_installment", installment-number)` key, advancing to N+1 only once N is
  recorded paid.

Both live in the driver core as concrete `ILifecycleCommandRule`s and are composed in `Program.cs`; the rules
read the family read-model stores (the host wires the Npgsql implementations) and reach the engine ONLY by
POSTing through the sink. The recurring **settlement-health gate** (`LIFECYCLE_DRIVER_SETTLEMENT_HEALTH_GATE`,
LCD-2, ADR-PC-036 §Decision 4) is a separate follow-up — not encoded by either rule yet.

## Idempotency, in one line

The **dispatch id is the engine idempotency key** — both are
`LifecycleCommandKey.Derive(instance_id, command_kind, stable_occurrence_key)`, derived the *same way the
engine derives it*, so a manual operator, the MCP agent, and this driver converge on **one** key per
occurrence. It is **number-pinned**: the recurring occurrence key is the stable installment *number*, never
the due-date, so a re-dated or backfilled retry of occurrence N dedupes to one money leg
(`LIFECYCLE_COMMAND_NUMBER_PINNED_IDEMPOTENT`). The engine's `command_dedup` is the authoritative backstop
(`ENGINE_COMMAND_IDEMPOTENT`); the dispatch ledger is the cheap front-line that keeps the driver from
re-POSTing every tick.

## Monitoring, health, and on-call

The driver is an always-on **money-mover whose worst failures are silent** — a wedged tick loop just stops
firing, and a parked settlement stalls a schedule with no arrears state to catch the miss. Its
observability surface (bd `babelstone-1nkm.4`) is OpenTelemetry parity with the notification sibling
(tracing + logs, [ADR-IC-007](../docs/product-management/integration_concepts/adrs/ADR-IC-007-observability-stack.md)
Layer 1) **plus** metrics on the shared `Babelstone.Engine` meter (`LifecycleDriverMetrics.cs`, wired by
the host's `WithMetrics` through the no-PII guard):

- `lifecycle_pass_last_success_timestamp_seconds` — the **tick-liveness heartbeat** gauge, the worker
  host's health/liveness signal (no HTTP surface — freshness + `absent()` is the probe, the
  `EngineMetricsAbsent` posture);
- `lifecycle_dispatch_total` / `lifecycle_dispatch_failure_total` — POST success/failure, tagged by the
  structural `command_kind` only;
- `lifecycle_dispatch_lag_seconds` — how late after its business due date each occurrence fired;
- `lifecycle_schedule_held_total` — the **parked-settlement stall** signal (`RecordScheduleHeld`, the emit
  hook the LCD-2 settlement-health gate calls when it lands, bd `babelstone-6cpq.10`).

The alert rules live in the `lifecycle-driver` group of
[`infra/grafana/prometheus/alert-rules.yaml`](../infra/grafana/prometheus/alert-rules.yaml)
(`LifecycleDriverTickStale`, `LifecycleDriverMetricsAbsent`, `LifecycleDispatchFailuresSustained`,
`LifecycleDispatchLagP99High`, and the `LifecycleScheduleHeld` page — "alerted, not invisible",
ADR-PC-036 §Residual-risks); the on-call procedures — including the parked-settlement / stalled-schedule
entry and the ledger audit queries — are
[`infra/runbooks/lifecycle-driver-ops.md`](../infra/runbooks/lifecycle-driver-ops.md). Every recovery is
retry-safe by construction: the ledger + the engine's `command_dedup` make the failure modes stalls,
never duplicates.

> Status: **host + both family rules shipped, and the ADR-PC-038 hardening is in.** The host owns the
> clock, runs the Cadence worker, single-fires by atomic claim on the durable Postgres dispatch ledger
> (survives restarts, shared across replicas — no leader elected; LCD-4/LCD-5), and POSTs through the
> command sink with the scoped SCA principal; the term-deposit `MaturityRule` and personal-loan
> `InstallmentRule` (bd `babelstone-6cpq.8` / `.9`) read their forward calendars and contribute the due
> commands. **Not** built here: the recurring settlement-health gate
> (`LIFECYCLE_DRIVER_SETTLEMENT_HEALTH_GATE`, LCD-2, ADR-PC-036 §Decision 4)
> ([ADR-PC-036](../docs/product-management/product_concepts/adrs/ADR-PC-036-lifecycle-command-driver.md)
> §Consequences). Extraction-ready subtree per
> [ADR-PC-019 §P2](../docs/product-management/product_concepts/adrs/ADR-PC-019-repository-strategy-monorepo.md);
> placement per [ADR-IC-013](../docs/product-management/integration_concepts/adrs/ADR-IC-013-in-house-estate-build-and-repository-placement.md).
