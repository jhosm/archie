# Term Deposit System — Integration Architecture
## Document 06: Observability and Distributed Tracing

---

## The Concrete Problem

Imagine you get a ticket: *"Client João Silva tried to constitute a deposit yesterday at 14:32 and never received confirmation. The money has left his account."*

Without adequate observability, here's what you have to do:

- Search the Deposits API logs for something related to the client (probably thousands of lines)
- Try to guess the corresponding `deposit_id` or `process_id`
- Jump to the orchestrator's logs, hoping it has the same ID in some form
- Search the ACL logs — maybe. If they exist. If they're retained long enough.
- Try to correlate with Core logs, which has its own format and own IDs
- Call Compliance and ask them to verify if they received anything
- Call the operations team to verify the state of the account in the Core
- 4 hours later, you still don't know where the saga stopped

With adequate observability:

- You paste the `correlation_id` into the dashboard, or search by client name/ID
- You see the complete trace: 47 operations across 8 systems, with timings, statuses, relevant payloads
- You identify in 30 seconds: "saga stopped at the `ConfirmDebit` step, ACL is in `INDETERMINATE` state, the clearance job did not run due to error X"
- You resolve in minutes, not hours

The difference is radical, and the only way to get there is to design observability **from day 1**, not add it later.

---

## The Three Pillars

Modern observability rests on three signal types, complementary:

| Pillar | What it captures | When to use it |
|---|---|---|
| **Logs** | Discrete events with rich context | Deep debugging, audit trail, forensics |
| **Metrics** | Aggregated time series | Dashboards, alerting, SLO tracking |
| **Traces** | Causality between distributed operations | Reconstructing cross-system flows, latency analysis |

For your system, **traces are the dominant pillar**. Distributed sagas are literally the canonical use case for distributed tracing.

---

## Distributed Tracing — What It Is, Concretely

A *trace* represents a complete business operation from start to finish, crossing all involved systems. It is composed of *spans*, and each span represents a unit of work in a specific system.

Visually, a trace of the constitution looks like this (approximate representation):

```
TRACE: corr-aB7xK2pQ9 (duration: 850ms)
│
├─ [API Gateway] HTTP POST /deposits/constitute          [150ms]
│  ├─ [Auth] Validate token                              [10ms]
│  ├─ [Idempotency] Check key                            [5ms]
│  ├─ [Deposits API] Handle command                      [120ms]
│  │  ├─ [DB] BEGIN TRANSACTION
│  │  ├─ [Aggregate] Create ConstitutionProcess          [20ms]
│  │  ├─ [Aggregate] Create Deposit (DRAFT)              [15ms]
│  │  ├─ [Outbox] INSERT ConstitutionRequested           [8ms]
│  │  └─ [DB] COMMIT                                     [12ms]
│  └─ Response 202                                       [5ms]
│
├─ [Outbox Publisher] Publish ConstitutionRequested      [50ms]
│  └─ [Kafka] Produce to topic                           [25ms]
│
├─ [Orchestrator] Consume ConstitutionRequested          [600ms]
│  ├─ [Inbox] Dedup check                                [3ms]
│  ├─ Parallel validations                               [220ms]
│  │  ├─ [Compliance Adapter] Validate eligibility       [90ms]
│  │  │  └─ [Compliance API] POST /eligibility           [78ms]
│  │  ├─ [ACL Core] Reserve balance                      [180ms]
│  │  │  ├─ [Idempotency] Check                          [4ms]
│  │  │  ├─ [Core SOAP] HoldsService.create              [165ms]  ← slow!
│  │  │  └─ [State Store] Persist mapping                [8ms]
│  │  └─ [Validator] Product limits                      [15ms]
│  ├─ [Orchestrator] Transition to APPROVED              [20ms]
│  ├─ [ACL Core] Confirm debit                           [195ms]
│  │  └─ [Core SOAP] HoldsService.confirm                [180ms]
│  ├─ [Compliance Adapter] Confirm registration          [85ms]
│  ├─ [Aggregate Deposit] Activate                       [45ms]
│  │  └─ [Outbox] INSERT DepositConstituted
│  └─ [Outbox Publisher] Publish DepositConstituted      [30ms]
│
└─ [Async fan-out — separate traces or linked spans]
   ├─ [Projector client_deposits] Update
   ├─ [Notifications Adapter] Send
   ├─ [Documentation Adapter] Generate FIN
   └─ ...
```

This is **not a slide diagram**. It's a real view in tools like Jaeger, Tempo, Honeycomb, Datadog APM. Given a `correlation_id`, you literally see this, with real times, and you can click on each span to see details.

The view points directly to the bottleneck (Core SOAP at 165ms+180ms = 345ms right there), the critical path, and where to investigate.

---

## OpenTelemetry — the Standard to Adopt

In greenfield, **OpenTelemetry (OTel)** is the obvious choice. Reasons:

