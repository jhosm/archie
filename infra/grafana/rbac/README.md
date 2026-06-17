# Grafana RBAC + access logging — the observability-plane access control (M.3, bd `babelstone-njt2.4`)

Role-scoped access to the Grafana LGTM plane, plus access logging of trace queries that carry
financial attributes. The trace/log store is a **searchable database of every financial
operation** ([doc 10 Boundary 7](../../../docs/product-management/integration_concepts/10-security-and-threat-model.md#boundary-7-observability-backend--all-system-data) /
[Principle 4](../../../docs/product-management/integration_concepts/10-security-and-threat-model.md#principle-4-the-observability-plane-is-a-regulated-data-store)),
so it is **not open to all engineers** and every access to a financially-attributed trace is
itself logged. This subtree is the **declarative, provisioned-as-code** realisation of
[ADR-IC-007 §P6](../../../docs/product-management/integration_concepts/adrs/ADR-IC-007-observability-stack.md)
and plane (iii) of [ADR-IC-016 §7](../../../docs/product-management/integration_concepts/adrs/ADR-IC-016-service-identity-and-mtls.md).

- **Build provenance:** in-house (ops config)
- **Wires into:** the `grafana-lgtm` appliance (`grafana/otel-lgtm:0.28.0`) in both
  [`infra/compose.yaml`](../../compose.yaml) and [`infra/k8s/`](../../k8s/README.md)
- **Reserved Test ID:** `OBS_PLANE_RBAC` (catalogue row SEC-2)

## Layout

```
infra/grafana/rbac/
├── grafana.ini                          # config overlay: RBAC on + access logging on
└── provisioning/
    ├── roles.yaml                       # the §P6 four roles + their permissions
    ├── datasource-permissions.yaml      # Tempo restricted to engineer + admin (the key lock)
    └── teams.yaml                        # teams ↔ role binding (membership is deployment data)
```

## The §P6 role configuration (the minimum, provisioned before any financial span is visible)

| Role | Can query | Tempo (traces)? |
|---|---|---|
| `babelstone:noc-viewer` | Operational metrics dashboards + alert state | **No** |
| `babelstone:engineer` | All signals — full trace, log, metric query | Yes |
| `babelstone:compliance-viewer` | Business metrics dashboard + Loki `tier=business` logs | **No** |
| `babelstone:admin` | Full access including Grafana configuration | Yes |

The load-bearing restriction is the **Tempo datasource lock** (`datasource-permissions.yaml`):
the trace datasource carries the **financial-restricted attribute tier** (`deposit.amount`,
`deposit.product`, `core.txn_id` — [ADR-IC-007 §P4](../../../docs/product-management/integration_concepts/adrs/ADR-IC-007-observability-stack.md)),
so only `engineer` + `admin` may query it. NOC and compliance are absent from the Tempo grant ⇒
no trace access at all — exactly doc 10 Boundary 7. Compliance reaches the business log stream
(`{tier="business"}` in LogQL) and the pre-built business dashboards, never ad-hoc trace
exploration.

## Access logging — who read a financially-attributed trace, and when

`grafana.ini` turns on Grafana's request + **dataproxy** logging (`[dataproxy] logging = true`),
which records every outbound datasource query — including a Tempo trace read — with the acting
user. Those structured (JSON) log lines are scraped by the OTel Collector into Loki, where the
who-queried-what trail is queryable and retained. This satisfies the doc 10 Boundary 7 / PSD2
requirement: *access to traces carrying financial attributes is itself logged.*

## OSS reality and the Enterprise / gateway fallback (honest scope)

[ADR-IC-007 §P6 baseline (2026-05-17)](../../../docs/product-management/integration_concepts/adrs/ADR-IC-007-observability-stack.md):
OSS Grafana ships **basic RBAC (Viewer/Editor/Admin org roles), datasource permissions, and
dashboards-as-code provisioning** — which is what the **Tempo datasource lock** above relies on,
and it is enforceable on OSS. Grafana **Enterprise** adds fine-grained per-folder RBAC with
team-based assignment and **native audit logs** of dashboard/query access. Where the per-folder
split or a tamper-evident audit log exceeds the OSS tier, the ADR names the fallback explicitly:
*"adopt Enterprise (which re-opens F1) or front Grafana with an external auth gateway that
enforces dashboard-level ACLs upstream of the application."* This subtree provisions the
OSS-enforceable controls (org-role split + Tempo datasource lock + dataproxy access logging); the
gateway/Enterprise upgrade is the documented production hardening when the role split needs
folder granularity. The bundled `otel-lgtm` appliance runs OSS Grafana, so this is the active
posture for the self-hosted POC.

## How to apply (additive overlay — not mounted by default)

Like [`infra/grafana/prometheus/`](../prometheus/README.md), this is an **additive overlay** a
deploy mounts; the dev `make up` does **not** mount it by default, to keep boot simple. A
deployment mounts it **before** the first service emits a financial span ([ADR-IC-007 §P6](../../../docs/product-management/integration_concepts/adrs/ADR-IC-007-observability-stack.md):
"must be in place before the first service emits spans carrying financial attributes"):

```yaml
# infra/compose.yaml, under the grafana-lgtm service:
volumes:
  - ./grafana/rbac/grafana.ini:/otel-lgtm/grafana/conf/grafana.ini:ro
  - ./grafana/rbac/provisioning:/otel-lgtm/grafana/conf/provisioning/access-control:ro
```

On Kubernetes the same files ride as a `ConfigMap` mounted at the appliance's Grafana config +
provisioning paths (mirroring the Prometheus-rules overlay pattern in [`infra/k8s/`](../../k8s/README.md)).
