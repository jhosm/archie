# Grafana per-persona dashboards (K.4, bd `babelstone-vk3x`)

**In plain English:** three ready-made Grafana dashboards, one for each kind of
person who looks at the system — the operator watching for trouble, the
compliance officer who needs an audit trail, and the developer debugging a
failure. They are committed as code (JSON + a provisioning file) so they appear
automatically when the stack comes up, instead of being clicked together by hand
and lost on the next reset. Each one shows the metrics and traces the engine
**already emits**, and they all let you paste a `correlation_id` to jump between
the metric, its trace, and its logs — the "paste an id, see everything" workflow
[Document 06](../../../docs/product-management/integration_concepts/06-observability-and-tracing.md)
asks for.

These dashboards visualise signals that exist today; they add **no**
instrumentation. They are the dashboard half of
[ADR-IC-007 §P6](../../../docs/product-management/integration_concepts/adrs/ADR-IC-007-observability-stack.md)
("multi-persona dashboards … provisioned as code"), the sibling of the K.3 alert
rules under [`../prometheus/`](../prometheus/).

- **Build provenance:** in-house (ops config — dashboards-as-code)
- **Wires into:** the `grafana-lgtm` appliance (`grafana/otel-lgtm:0.28.0`) in
  [`infra/compose.yaml`](../../compose.yaml) and [`infra/k8s/`](../../k8s/README.md)

## The three personas (ADR-IC-007 §P6)

| Dashboard (`uid`) | Persona / role | What it shows | Cross-signal navigation it is allowed |
|---|---|---|---|
| `babelstone-operator-noc` (`operator-noc.json`) | Operator / **NOC** (`noc-viewer`) | Operational SLIs only: outbox publish lag, publish-latency p99, inbox delivery health, saga dispatch outcomes. No financial/personal attributes. | Exemplars on the latency panel → Tempo trace (for an operator with deeper access); NOC itself is metrics-only by role. |
| `babelstone-compliance` (`compliance.json`) | **Compliance** (`compliance-viewer`) | Business view: constitution volume, saga delivered/refused, and the `tier=business` Loki audit stream. No Tempo trace access by design. | `correlation_id` → `tier=business` logs (the PSD2 audit trail, ADR-IC-007 §P5). |
| `babelstone-developer` (`developer.json`) | **Developer** (`engineer`) | Full deep-dive: `correlation_id` TraceQL trace search, all-tier logs, DB query-latency histogram, outbox/inbox internals. | Full metric → trace → log: exemplars, traces-to-logs, all signals (ADR-IC-007 §P6 engineer). |