1. **Vendor-neutral.** You generate data in a standard format, choose (and switch) the backend later. Today self-hosted Jaeger, tomorrow Honeycomb, then Datadog — without refactor.
2. **Broad coverage.** Mature SDKs for Java, .NET, Python, Go, Node. Auto-instrumentation for common frameworks (Spring, FastAPI, Express).
3. **The three pillars unified.** Traces, metrics, logs in the same model, with automatic correlation.
4. **Huge community.** Documentation, integrations, examples.

Recommendation: **OpenTelemetry SDK in each service + OTel Collector** as an export layer. Choose the backend according to budget and team.

---

## The Mechanism: Context Propagation

The magic of distributed tracing depends on one thing: **context propagation between systems**. Each call (HTTP, Kafka, any transport) carries special headers with `trace_id` and `parent_span_id`. The next service detects them, creates its spans as children, and propagates forward.

Modern standard: **W3C Trace Context** (`traceparent`, `tracestate` headers). Natively supported by OTel.

For your system:

| Transport | How to propagate |
|---|---|
| HTTP between services | Headers `traceparent` automatically (OTel HTTP instrumentation) |
| Kafka | Message headers with `traceparent` (OTel Kafka instrumentation) |
| To the Core (SOAP/MQ) | Manual: the ACL adds to the SOAP envelope or MQ property |
| To systems that don't understand trace context | At least propagates `correlation_id` in a reference field |

**Notice this is exactly Primitive 4 (Identity) materialized.** The `correlation_id` we defined isn't just logging — it's the axis around which all observability organizes. If Primitive 4 is well implemented, distributed tracing **emerges naturally**. If it's poorly implemented, everything else collapses.

---

## Manual vs Automatic Spans

OTel does a lot on its own: every HTTP call, every SQL query, every Kafka produce/consume creates a span automatically. But **the most valuable spans are manual ones**, capturing **domain semantics**:

```
tracer.startActiveSpan("aggregate.deposit.activate", span -> {
  span.setAttribute("deposit.id", deposit.getId());
  span.setAttribute("deposit.amount", deposit.getAmount());
  span.setAttribute("deposit.product", deposit.getProductCode());
  span.setAttribute("process.id", processId);
  
  try {
    deposit.activate(...);
    span.setStatus(StatusCode.OK);
  } catch (DepositCannotBeActivatedException e) {
    span.setStatus(StatusCode.ERROR, e.getMessage());
    span.recordException(e);
    throw e;
  }
});
```

These manual spans transform a technical trace ("HTTP, SQL, Kafka") into a **business** trace ("constituted, validated eligibility, reserved balance, activated"). When someone looks at the trace, they read a comprehensible story, not a technical-detail soup.

### Essential Manual Spans for Your System

- Each saga state transition (`process.transition: STARTED → PARALLEL_VALIDATION`)
- Each domain operation (`aggregate.deposit.{activate,cancel,mobilize}`)
- Each Core call via ACL (with ID mappings as attributes)
- Each compensation (`saga.compensation: release_balance_reservation`)
- Each inbox check / outbox publish

---

## Semantic Attributes

Apply discipline on attributes. Convention I recommend:

```
deposit.id, deposit.client_id, deposit.amount, deposit.product
process.id, process.state, process.type
saga.phase, saga.compensation
core.txn_id, core.account, core.operation
compliance.registration_id, compliance.hold_id
event.type, event.version, event.message_id
inbox.deduplicated (boolean), outbox.published_at
```

These attributes enable **queries**:

- "Show me all traces of constitutions for product TD-TRAD-12M that failed yesterday"
- "Show me all traces where ACL Core had latency >300ms"
- "Show me all processes that entered compensation"

Without disciplined attributes, you have data without query value.

---

## Logs — Structured, Not Free Text

Logs remain critical, but they must be **structured** (JSON) and **correlated with traces**.

Each log line automatically includes:

```json
{
  "timestamp": "2026-05-15T14:32:17.342Z",
  "level": "INFO",
  "service": "deposits-orchestrator",
  "trace_id": "8a7b3c...",
  "span_id": "f4e5d6...",
  "correlation_id": "corr-aB7xK2pQ9",
  "message": "Saga transitioned to APPROVED",
  "process_id": "PROC-2026-00098765",
  "deposit_id": "DEP-2026-00012345"
}
```

`trace_id` and `span_id` injected automatically by OTel logging integration. Result: given a trace, you navigate to the corresponding logs in one click (and vice-versa).

**Absolute rule**: never, ever, logs without `correlation_id`. In production, that's the single field that will make the difference between 5-minute debugging and 5-hour debugging.

### Level Discipline

- `ERROR`: something went wrong and needs human attention (unexpected exception, saga in terminal failure state)
- `WARN`: something went wrong but the system recovered (retry succeeded, compensation executed)
- `INFO`: significant business events (saga started, saga completed, important transition)
- `DEBUG`: technical detail for troubleshooting (only in dev, or temporarily activatable in production)

Don't log PII at INFO/WARN/ERROR levels. NIB, name, email — only in DEBUG or redacted.

---

