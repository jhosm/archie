# Telemetry Guidelines

**In plain English:** every span, log line, and metric this system emits lands in one shared,
searchable, cross-service store that an operator uses to debug a live banking incident at 2am —
and because it aggregates data from every service, that store is *regulated*: it must never
become a searchable index of customers' personal data. This guideline is how we write telemetry
that earns its place there. Instrument the operations an operator would actually need to see;
carry only structural identifiers, never a customer's NIF/IBAN/name/e-mail; and emit through the
one shared, versioned contract that already lives in code so a dashboard query never breaks under
you. The *why* behind all of this is
[Document 06](../integration_concepts/06-observability-and-tracing.md) and
[ADR-IC-007](../integration_concepts/adrs/ADR-IC-007-observability-stack.md); this page is the
day-to-day "what do I do when I add code" companion.

This is a standalone reference (the `implementation_guidelines/` series is not sequenced). It
governs the telemetry emitted from `engine/`, `families/`, `orchestrator/`, `acl/`, and
`notification/` (C#) and `mcp-server/` (Python). It does **not** re-decide the observability
*stack* (that is [ADR-IC-007](../integration_concepts/adrs/ADR-IC-007-observability-stack.md)) or
the conceptual *why* (that is Document 06) — it tells you how to write a signal that conforms to
both.

Unlike the [code-comment guideline](./code-comments.md), most of this **is** mechanically gated.
The `babelstone.*` attribute contract, the no-PII rule, the resource stamp, and the
emit-in-the-shell rule are each backed by a fitness function in the
[commitment catalogue](../product_concepts/adrs/commitment-catalogue.md) (`OBS-1`…`OBS-6`) and, for
PII, a runtime emit-time guard plus a build-time analyser. So a violation usually fails CI rather
than merely reads badly. The judgement this guideline still asks *you* for is the part no gate
covers: **is this the right thing to instrument, named the right way, at the right level.**

## The litmus

One question decides whether a signal is worth emitting and safe to emit:

> **At 2am, would an operator need this to diagnose a failure — and does every field carry only a
> structural identifier, never a value that identifies a person?**

If it would not help diagnose anything, it is noise in a regulated store — don't emit it. If any
field carries a NIF, IBAN, account number, customer name, or e-mail, it is a personal-data leak
into a queryable cross-service index — and the runtime guard will drop or fail it anyway. Every
rule below is this litmus applied to a specific case.

## The rules

### 1. Emit through the shared contract, never ad-hoc strings

There is exactly one `ActivitySource` and one `Meter` for the estate —
`BabelstoneTelemetry.ActivitySource` / `BabelstoneTelemetry.Meter` (both named `Babelstone.Engine`,
the OTel instrumentation scope). Open spans and create instruments on those, so a host turns
your signal on with one `AddSource` / `AddMeter`. Span attribute keys come from
`BabelstoneAttributes`; structured-log `EventId`s come from `BabelstoneEvents`. Do not hand-write a
tag key or an event id at a call site — add a named constant to the contract and reference it.

### 2. Names are wire contracts — add-and-deprecate, never rename

A `babelstone.*` span key, a snake-case metric name (`outbox_publish_lag_seconds`), and an
`EventId` number are each read by a Grafana panel or an alert rule *by their exact string or
number*. Renaming one silently breaks the query that depends on it. Treat the contract as
append-only: to change a signal, add the new key/metric/id and deprecate the old — never rename or
renumber in place. The two naming registers are not interchangeable — span attributes are
dotted `babelstone.*`; metric names are snake_case with a unit suffix (`_seconds`, `_total`) and
carry no prefix, because they follow the Prometheus convention the alert rules read.

### 3. No PII in any signal — this is the load-bearing rule

Span tags, structured-log fields, and metric dimensions carry only the admitted operational tier:
structural identifiers (a partition key, a product code, a saga process id, an event type). Never a
NIF, IBAN, account number, name, or e-mail — at *any* log level, including `DEBUG`
(`OBS_NO_PII_ATTRS`, ADR-IC-007, ADR-PC-004). Two consequences you apply directly:

- **Money rides as integer cents**, under the `babelstone.*_cents` keys (`InterestCents`,
  `TaxCents`) — never a formatted decimal, matching the engine's cents-native discipline.
- **A customer reference is a pseudonym, not an id.** When a span must point at a customer, carry
  `babelstone.subject_pseudonym`, derived with `ClientPseudonym.Of(clientId, salt)` (a salted
  one-way HMAC, ADR-IC-016 plane iii) — never the raw `client_id`, which keys into the Customer
  Data Store and is PII.

This is enforced at emit by the runtime guard (`AddBabelstonePiiGuard` in
`Babelstone.Telemetry.Hosting`, across traces/logs/metrics) — the load-bearing leg, since every
real attribute is runtime-valued — with the `BENG005` analyser as a build-time backstop for a
literal call-site leak. Don't rely on the gate as your design: choose non-PII fields *first*.

### 4. Emit in the impure shell, never in the pure decider or fold

Product-semantic spans and metrics are opened in the runtime shell —
`AggregateRuntime.AppendAsync`'s span hook, a host endpoint, the outbox/inbox pump — **never** in a
pure decider, a fold, or replayed state (`OBS-2`; ADR-IC-010). Emitting a signal from the pure core
would make replay non-deterministic (a metric read during a fold changes nothing the fold produces,
but a span started there rides the replay path) and trips the determinism commitments
(`NO_CLOCK_DRIVEN_ENGINE_SIGNAL`, `DETERMINISM_GATE`). Instrumentation observes the engine; it is
never part of what an event folds to.

### 5. Manual spans tell a business story, named `<entity>.<operation>`

Auto-instrumentation already gives you the HTTP/SQL/Kafka spans. The manual spans you add exist to
turn a technical trace into a *business* one — `deposit.constituted`, `accrual.computed`,
`withholding.applied`, `saga.advance`. Name them `<entity>.<operation>` (ADR-IC-007), open them in
the shell (rule 4), and tag them with the structural attributes that make them *queryable*
(partition key, product code, saga transition) — an attribute nobody would filter or group by is
not worth adding.

### 6. Logs are structured, correlated, and level-disciplined

Every host log is structured (not free text) and carries `correlation_id` — the single field that
turns 5-hour debugging into 5-minute debugging (Document 06). `trace_id`/`span_id` are injected by
the OTel logging integration. Respect the levels: `ERROR` = needs a human; `WARN` = recovered
(retry/compensation succeeded); `INFO` = significant business event; `DEBUG` = troubleshooting
detail. And — restating rule 3 because it is the one people forget at `DEBUG` — no PII at any level.

### 7. Propagate `traceparent` across every boundary

A trace is only useful if it survives the hop. W3C `traceparent` propagates across every process
boundary — HTTP headers, and the durable bus as an envelope/outbox header (`OBS-4`) — so one
`correlation_id` resolves a complete cross-service trace. When you add a new transport or a new
boundary, carry the context across it; a signal that starts a fresh root at each service is a trace
that tells no story.

## Citation discipline

The [code-comment guideline](./code-comments.md) governs how you *cite* these decisions in the code
you write. In short, and applied to telemetry:

- **Cite the commitment name — it is the strongest anchor.** `OBS_NO_PII_ATTRS`,
  `OBS_SPAN_PRODUCT_SEMANTICS`, `NO_CLOCK_DRIVEN_ENGINE_SIGNAL` are each backed by a fitness
  function, so if your comment's claim goes false, CI fails. Pair the commitment with the bare ADR
  id (`ADR-IC-007 / OBS_NO_PII_ATTRS`): the ADR is the rationale half, the commitment is the
  testable half.
- **A prose ADR reference stops at the bare id** — `ADR-IC-007`, not `ADR-IC-007 §P4`. Section
  numbers shift when an ADR is amended; the bare id survives.
- **Don't restate the contract in a comment.** The key's `<summary>` in `BabelstoneAttributes`
  already owns "this is operational-tier, never PII, never renamed." Point at it; don't paraphrase
  it into a second source that can drift.

## When not to instrument

- The auto-instrumentation already captures it (a bare HTTP/SQL span with no domain semantics to
  add — rule 5).
- The attribute is not something anyone would query, filter, or group by — it is payload, not a
  signal.
- The value can only be expressed by leaking PII — redesign around a structural identifier or a
  pseudonym instead (rule 3), or don't emit it.
- It would fire from the pure core (rule 4) — move it to the shell or drop it.

## Relationship to the governance model

This guideline is the telemetry counterpart of the repo's explicit-drift posture
([ADR-PC-020](../product_concepts/adrs/ADR-PC-020-llm-toolchain-and-conformance-governance.md)):
divergence is allowed, *silent* divergence is not. Most of what this page asks for is protected by
a fitness function or a runtime guard, so a signal that violates it fails CI rather than drifting
quietly — the same guarantee the `babelstone.*` contract's `<summary>` comments carry into the
generated API reference. What is left to judgement — *is this worth instrumenting, named right, at
the right level* — is what reviewers and the litmus are for.