The role → dashboard-folder access mapping itself (the RBAC roles named above) is
owned by a **separate** lane, [`infra/grafana/rbac/`](../) (bd `njt2.4`, PR #229),
and is intentionally kept file-disjoint from this subtree to avoid a merge
conflict. This lane only *names* the `Babelstone` Grafana folder its provider
provisions into; the RBAC lane scopes who may open it.

## Metrics & spans these read (all already emitted)

| Signal | Name on the wire | Source |
|---|---|---|
| Outbox publish lag (SLI) | `outbox_publish_lag_seconds` | engine outbox relay (`OutboxLagObserver`), ADR-IC-004 §P4 |
| Outbox publish latency (histogram) | `outbox_publish_latency_seconds_*` | engine outbox relay (`OutboxDrainer`), ADR-IC-004 G.1 |
| Inbox delivery counters | `inbox_handled_total` / `inbox_duplicates_total` / `inbox_poison_total` / `inbox_tombstone_total` | engine + orchestrator inbox pump, G.2 |
| Saga command dispatch | `saga_dispatch_delivered_total` / `saga_dispatch_refused_total` | orchestrator `SagaCommandDispatchDrainer` |
| DB query latency (histogram) | `db_client_operation_duration_seconds_*` | Npgsql OTel instrumentation, K.5 |
| `correlation_id` on every span | `babelstone.saga.correlation_id` (→ TraceQL `.babelstone.saga.correlation_id`) | every manual span, ADR-IC-007 §P3 |
| Saga-advance spans | `saga.advance` (+ `deposit.constituted`, `accrual.computed`, `withholding.applied`) | engine / orchestrator manual spans, ADR-IC-007 §P2 |

> Metric names are read by exactly these strings. OTLP→Prometheus translates dots
> in instrument/attribute names to underscores (`saga.dispatch.delivered` →
> `saga_dispatch_delivered_total`; `babelstone.aggregate_type` →
> `babelstone_aggregate_type`) and adds `_bucket`/`_count`/`_sum` to histograms and
> `_total` to monotonic counters — the same no-op-on-the-name convention the K.3
> rules rely on. `service.name` is exposed as the `service_name` label (the
> dashboards' `$service` variable).

## Cross-signal correlation (how the navigation works)

The `grafana/otel-lgtm` appliance **pre-wires** the three-way correlation
ADR-IC-007 §S2 relies on — these dashboards just use it:

- **metric → trace:** the Prometheus datasource has
  `exemplarTraceIdDestinations → trace_id → Tempo`. Every histogram panel here sets
  `"exemplar": true`, so the exemplar dots are clickable jumps to the trace.
- **trace → log:** the Tempo datasource has `tracesToLogsV2 → Loki` keyed on
  `trace_id`. Open a span and "Logs for this span" lands in Loki.
- **`correlation_id` entry point:** the compliance and developer dashboards carry a
  `correlation_id` textbox variable. Paste the originating id and the trace-search
  / log panels scope to that one request — the Document 06 "paste a `correlation_id`,
  see everything" path.

## Wiring into the appliance

Add two bind-mounts to the `grafana-lgtm` service (this subtree intentionally does
**not** edit `compose.yaml` — it is the artefact a deploy mounts, mirroring how
[`../prometheus/`](../prometheus/) ships the K.3 rules unmounted):

### Compose

```yaml
# infra/compose.yaml — grafana-lgtm service
volumes:
  - ./grafana/dashboards/provisioning/babelstone-personas.yaml:/otel-lgtm/grafana/conf/provisioning/dashboards/babelstone-personas.yaml:ro
  - ./grafana/dashboards/json:/var/lib/grafana/dashboards/babelstone:ro
```

The appliance reads dashboard providers from
`conf/provisioning/dashboards/*.yaml` relative to its Grafana home
(`/otel-lgtm/grafana`); the provider's `options.path`
(`/var/lib/grafana/dashboards/babelstone`) is where the JSON is mounted. Grafana
loads them on startup and keeps watching the directory.

### Kubernetes

Mount `babelstone-personas.yaml` as a ConfigMap at
`…/conf/provisioning/dashboards/` and the three JSON files as a ConfigMap at the
provider's `options.path` in the `grafana-lgtm` Deployment. Kept out of the
kustomize base in this lane (the base ships the appliance with default config;
dashboard provisioning is an additive overlay a deploy applies) — the same posture
[`../prometheus/`](../prometheus/) takes with the K.3 rules.

## Validating

The JSON is parse-validated (and the provisioning YAML well-formed-checked) in
CI's infra path (any `infra/**` change). To sanity-check the appliance actually
loads them locally:

```bash
# Boot the appliance with the dashboards + provider mounted
docker run -d --name lgtm-verify -p 3000:3000 \
  -v "$PWD/infra/grafana/dashboards/provisioning/babelstone-personas.yaml:/otel-lgtm/grafana/conf/provisioning/dashboards/babelstone-personas.yaml:ro" \
  -v "$PWD/infra/grafana/dashboards/json:/var/lib/grafana/dashboards/babelstone:ro" \
  grafana/otel-lgtm:0.28.0

# All three dashboards should be listed (tag: babelstone)
curl -s -u admin:admin 'http://localhost:3000/api/search?tag=babelstone' | jq '.[].uid'
#   "babelstone-operator-noc"
#   "babelstone-compliance"
#   "babelstone-developer"
```

(They show empty until the stack emits signal — they read live engine
metrics/traces. Run `make demo` for an engine + orchestrator wired to the
collector, then the panels populate.)

## Cross-references

- [ADR-IC-007 §P2/§P3/§P6](../../../docs/product-management/integration_concepts/adrs/ADR-IC-007-observability-stack.md)
  — span-attribute contract, the identity trio on every span, the persona/RBAC role model.
- [ADR-IC-004 §P4](../../../docs/product-management/integration_concepts/adrs/ADR-IC-004-outbox-pattern-mechanism.md)
  — the outbox-lag SLI and its thresholds (mirrored by the K.3 alert rules).
- [Document 06 — Observability and tracing](../../../docs/product-management/integration_concepts/06-observability-and-tracing.md)
  — the persona dashboards and the "paste a `correlation_id`, see everything" scenario.
- [`../prometheus/`](../prometheus/) — the K.3 Critical-SLI alert rules (the alerting
  sibling of these dashboards).