## Metrics — What to Monitor

Metrics are where you define SLOs and where alerts live. For your system, I would recommend these groups:

### Technical Metrics (RED — Rate, Errors, Duration)

- Requests/sec per endpoint
- Error rate per endpoint
- Latency percentiles (p50, p95, p99, p99.9) per endpoint
- Same for event handlers: events/sec, error rate, processing duration

### Critical Infrastructure Metrics

- Outbox lag (age of the oldest `PENDING`, by table)
- Inbox dedup rate (% of duplicate messages detected — anomalous spike suggests problem)
- Kafka consumer lag (by consumer group)
- Projector lag (delta between event emitted and read model updated)
- Saga state distribution (how many processes in each state)
- ACL state (how many operations `IN_FLIGHT`, `INDETERMINATE`)

### Business Metrics (essential for detecting "silent" problems)

- Constitutions started/hour
- Constitutions successfully completed/hour
- Constitutions cancelled/hour (by reason)
- Average end-to-end saga time (constitution, mobilization)
- % of sagas that entered compensation
- % of operations in `INDETERMINATE` in the ACL

**Why business metrics are vital:** sudden drop in "constitutions completed" rate can mean a bug that doesn't throw technical errors — just sagas that never finish. Without this metric, you discover days later.

---

## Alerts — Discipline Is Everything

A poorly calibrated alert is worse than no alert: generates noise, normalizes failure, trains the team to ignore. Principles:

1. **Alert only on symptoms, not on causes.** "Latency p99 above 1s for 5 min" is a symptom. "CPU at 80%" is a cause — someone sees it on the dashboard when investigating.
2. **Anything that fires out of hours must be actionable.** If someone receives an alert at 3am, they have to know **exactly** what to do.
3. **SLO-based alerting.** Define clear SLOs (e.g., "99.5% of constitutions complete in <2s"), alert when the error budget is running out, not on every individual failure.
4. **Differentiate warning from critical.** Warning goes to Slack/email, critical wakes someone up.

### Suggested Critical Alerts

- Outbox lag >5 minutes (events aren't being published)
- Kafka consumer lag growing monotonically
- ACL operations in `INDETERMINATE` >N
- Saga state `HUMAN_INTERVENTION_REQUIRED` (any occurrence)
- Error rate >1% on critical endpoints
- Sudden drop in business operation rate (compared to baseline)

---

## Dashboards — Designed by Persona

Different people need different views. I would recommend at least three:

### Operations Dashboard (NOC, on-call)

- Global system health (uptime, error rates)
- Outbox lags, consumer lags, inbox dedup rates
- Operations in anomalous states (`INDETERMINATE`, `HUMAN_INTERVENTION`)
- Active alerts
- Latency percentiles of critical endpoints

### Business Dashboard (product, compliance, management)

- Operational volumes (constitutions, mobilizations, maturities per hour/day)
- Success rates per operation type
- Distribution by product, amount, term
- Compensations executed (reasons)
- Average processing times

### Per Bounded Context Dashboard (each team)

- Metrics of their service/context
- Events published and consumed
- Latency and error rate of own endpoints
- Health of integrators (ACL, adapters)

---

## Saga State View — Specific to Event-Driven

Something I explicitly recommend building: **a view of the state of all sagas in progress**. Not an aggregated dashboard — an operational view where someone from ops can search by client, process_id, correlation_id, and see:

- Current saga state
- Transition history with timestamps
- Executed or pending compensations
- Errors encountered
- Links to corresponding traces and logs
- In cases of `HUMAN_INTERVENTION_REQUIRED`: available actions (retry, cancel, force compensation)

This view is the **operations console** of the system. In event-driven systems, it's as important as the main application.

---

## The Rule That Unites Everything: Design for Debuggability From Day One

In event-driven systems with sagas, the first time you are in production and something goes wrong, you will see whether observability was treated as a first-class citizen or as an afterthought. **The difference is existential**: without adequate observability, you lose trust in the system, and in banking that's the fast track to never running a new deploy again.

Concretely, what this means in practice:

- All observability infrastructure **before** the first business feature. Not "first we make it work, then we instrument".
- Code review **rejects** code without spans, without structured logs, without attributes. Non-negotiable.
- Each new feature includes dashboard updates and corresponding alerts. Treated as part of the definition of done.
- Regular game days: simulate failures (Core unavailable, slow Kafka, bug in projector) and train the team to use observability to diagnose. Without this, no one knows how to use the tools when it counts.

---

## Closing

This document covers the first of the four transversal topics. The following are:

- **Testing strategy** — how you test with confidence a system that has local ACID + eventual consistency + sagas + compensations
- **Event catalog governance** — who approves a new integration event, who owns it, naming conventions, deprecation policy
- **Long-term schema evolution** — migrating consumers between major versions, dual-write during transitions, sunsetting old versions

Observability has natural links to testing: well-built observability facilitates testing (test traces, integration test debugging), and well-built testing instruments observability (validating that the right spans and metrics are generated).
