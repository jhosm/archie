# Payout-landing reconciliation alerts — ops runbook (bd `babelstone-qa92.2`)

**In plain English.** When the engine pays a customer — a deposit maturity, a loan
disbursement — that payout has to *land* on the customer's current account as a
credit. Almost always it does. The **payout-landing reconciler** is the safety net
for the rare times it doesn't: it pairs every payout the engine's source recorded
against the landing the current account recorded, and flags the mismatches — a
payout that **dropped** (paid, never landed), one that **doubled** (landed twice),
one that landed at the **wrong amount**, or a landing with no payout behind it
(an **orphan**). Until now that reconciler existed but *nothing ran it*, so its
warnings reached no one. This runbook is what an operator does when one of those
warnings finally fires: what each means, how urgent it is, and how to resolve it.

The formal frame: the reconciler is
[`PayoutLandingReconciler`](../../orchestrator/src/Babelstone.Orchestrator/PayoutLandingReconciler.cs)
(shipped in bd `98mj.7`), the engine↔engine-CA re-scoping of the
[ADR-PC-016](../../docs/product-management/product_concepts/adrs/ADR-PC-016-legacy-current-account-adapter.md)
flow-1 reconciliation pattern
([ADR-PC-043](../../docs/product-management/product_concepts/adrs/ADR-PC-043-intra-engine-settlement-counterparty.md)).
bd `qa92.2` added the part that makes it *run*: a scheduled
[Cadence](../../docs/product-management/integration_concepts/adrs/ADR-IC-019-family-agnostic-notification-platform.md)
worker in the orchestrator host
([`PayoutLandingReconciliationWorker`](../../orchestrator/src/Babelstone.Orchestrator/PayoutLandingReconciliationWorker.cs) /
[`…SchedulePass`](../../orchestrator/src/Babelstone.Orchestrator/PayoutLandingReconciliationSchedulePass.cs))
that ticks on a cadence, reads the source payouts + CA landings as-of today, calls
`PayoutLandingReconciler.Reconcile`, and surfaces every non-matched signal to an
operator sink — a Prometheus counter + a structured log
([ADR-IC-007](../../docs/product-management/integration_concepts/adrs/ADR-IC-007-observability-stack.md)
Layer 1). The metrics live in
[`PayoutReconciliationMetrics`](../../orchestrator/src/Babelstone.Orchestrator/PayoutReconciliationMetrics.cs)
on the shared `Babelstone.Engine` meter; the alert rules pair with the
`payout-landing-reconciliation` group in
[`alert-rules.yaml`](../grafana/prometheus/alert-rules.yaml).

> **Safety model (read this first).** The reconciler **raises signals; it never
> moves money** (ADR-PC-043 reconcile-signals-only). It does not re-pay a DROP, net
> out a DOUBLE, adjust a WRONG-AMOUNT, or reverse an ORPHAN — every fix below is an
> **operator/reconciliation-process action**, driven through the normal money-movement
> surfaces (an operator-initiated resolution intent, ADR-PC-043 §Idempotency), never
> by this reconciler. So nothing here can *cause* a double or wrong movement; the
> failure modes it reports are *pre-existing* discrepancies it makes visible. The
> classifier is **clock-free** (ADR-PC-023 §6): the worker owns the clock and injects
> `asOf`, so the same inputs on the same day always classify the same way — a re-run
> is idempotent (it re-emits the same signals; a counter/log is a rate an operator
> reads, not a state a re-emit corrupts).

---

## 0 · The signals at a glance

| Alert | Metric (`reconciliation_class`) | Severity | Meaning |
|---|---|---|---|
| `PayoutReconciliationDropDetected` | `payout_reconciliation_signal_total{…="drop"}` | **critical** | Source paid, nothing landed past the interim DROP SLA — a dropped payout the customer is owed. |
| `PayoutReconciliationDoubleDetected` | `…{…="double"}` | **critical** | Two or more CA landings for one source payout — the customer credited twice for one intent. |
| `PayoutReconciliationWrongAmountDetected` | `…{…="wrong_amount"}` | **critical** | One landing whose amount ≠ the source payout — the customer got the wrong sum. |
| `PayoutReconciliationOrphanLandingDetected` | `…{…="orphan_landing"}` | warning | A CA landing with no source payout — money landed the engine never sourced. |
| `PayoutReconciliationTickStale` | `payout_reconciliation_pass_last_success_timestamp_seconds` | **critical** | No reconciliation pass has completed for >2.5 poll intervals — the safety net is not running. |
| `PayoutReconciliationMetricsAbsent` | `absent(payout_reconciliation_pass_last_success_timestamp_seconds)` | warning | The reconciler reports no heartbeat at all — down, crash-looping, telemetry broken, or deliberately disabled. |

The tick-liveness heartbeat **is** the reconciler's health surface (the reconciler
is a worker with no HTTP endpoint), the same freshness + `absent()` posture as the
lifecycle driver's heartbeat and the `EngineMetricsAbsent` staging-liveness rule.
The heartbeat emits only after the **first completed pass**.

