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
  rules, derive each due occurrence's number-pinned dispatch id, **check** the dispatch ledger (skip a
  re-tick), **POST** through the sink, and — only on success — **record** the dispatch. Check-then-POST-then-
  record so a transient engine outage never strands a due command (the next pass retries; the engine
  dedupes).
- `ILifecycleCommandRule.cs` — the **family-contribution port** (`ILifecycleCommandRule` +
  `LifecycleCommandDecision` + `DispatchedCommand` + `ILifecycleCommandSink`), the write-side mirror of the
  notification core's `INotificationScheduleRule`. A family rule reads its own forward calendar and says
  which occurrences are due; it never reimplements the idempotency.
- `LifecycleDispatchLedger.cs` — the "already fired this occurrence" memory that makes a re-tick a no-op,
  keyed on the canonical, **server-derived, number-pinned** dispatch id
  (`LifecycleCommandKey.Derive(instance_id, command_kind, stable_occurrence_key)` — referenced from the
  engine hosting seam, **not** reinvented; LCD-1,
  [ADR-PC-036](../docs/product-management/product_concepts/adrs/ADR-PC-036-lifecycle-command-driver.md)
  §Decision 1+3). In-memory v1; a durable, crash-surviving ledger is a later operating concern (the engine's
  `command_dedup` is the authoritative idempotency backstop regardless).
- `HttpLifecycleCommandSink.cs` — the production `ILifecycleCommandSink`: POSTs the engine's
  [ADR-PC-029](../docs/product-management/product_concepts/adrs/ADR-PC-029-engine-command-ingress.md)
  command endpoint, presenting the canonical key as the `Idempotency-Key` header and the **scoped,
  non-interactive SCA service principal** (`X-SCA-Service-Principal`) on a money-mover route. A non-success
  engine response is backpressure (it throws). This is the **only** runtime path the driver takes to the
  engine.

**`src/Babelstone.Lifecycle.Host/`** — the runnable **composition-root exe**
(`Microsoft.NET.Sdk.Worker`). `Program.cs` resolves the engine command endpoint (`Engine:BaseUrl`, a
service endpoint not a credential), registers the typed command-POST `HttpClient`, `TimeProvider.System`,
the dispatch ledger and the cadence knobs, registers the per-tick pass over the family rules, and runs the
clock-owning worker. Family `ILifecycleCommandRule` contributions plug in here with zero core diff.

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

> Status: **host + both family rules shipped.** The host owns the clock, runs the Cadence worker, dedupes on
> the number-pinned dispatch id, and POSTs through the command sink with the scoped SCA principal; the
> term-deposit `MaturityRule` and personal-loan `InstallmentRule` (bd `babelstone-6cpq.8` / `.9`) read their
> forward calendars and contribute the due commands. **Not** built here: the recurring settlement-health gate
> (`LIFECYCLE_DRIVER_SETTLEMENT_HEALTH_GATE`, LCD-2, ADR-PC-036 §Decision 4), and the operating-concern
> hardening the host owns as it matures — single-firing/leader-election, a durable dispatch ledger, and
> monitoring
> ([ADR-PC-036](../docs/product-management/product_concepts/adrs/ADR-PC-036-lifecycle-command-driver.md)
> §Consequences). Extraction-ready subtree per
> [ADR-PC-019 §P2](../docs/product-management/product_concepts/adrs/ADR-PC-019-repository-strategy-monorepo.md);
> placement per [ADR-IC-013](../docs/product-management/integration_concepts/adrs/ADR-IC-013-in-house-estate-build-and-repository-placement.md).
