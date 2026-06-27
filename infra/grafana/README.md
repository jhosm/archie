# Grafana LGTM — Critical SLI alert rules (K.3, bd `babelstone-gbgw`)

Alerting rules for the engine's **Critical SLIs**. The metrics these rules read
are **already emitted** by the engine over OTLP
([ADR-IC-007 §P1](../../docs/product-management/integration_concepts/adrs/ADR-IC-007-observability-stack.md))
and land in the Grafana LGTM appliance's bundled Prometheus. This subtree
materialises the **alert rules** those metrics call for — per
[ADR-IC-004 §P4](../../docs/product-management/integration_concepts/adrs/ADR-IC-004-outbox-pattern-mechanism.md)
(the outbox-lag SLI is "first-class, not an afterthought") and
[ADR-PC-005 §S2](../../docs/product-management/product_concepts/adrs/ADR-PC-005-dr-rto-rpo.md)
(the broader Critical-SLI set: saga states, compensation rate, sync-projection
p99, replication lag).

- **Build provenance:** in-house (ops config)
- **Wires into:** the `grafana-lgtm` appliance (`grafana/otel-lgtm:0.28.0`) in
  both [`infra/compose.yaml`](../compose.yaml) and
  [`infra/k8s/`](../k8s/README.md)

## Layout

```
infra/grafana/
└── prometheus/
    ├── prometheus.yaml    # config overlay: names the rule file + Alertmanager seam
    └── alert-rules.yaml   # the Critical-SLI alerting rules (Prometheus `groups:`)
```

## What is alerted (and what is not yet)

| SLI | Metric (emitted) | Rule | Threshold | ADR |
|---|---|---|---|---|
| **Outbox publish lag** | `outbox_publish_lag_seconds` | `OutboxPublishLagWarning` | > 30 s for 1 m | ADR-IC-004 §P4 |
| **Outbox publish lag** | `outbox_publish_lag_seconds` | `OutboxPublishLagCritical` | > 5 min for 1 m (publisher down / Redpanda unavailable) | ADR-IC-004 §P4 |
| **Outbox publish latency p99** | `outbox_publish_latency_seconds` (histogram) | `OutboxPublishLatencyP99High` | p99 > 5 s over 5 m | ADR-IC-004 (G.1) |
| **Inbox poison rate** | `inbox_poison_total` | `InboxPoisonRecordsAppearing` | any in 5 m | ADR-IC-004 (G.2) |
| **Projection-rebuild drill freshness** | `reconciliation_drill_last_success_timestamp_seconds` (drill-pushed gauge, or `absent`) | `ProjectionRebuildDrillStale` | >35 d or never, for 1 h | ADR-PC-005 §P5 (M.5) |

**Critical SLIs whose metric is not yet emitted** are present in
`alert-rules.yaml` as **commented, guarded** rules — each carries a
`TODO(emit-pending)` naming the pending emit and its owner, with the threshold +
severity already decided so the alert ships *with* the metric. These are the
remaining [ADR-PC-005 §S2](../../docs/product-management/product_concepts/adrs/ADR-PC-005-dr-rto-rpo.md)
Critical SLIs plus the M.5 reconciliation signals:

| SLI | Pending metric | Owner |
|---|---|---|
| Saga in `HUMAN_INTERVENTION_REQUIRED` | `saga_state{state=…}` | Epic E (saga runtime) |
| Compensation rate | `saga_compensation_total` | Epic E |
| Postgres replication lag | `pg_replication_lag_seconds` (postgres_exporter / `pg_stat_replication` scrape — not engine code) | M.4 follow-up / observability |
| Sync-projection apply p99 | `projection_apply_seconds` (histogram) | Epic F (projections) |
| Reconciliation checksum mismatch (§7.1 a) | `reconciliation_checksum_mismatch_total{consumer,projection_kind}` | Epic F / projection runtime (the reconciler host) |
| Reconciliation event-count skip (§7.1 b) | `reconciliation_event_count_skip_total{consumer,projection_kind}` | Epic F / projection runtime |
| Rebuild-drill divergence (§7.2 c) | `reconciliation_rebuild_divergence_total{projection_kind}` | the projection-rebuild-drill job (bd babelstone-j67l) |

### Projection reconciliation (M.5 — bd `babelstone-irfl`)

The `projection-reconciliation` group materialises the alerts for the three
[event-store §7.1](../../docs/product-management/product_concepts/feature-design-event-store-projections.md)
reconciliation patterns the
[`ProjectionReconciler`](../../engine/src/Babelstone.Engine/ProjectionReconciler.cs)
computes — checksum mismatch (a), event-count **skip** (b — a benign *gap* is
**not** alerted), and full-rebuild-drill divergence (c). The reconciler returns
these as *records* today, not yet a Prometheus metric, so the checksum/skip/
divergence rules are guarded with the same `TODO(emit-pending)` discipline as the
saga block; the **drill-freshness** rule (`ProjectionRebuildDrillStale`) is live,
driven by a gauge the monthly rebuild drill
([`scripts/projection-rebuild-drill.sh`](../../scripts/projection-rebuild-drill.sh),
bd `babelstone-j67l`) can push on each green run. The operator response —
thresholds, escalation, and the rebuild-is-the-repair-path procedure — is the
[reconciliation-alerts](../runbooks/reconciliation-alerts.md) and
[projection-rebuild-drill](../runbooks/projection-rebuild-drill.md) runbooks.