Every signal's structured log carries the **same structural fields** an escalation
should carry — the opaque `IntentId`, the `ReconciliationClass`, and the reconciler's
detail string (integer cents, dates). **No PII** (ADR-PC-004 §P2): never a depositor
name, NIF, or IBAN. See §5.

---

## 1 · The interim DROP SLA (read before triaging a DROP)

A source-paid-not-landed pair is **not immediately** a DROP — the landing may still
be in transit. It is `IN_FLIGHT` until it is older than the **DROP SLA horizon**,
then a `DROP`. The interim horizon is **`DefaultDropSlaDays = 3` days**
([`PayoutLandingReconciler.DefaultDropSlaDays`](../../orchestrator/src/Babelstone.Orchestrator/PayoutLandingReconciler.cs)).

**This value is a placeholder, not the settled SLA.** Q-AG calibration of the real
horizon is **pending** (ADR-PC-043 §Residual risks). An operator MAY override it per
deployment via `Reconciler:PayoutLanding:DropSlaDays` (thresholds are deployment-time
decisions, ADR-IC-004 §P4); leaving it unset uses the 3-day interim. When the Q-AG
value lands, set it in config — **no code change**, and update this section.

Because the age is measured against the injected `asOf` (never a wall clock inside
the classifier, ADR-PC-023 §6), a DROP that fires today would have been `IN_FLIGHT`
yesterday: the horizon is genuinely load-bearing, so do not treat an in-SLA payout as
a miss.

---

## 2 · `PayoutReconciliationDropDetected` — source paid, nothing landed

**What it means.** The reconciler found a source payout with **no** matching CA
landing, older than the DROP SLA. The credit owed the customer has not landed.

**Urgency — critical.** Funds a customer is owed are in limbo. It is *safe* (the
source holds the funds via the payout-pending marker — the reconciler never re-pays),
but a human must reconcile it.

**Triage.**
1. Read the log line's `IntentId` and detail (it names the amount, the value date, and
   the age past SLA). The intent id is `f(source_id, occurrence)` — it identifies
   *which* economic payout dropped.
2. Confirm it is genuinely dropped, not merely a slow landing that crossed the horizon:
   re-check whether a CA landing for that intent has since appeared (the next pass will
   re-classify it MATCHED if so, and the alert clears on its own).
3. Establish blast radius: one intent (a single lost credit) or many sharing a cause
   (a settlement path outage that dropped a batch — cross-check the settlement saga
   health and the CA credit-writer for the same window).

**Resolve.** Drive an **operator-initiated resolution** of the undeliverable credit
through the normal money-movement surface — a resolution intent derived from the SAME
original intent id (ADR-PC-043 §Idempotency), so a late original landing and the
resolution collapse to exactly one credit (no double-pay). **Do not** hand-post a
credit outside that surface, and **do not** ask the reconciler to fix it — it only
signals. Close the alert once the resolution lands and the next pass classifies the
intent MATCHED.

---

## 3 · `PayoutReconciliationDoubleDetected` — two landings for one payout

**What it means.** Two or more CA landings recorded for a **single** source payout —
the customer may have been credited twice for one economic intent.

**Urgency — critical.** Money the bank must claw back. The reconciler **never nets it
out** (ADR-PC-043 signal-only).

**Triage.**
1. `IntentId` + detail name the intent and the landing count. A saga *reissue*
   (byte-identical body, fresh dispatch id) is **not** a double — the reconciler
   recovers the intent from the reference the writer derived, so a reissue pairs as one
   occurrence. A genuine double is two *distinct* applied landings for one intent.
2. Establish the cause: a credit-writer idempotency gap (the intent-keyed
   `command_dedup` should have collapsed a reissue — a double means two DIFFERENT
   intents mapped to the same landing, or a writer bypassed the dedup), or an
   out-of-band manual credit.

**Resolve.** Hand the duplicate to the reconciliation process to **claw back the
extra landing** through the normal debit surface, keyed so a retry is safe. Root-cause
the writer path that let two landings through — a recurring double is an idempotency
defect, not a reconciliation event.

---

## 4 · `PayoutReconciliationWrongAmountDetected` — landing amount ≠ source

**What it means.** Exactly one CA landing for the intent, but its amount **differs**
from what the source paid — the customer got the wrong sum.

**Urgency — critical.** A customer-visible money error.

**Triage.**
1. `IntentId` + detail name **both** amounts (source cents vs landed cents). The
   in-band guard on the CA event is the first line; this is the reconciliation backstop
   that catches what slipped past it.
2. Determine the direction: landed **short** (customer under-credited — owed the
   difference) or landed **over** (over-credited — claw back the difference).

**Resolve.** Drive the correcting movement (a top-up or a claw-back for the delta)
through the normal money surface — never adjust the landing in place, and never ask the
reconciler to adjust it. Root-cause how the amount diverged (a rate/rounding bug, a
truncated body) so it does not recur.

---

## 5 · `PayoutReconciliationOrphanLandingDetected` — landing with no payout

