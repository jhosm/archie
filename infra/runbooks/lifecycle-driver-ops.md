# Lifecycle driver — ops runbook (bd `babelstone-1nkm.4`)

**In plain English.** The lifecycle driver is the always-on service that *moves money on a
schedule*: it owns the clock the engine deliberately lacks, ticks on a cadence, reads the
forward calendars, and fires each due deposit maturity and loan installment at the engine
exactly once. Its worst failures are **silent by construction** — a wedged tick loop just
stops firing (no request ever arrives to error), and a settlement parked for human
intervention quietly stalls a loan's whole schedule with **no arrears state anywhere** to
make the miss visible. This runbook is what an operator does when one of the driver's
alarms goes off: what each one means, how urgent it is, and how to fix it.

The formal frame: the host is
[ADR-PC-036](../../docs/product-management/product_concepts/adrs/ADR-PC-036-lifecycle-command-driver.md)
(the clock-owning lifecycle-command driver), hardened per
[ADR-PC-038](../../docs/product-management/product_concepts/adrs/ADR-PC-038-lifecycle-driver-leader-election-and-durable-ledger.md)
(single-firing by atomic claim on the durable Postgres `lifecycle_dispatch_ledger`; no
elected leader). The metrics live in
[`LifecycleDriverMetrics`](../../lifecycle-driver/src/Babelstone.Lifecycle/LifecycleDriverMetrics.cs)
on the shared `Babelstone.Engine` meter; the alert rules pair with the `lifecycle-driver`
group in [`alert-rules.yaml`](../grafana/prometheus/alert-rules.yaml).

> **Safety model (read this first).** Nothing in this runbook can cause a double money
> movement. Every dispatch is keyed on a canonical, server-derived, **number-pinned**
> idempotency key, deduped twice: front-line by the durable dispatch ledger (a restart or a
> second replica re-POSTs nothing), and authoritatively by the engine's `command_dedup`
> (ADR-PC-029 — a redundant POST replays the original outcome, one money leg). Every
> recovery action below is therefore *safe to retry*: the failure modes are **stalls**,
> never duplicates. Missed occurrences **backfill automatically** once the driver recovers
> — the forward calendar keeps surfacing an occurrence until the engine event that
> satisfies it lands, and the engine stamps the correct business date (`valid_time` rides
> the command), so even a late firing records the right business day.

---

## 0 · The signals at a glance

| Alert | Metric | Severity | Meaning |
|---|---|---|---|
| `LifecycleDriverTickStale` | `lifecycle_pass_last_success_timestamp_seconds` | **critical** | No schedule pass has completed for >2.5 poll intervals — due money movement is not happening. |
| `LifecycleDriverMetricsAbsent` | `absent(lifecycle_pass_last_success_timestamp_seconds)` | warning | The driver reports no heartbeat at all — down, crash-looping before its first tick, or telemetry broken. |
| `LifecycleDispatchFailuresSustained` | `lifecycle_dispatch_failure_total` | **critical** | The driver ticks but its engine POSTs keep failing — occurrences claim, fail, release, retry. |
| `LifecycleDispatchLagP99High` | `lifecycle_dispatch_lag_seconds` | warning | Occurrences are firing long after their business due date (outage backfill or a persistently failing subset). |
| `LifecycleScheduleHeld` | `lifecycle_schedule_held_total` | **critical** | A recurring schedule is stalled behind a settlement parked in `HUMAN_INTERVENTION_REQUIRED` — the silent stall, paged. |

The tick-liveness heartbeat **is** the host's health surface: the driver is a worker host
with no HTTP endpoint, so liveness is read from the metric plane (freshness + `absent()`),
the same posture as the `EngineMetricsAbsent` staging-liveness rule. The heartbeat emits
only after the **first completed pass**.

## 1 · `LifecycleDriverTickStale` — the loop is not ticking

**Means:** the clock-owning poll loop has not run a pass to completion in >2.5 poll
intervals. Either the process is wedged/crash-looping, or every pass is failing and the
Cadence worker is stuck at its exponential-backoff ceiling (5 min) against a dead
dependency.

**Do:**
1. Check the host is running and not crash-looping (container restarts, exit logs).
2. Read its logs for the Cadence backoff warning (`Cadence schedule pass failed; backing
   off …`) — the inner exception names the dead dependency:
   - **engine command surface** unreachable → check the engine host / `Engine:BaseUrl`;
   - **read-model Postgres** (the calendars the rules scan) → check
     `Engine:ReadModelConnectionString`;
   - **ledger Postgres** (`lifecycle_dispatch_ledger`) → check
     `Lifecycle:LedgerConnectionString`; a boot-time migration failure also lands here.
3. Restart/fix the dependency; the loop recovers on its next tick. **No manual replay is
   needed**: the next pass re-derives every still-due occurrence and fires the missed ones
   (deduped by ledger + `command_dedup`).

