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

## Enforced end-to-end in CI (`scripts/grafana-rbac-check.sh`)

The access controls above are not just declared — they are **proven against a real Grafana** on
every `infra/**` change. [`scripts/grafana-rbac-check.sh`](../../../scripts/grafana-rbac-check.sh)
(run by the CI `infra` job and locally via `make grafana-rbac-check`) stands up the pinned
`grafana/otel-lgtm:0.28.0` appliance with this subtree's `grafana.ini` as the config overlay,
then asserts the **end-to-end enforcement** that flips catalogue row **SEC-2** (`OBS_PLANE_RBAC`)
from `Planned` to `Live`:

- an **anonymous** Tempo query is **refused** (`401`) — the plane is not world-readable;
- a **NOC-class token** without the `datasources:query` privilege is **refused** the Tempo trace
  read (`403`, *"Permissions needed: datasources:query"*) while **engineer/admin** tokens
  **succeed** (`200`);
- the authorised engineer/admin read is **recorded** in the Grafana dataproxy access log
  (`logger=data-proxy-log`, `datasource=tempo`, with the acting `username`), and the refused NOC
  read is **not** (it is denied before the proxy) — exactly *who read a financially-attributed
  trace, and when*.

Static assertions in the same script pin that `grafana.ini` / `roles.yaml` /
`datasource-permissions.yaml` still express the §P6 role split + Tempo lock, so a future edit
cannot silently drop one ([ADR-PC-020 §D3](../../../docs/product-management/product_concepts/adrs/ADR-PC-020-llm-toolchain-and-conformance-governance.md):
no silent divergence). Run the static block alone (no Docker) with
`GRAFANA_RBAC_CHECK_STATIC_ONLY=1 ./scripts/grafana-rbac-check.sh`.

**What the live leg asserts is the OSS-enforceable `datasources:query` *action-level* gate**, not
the per-datasource Tempo lock — see the next section for why, and which hardening is out of OSS
scope.

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

**Precisely what OSS enforces (verified live by `grafana-rbac-check.sh`).** On the OSS tier the
`custom roles` in `roles.yaml` and the managed `datasourcePermissions` in
`datasource-permissions.yaml` are an **Enterprise** feature — the OSS fine-grained-RBAC API
(`/api/access-control/roles`) returns `404`, and the OSS basic `Viewer` role grants
`datasources:query` on **every** datasource. So the *faithful* "noc-viewer may query Prometheus
but not Tempo" split is **not** OSS-enforceable; that per-datasource granularity is precisely the
Enterprise / upstream-gateway hardening named above. What the live CI gate enforces instead is the
**OSS-enforceable `datasources:query` *action-level* gate**: a token **without** that privilege is
refused trace reads (`403`), one **with** it succeeds (`200`), anonymous access is refused, and the
authorised read is logged. The gate models the NOC posture with a no-`datasources:query` token,
which is the honest OSS realisation of doc 10 Boundary 7 until the Enterprise/gateway split lands.

## How to apply (additive overlay — not mounted by default)

Like [`infra/grafana/prometheus/`](../prometheus/README.md), this is an **additive overlay** a
deploy mounts; the dev `make up` does **not** mount it by default, to keep boot simple. A
deployment mounts it **before** the first service emits a financial span ([ADR-IC-007 §P6](../../../docs/product-management/integration_concepts/adrs/ADR-IC-007-observability-stack.md):
"must be in place before the first service emits spans carrying financial attributes"):

```yaml
# infra/compose.yaml, under the grafana-lgtm service:
volumes:
  # The otel-lgtm appliance resolves Grafana's override config at conf/custom.ini (its run dir,
  # /otel-lgtm/grafana, is the config homepath), so the overlay rides there — NOT conf/grafana.ini.
  - ./grafana/rbac/grafana.ini:/otel-lgtm/grafana/conf/custom.ini:ro
  - ./grafana/rbac/provisioning:/otel-lgtm/grafana/conf/provisioning/access-control:ro
environment:
  # The appliance force-defaults anonymous-Admin ON (an env var that wins over the .ini) for
  # zero-config local use; set this to honour grafana.ini's [auth.anonymous] enabled = false —
  # the regulated-store posture. (On Grafana Enterprise/standalone the .ini alone suffices.)
  - GF_AUTH_ANONYMOUS_ENABLED=false
```

On Kubernetes the same files ride as a `ConfigMap` mounted at the appliance's Grafana config +
provisioning paths (mirroring the Prometheus-rules overlay pattern in [`infra/k8s/`](../../k8s/README.md)).