Keeping them visible-but-guarded (rather than omitted) makes "the SLI is named,
the metric is the gap" auditable, the
[ADR-PC-020 §P5](../../docs/product-management/product_concepts/adrs/ADR-PC-020-llm-toolchain-and-conformance-governance.md)
deliberate-visible-hole discipline.

## Metric naming (OTLP → Prometheus)

The engine emits these on the `Babelstone.Engine` meter as
snake_case-with-unit-suffix names (`BabelstoneAttributes.*Metric` in
`engine/src/Babelstone.Telemetry/`), so the OTLP-to-Prometheus translation is a
**no-op on the names**: `outbox_publish_lag_seconds` is read by exactly that
string (the ADR-IC-004 §P4 contract string). Histograms expose
`_bucket` / `_count` / `_sum`; OTLP delta counters are exposed cumulative with
the `_total` suffix that is already part of the emitted name
(`inbox_poison_total`).

Thresholds are deployment-time decisions
([ADR-IC-004 §P4](../../docs/product-management/integration_concepts/adrs/ADR-IC-004-outbox-pattern-mechanism.md):
"Alert threshold is a deployment-time decision"); the values here are the
ADR-named POC defaults (30 s / 5 min for outbox lag).

## Wiring the rules into the appliance

The `grafana/otel-lgtm` appliance bundles Prometheus. To load these rules, mount
this directory and overlay the bundled config's `rule_files`:

### Compose

Add a bind-mount to the `grafana-lgtm` service (this subtree intentionally does
not edit `compose.yaml` — it is the artefact a deploy mounts):

```yaml
# infra/compose.yaml — grafana-lgtm service
volumes:
  - ./grafana/prometheus/prometheus.yaml:/otel-lgtm/prometheus.yaml:ro
  - ./grafana/prometheus/alert-rules.yaml:/otel-lgtm/alert-rules.yaml:ro
```

### Kubernetes

The two files are mounted as a ConfigMap into the `grafana-lgtm` Deployment at
the same `/otel-lgtm/` paths. The **staging** overlay
([`infra/k8s/overlays/staging/`](../k8s/README.md)) wires this (bd
`babelstone-zla1.9`): a `configMapGenerator` builds the `grafana-lgtm-rules`
ConfigMap from these same `prometheus/{prometheus,alert-rules}.yaml` files (one
config source, the base's `otel-collector-config` / `kong-config` pattern), and
[`grafana-lgtm-rules.patch.yaml`](../k8s/overlays/staging/grafana-lgtm-rules.patch.yaml)
subPath-mounts each file so the appliance's own `/otel-lgtm/` contents aren't
clobbered. It is a **staging** concern (where the always-on box's alerts must
fire), not the kustomize **base** — the base still ships the appliance with
default config, and `dev`/`ha` don't load rules. The patch touches only the
Deployment, so the OTLP boundary (no 4317/4318 on the `grafana-lgtm` Service,
[ADR-IC-007 §P1](../../docs/product-management/integration_concepts/adrs/ADR-IC-007-observability-stack.md))
is preserved.

## Validating the rules

`alert-rules.yaml` is a standard Prometheus rules file. Validate with
`promtool`:

```bash
promtool check rules infra/grafana/prometheus/alert-rules.yaml
```

Both files are plain YAML and parse-validated in CI's infra job path (any
`infra/**` change). The rule *expressions* are PromQL; they are syntax-checked
by `promtool` where it is available (not pinned in `mise.toml` today — a
follow-up can add it to the infra gate).

## Cross-references

- [ADR-IC-004 §P4](../../docs/product-management/integration_concepts/adrs/ADR-IC-004-outbox-pattern-mechanism.md)
  — outbox-lag SLI as a first-class signal; the 30 s / 5 min thresholds.
- [ADR-IC-007 §P1](../../docs/product-management/integration_concepts/adrs/ADR-IC-007-observability-stack.md)
  — the OTLP-single-entry pipeline these metrics flow through; Grafana's
  alerting engine provides the SLO-based alerts in the same tool as the
  dashboards.
- [ADR-PC-005 §Decision / §P5](../../docs/product-management/product_concepts/adrs/ADR-PC-005-dr-rto-rpo.md)
  — the named RTO/RPO targets the broader Critical-SLI set guards (replication
  lag is a DR-degradation signal); §P5 — drills as resilience-testing evidence
  (the drill-freshness alert). (The §S2 "Ecosystem coherence" soft criterion is
  where ADR-PC-005 notes ADR-IC-007 covers replication-lag observability; the
  Critical-SLI *targets* themselves live in §Decision and §P5.)
- [06 — Observability and tracing](../../docs/product-management/integration_concepts/06-observability-and-tracing.md)
  — SLO-based alerting; the "saga in HUMAN_INTERVENTION_REQUIRED" alert.
- [event-store §7.1 / §7.2](../../docs/product-management/product_concepts/feature-design-event-store-projections.md)
  — the three reconciliation patterns and the rebuild drill the
  `projection-reconciliation` rule group alerts on.
- [reconciliation-alerts](../runbooks/reconciliation-alerts.md) /
  [projection-rebuild-drill](../runbooks/projection-rebuild-drill.md) runbooks
  — the M.5 operator response: thresholds, escalation, rebuild-is-repair (bd
  `babelstone-irfl` / `babelstone-j67l`).