**Multi-replica note:** replicas are competing consumers — there is no leader whose death
stalls the fleet. This alert firing means **no** replica is completing passes; a single
dead replica with healthy siblings never trips it.

## 2 · `LifecycleDriverMetricsAbsent` — no heartbeat at all

**Means:** the heartbeat series does not exist. On a stack that deploys the driver: it is
down, has never completed a pass since deploy (check the fail-loud boot — a missing
`Lifecycle:LedgerConnectionString` / read-model / engine URL refuses to start), or the OTLP
export path is broken. On a stack that deliberately runs no driver, silence the rule at the
Alertmanager route.

**Do:** confirm the deployment exists and its boot log got past configuration resolution
and the ledger migration; then check the collector path (the engine's own metrics arriving
is a quick control).

## 3 · `LifecycleDispatchFailuresSustained` — ticking, but POSTs failing

**Means:** passes run, claims are won, but the engine POST keeps throwing (non-2xx /
timeout). Each failure is individually safe — the claim releases un-recorded and the next
pass retries — but a sustained rate means due money movement is stalled.

**Do:**
1. Read the sink's warning logs (`Lifecycle command POST … returned {Status}`):
   - **5xx / timeout** → engine or its store is unhealthy; fix there.
   - **422 SCA_REQUIRED** on money-mover routes → the scoped service-principal
     (`X-SCA-Service-Principal`, `lifecycle:deposit-money-mover`) is not being attested by
     the gateway / not allowed by IAM — a configuration regression (ADR-PC-036 §Decision 1).
   - **404** → route drift between a family rule's `RequestPath` and the engine API.
2. After the fix, do nothing else: the still-due occurrences retry on the next tick.

## 4 · `LifecycleDispatchLagP99High` — firing very late

**Means:** dispatches are landing more than a day after their business due date — the tail
of a long outage backfill (expected: it drains and the alert clears) or a subset of
occurrences that repeatedly fails (check alert 3). Business dates remain correct
(bitemporal `valid_time` rides the command), so this is operational, not a correctness
incident.

## 5 · `LifecycleScheduleHeld` — the parked-settlement stall (the page that exists because nothing else can see it)

**Means:** the settlement-health gate (ADR-PC-036 §Decision 4, `LIFECYCLE_DRIVER_SETTLEMENT_HEALTH_GATE`/LCD-2)
held recurring occurrence N+1 because occurrence N's de-settled cash leg is parked in
`HUMAN_INTERVENTION_REQUIRED`. **This stall is invisible everywhere else**: the engine
advanced the paid-count on the event (not on settled cash), there is no arrears state, and
the driver is *correctly* refusing to outrun uncollected money. The schedule will not
advance until a human resolves the parked settlement.

**Do:**
1. Find the parked settlement saga(s) in the orchestrator store:
   ```sql
   SELECT process_id, saga_type, state, updated_at
   FROM saga_state
   WHERE state = 'HUMAN_INTERVENTION_REQUIRED';
   ```
2. Cross-reference what the driver last fired for the affected instance — the ledger is the
   audit trail:
   ```sql
   SELECT command_kind, occurrence_key, due_at, status, dispatched_at
   FROM lifecycle_dispatch_ledger
   WHERE instance_id = '<loan/deposit id>'
   ORDER BY occurrence_key;
   ```
3. Resolve the settlement with the Core (the ADR-IC-003 §P4 clearance path — never a blind
   retry of an irreversible leg).
4. Once the leg leaves the parked state, the gate stops holding N+1 and the schedule
   resumes on the next tick — no manual re-fire.

**Emit status:** the metric hook (`LifecycleDriverMetrics.RecordScheduleHeld`) ships with
this monitoring surface; the gate that calls it is the LCD-2 build (bd `babelstone-6cpq.10`).
Until that lands the series is absent and the rule is dormant by construction — the alert
ships *ready*, not commented out.

## 6 · Useful audit queries (the durable ledger)

"What did the driver dispatch today, and how late?"

```sql
SELECT command_kind, count(*) AS fired,
       max(dispatched_at - (due_at::timestamptz)) AS worst_lag
FROM lifecycle_dispatch_ledger
WHERE status = 'DISPATCHED' AND dispatched_at >= date_trunc('day', now())
GROUP BY command_kind;
```

"Anything seen due but never successfully fired?" (PENDING rows older than a few poll
intervals pair with alerts 1/3 — a claim keeps failing or nothing is ticking):

```sql
SELECT instance_id, command_kind, occurrence_key, due_at, first_seen_at
FROM lifecycle_dispatch_ledger
WHERE status = 'PENDING'
ORDER BY first_seen_at;
```

Rows are structural references only (ids, kind codes, ordinals, dates) — the ledger is
PII-free by design (ADR-PC-004 §P2), so these queries are safe to run and share in an
incident channel.