**What it means.** A CA landing for an intent the engine **never sourced** — money
landed that has no source payout behind it.

**Urgency — warning.** It does not owe a customer money the way a DROP does; it is a
reconciliation curiosity that still needs a human. Usually a **mis-keyed reference**
(a landing whose intent reference does not resolve to a known source payout) or an
**out-of-band credit** applied directly to the CA.

**Resolve.** Trace the landing's reference to its true source (was it mis-derived?),
and if it is genuinely un-sourced, hand it to the reconciliation process to reverse or
book correctly. Signal-only — the reconciler never absorbs it silently, which is the
whole point of surfacing it.

---

## 6 · `PayoutReconciliationTickStale` / `PayoutReconciliationMetricsAbsent` — the net is down

**What it means.** The reconciler has stopped completing passes (`TickStale`, >2.5 poll
intervals) or is reporting no heartbeat at all (`MetricsAbsent`). **A silent reconciler
is exactly how a DROP goes unnoticed** — this is the highest-leverage alert in the set,
because it means every other signal here is blind.

**Do:**
1. Check the orchestrator host is running and not crash-looping (container restarts,
   exit logs).
2. Read its logs for the Cadence backoff warning (`Cadence schedule pass failed;
   backing off …`) — the inner exception names the dead dependency (the source read
   surface: the movement ledger / CA landing read model).
3. Confirm the reconciler is even **meant** to run on this stack: it starts only when
   `Reconciler:PayoutLanding:Enabled=true` **and** an `IPayoutLandingSource` is wired
   (the live movement-ledger + CA-landing read — a human bring-up step). On a stack
   that deliberately runs no reconciler, silence `PayoutReconciliationMetricsAbsent` at
   the Alertmanager route rather than leaving it firing.
4. Once the dependency recovers, the loop resumes on its own (Cadence backs off and
   retries); a re-run over the same world re-derives the same signals, so **no missed
   discrepancy is lost** — a payout that dropped while the reconciler was down is still
   a DROP on the next pass.

---

## 7 · No-PII discipline (do not break it while triaging)

Every payout-reconciliation signal carries only **structural references** — the opaque
`IntentId` (`f(source_id, occurrence)`), the `ReconciliationClass`, and the detail
string (integer cents, dates)
([ADR-PC-004 §P2](../../docs/product-management/product_concepts/adrs/ADR-PC-004-pii-crypto-shredding.md)
/ the no-PII-on-the-durable-bus rule; the `reconciliation_class` metric label is the
only dimension, admitted by the runtime no-PII guard). When you escalate or attach
evidence to a ticket, carry the **same** references — an intent id and a class, never a
depositor name, NIF, or IBAN. Resist "making it readable" by resolving the subject;
that is exactly the boundary the design keeps closed.

---

## 8 · Cross-references

- [`PayoutLandingReconciler.cs`](../../orchestrator/src/Babelstone.Orchestrator/PayoutLandingReconciler.cs)
  — the classifier (read-only reference; clock-free, signal-only).
- [`PayoutLandingReconciliationSchedulePass.cs`](../../orchestrator/src/Babelstone.Orchestrator/PayoutLandingReconciliationSchedulePass.cs)
  / [`…Worker.cs`](../../orchestrator/src/Babelstone.Orchestrator/PayoutLandingReconciliationWorker.cs)
  — the scheduled driver that runs it (bd `qa92.2`).
- [`PayoutReconciliationMetrics.cs`](../../orchestrator/src/Babelstone.Orchestrator/PayoutReconciliationMetrics.cs)
  — the operator metric surface the alert group reads.
- [`alert-rules.yaml`](../grafana/prometheus/alert-rules.yaml) — the
  `payout-landing-reconciliation` rule group these procedures pair with.
- [reconciliation-alerts runbook](./reconciliation-alerts.md) — the sibling
  *projection*-reconciliation runbook (event-store §7.1 drift, a different reconciler).
- [lifecycle-driver runbook](./lifecycle-driver-ops.md) — the sibling clock-owning
  worker whose tick-liveness posture this reconciler mirrors.
- [ADR-PC-043](../../docs/product-management/product_concepts/adrs/ADR-PC-043-intra-engine-settlement-counterparty.md)
  — the intra-engine settlement-counterparty decision (reconcile-signals-only; the
  interim DROP SLA and its pending Q-AG calibration).
- [ADR-PC-023](../../docs/product-management/product_concepts/adrs/ADR-PC-023-temporal-signals-projection-derived.md)
  — clock-free classifier boundary; the downstream consumer owns the read cadence.
- [ADR-IC-019](../../docs/product-management/integration_concepts/adrs/ADR-IC-019-family-agnostic-notification-platform.md)
  — the shared Cadence poll-loop machinery the scheduled reconciler reuses.
- [ADR-IC-007](../../docs/product-management/integration_concepts/adrs/ADR-IC-007-observability-stack.md)
  — the Layer-1 operator surface (Prometheus counter + structured log) the signals ride.
