# Deployed environment (Kubernetes)

Kustomize manifests for the deployed **backing-infra** stack, per
[ADR-IC-013 §D2](../../docs/product-management/integration_concepts/adrs/ADR-IC-013-in-house-estate-build-and-repository-placement.md)
(IaC subtree co-located in the monorepo). These deploy the **same 9 services**
as [`infra/compose.yaml`](../compose.yaml) to a Kubernetes cluster, shaped for a
single **dev / staging** environment.

Packaging is **Kustomize** (base + overlays). This is a packaging choice, not an
ADR-level decision — it is recorded here and in the introducing PR body rather
than as an ADR reversal
([ADR-PC-020 §D3](../../docs/product-management/product_concepts/adrs/ADR-PC-020-llm-toolchain-and-conformance-governance.md):
no silent contradiction; this contradicts nothing).

- **Build provenance:** in-house (IaC)
- **Layout governed by:** [ADR-PC-019 §P1](../../docs/product-management/product_concepts/adrs/ADR-PC-019-repository-strategy-monorepo.md)

---

## Scope

**Backing infrastructure only** — the same services as the Compose stack. No
application/engine service images (they connect to this stack).

| Service | Kind | Port(s) (in-cluster) | ADR |
|---|---|---|---|
| postgres | StatefulSet + PVC | 5432 | [ADR-PC-001](../../docs/product-management/product_concepts/adrs/ADR-PC-001-event-store-technology.md) |
| redpanda | StatefulSet + PVC | 9092, 19092, 8081, 18081, 9644 | [ADR-IC-001](../../docs/product-management/integration_concepts/adrs/ADR-IC-001-event-backbone-message-broker.md) |
| redpanda-console | Deployment | 8080 | dev convenience |
| kong | Deployment | 8000, 8001 | [ADR-IC-006](../../docs/product-management/integration_concepts/adrs/ADR-IC-006-edge-api-gateway.md) |
| openbao | Deployment | 8200 | [ADR-PC-004](../../docs/product-management/product_concepts/adrs/ADR-PC-004-pii-crypto-shredding.md) |
| grafana-lgtm | Deployment | **3000 only** | [ADR-IC-007](../../docs/product-management/integration_concepts/adrs/ADR-IC-007-observability-stack.md) |
| otel-collector | Deployment | 4317, 4318, 13133 | [ADR-IC-007](../../docs/product-management/integration_concepts/adrs/ADR-IC-007-observability-stack.md) |
| registry | StatefulSet + PVC | 5000 | [ADR-PC-007](../../docs/product-management/product_concepts/adrs/ADR-PC-007-signed-yaml-oci-pack.md) |
| eventcatalog | Deployment | 80 | [ADR-IC-008](../../docs/product-management/integration_concepts/adrs/ADR-IC-008-event-catalog-governance-tooling.md) |

All Services are `ClusterIP` (dev: reach them via `kubectl port-forward`).
Ingress/gateway exposure beyond Kong is out of scope.

## Layout

```
infra/k8s/
├── base/                       # environment-agnostic resources
│   ├── kustomization.yaml      # namespace, resources, configMapGenerator
│   ├── namespace.yaml          # babelstone-dev
│   ├── <service>.yaml          # one file per backing service
│   └── secrets.example.yaml    # DEV-ONLY placeholder Secret (see below)
└── overlays/
    └── dev/
        └── kustomization.yaml  # single env; replicas=1 (no HA)
```

Render the manifests:

```bash
mise exec -- kustomize build --load-restrictor=LoadRestrictionsNone infra/k8s/overlays/dev
```

Validate them (this is the CI gate):

```bash
mise exec -- kustomize build --load-restrictor=LoadRestrictionsNone infra/k8s/overlays/dev \
  | mise exec -- kubeconform -strict -summary -kubernetes-version 1.31.0
```

`kustomize` and `kubeconform` are pinned in [`mise.toml`](../../mise.toml)
(single source of truth — CI reads the same pins).

> **`--load-restrictor=LoadRestrictionsNone`** is required because the base
> `configMapGenerator` reads `../../kong/kong.yml` and `../../otel/collector.yaml`
> — files *above* the kustomization root. That is deliberate: it keeps one
> config source for both the Compose and K8s stacks (see below). Kustomize's
> default restrictor forbids reaching outside the root, so this flag relaxes it.

## Namespace

Everything lands in the **`babelstone-dev`** namespace (set by both
`base/kustomization.yaml` and the `overlays/dev` overlay).

## Configuration — single source of truth

The Kong and OTel Collector configs are **not duplicated**. The base
`configMapGenerator` builds them straight from the same files the Compose stack
mounts:

- `kong-config` ConfigMap ← [`../kong/kong.yml`](../kong/kong.yml) (key
  `kong.yml`, mounted at `/kong/kong.yml`, `KONG_DECLARATIVE_CONFIG`)
- `otel-collector-config` ConfigMap ← [`../otel/collector.yaml`](../otel/collector.yaml)
  (key `collector.yaml`, mounted at `/etc/otel/collector.yaml`)

Editing `infra/kong/kong.yml` or `infra/otel/collector.yaml` updates both the
Compose stack and these manifests.

## Secret seam (DEV-ONLY → M.2)

`base/secrets.example.yaml` is a **placeholder** `Secret`
(`babelstone-dev-secrets`) carrying the trivial Compose dev defaults
(`POSTGRES_PASSWORD`, `OPENBAO_DEV_TOKEN`). It exists only so `kustomize build`
resolves the `secretKeyRef` wiring.

- **NEVER commit real credentials.** Swap this for an uncommitted Secret (or a
  platform-injected secret) in any real deployment.
- **OpenBao runs in `-dev` mode** here — in-memory, auto-unsealed, fixed root
  token. This is a **seam, not real provisioning**. The Deployment carries the
  `babelstone.io/secret-boundary` annotation marking it as such.
- **Real OpenBao provisioning is M.2** (babelstone-puu3): real storage,
  auto-unseal, policy/auth setup, HA/DR
  ([ADR-PC-005](../../docs/product-management/product_concepts/adrs/ADR-PC-005-dr-rto-rpo.md)).
  This subtree only reserves the seam.

## Observability boundary (ADR-IC-007 §P1)

Only the **OTel Collector** exposes OTLP (`4317`/`4318`). The Grafana LGTM
Service exposes **only the Grafana UI (3000)** — its own OTLP ports are *not*
published; telemetry reaches it via the Collector fan-out (`grafana-lgtm:4317`),
cluster-internally. CI asserts the grafana-lgtm Service never exposes 4317/4318.

## Out of scope (downstream)

This is a single, non-HA, dev/staging-shaped environment. Explicitly deferred:

- **HA topology — P.7** (babelstone-ixkp): 3-node Redpanda, Postgres HA, warm
  standby. Will live in a separate overlay; the base/dev split here is the seam
  it diverges from.
- **CD / promotion pipeline — Q.6** (babelstone-4c81): how rendered manifests
  get applied and promoted across environments. CI here only *validates*
  (`kustomize build` + `kubeconform`); it does not deploy.
- **Real OpenBao provisioning — M.2** (babelstone-puu3): see the secret seam
  above.
- **Application / engine service images**: out of scope here, exactly as in the
  Compose stack (backing infra only).
