# Deployed environment (Kubernetes)

Kustomize manifests for the deployed **backing-infra** stack, per
[ADR-IC-013 §D2](../../docs/product-management/integration_concepts/adrs/ADR-IC-013-in-house-estate-build-and-repository-placement.md)
(IaC subtree co-located in the monorepo). These deploy the **same 10 services**
as [`infra/compose.yaml`](../compose.yaml) to a Kubernetes cluster. **`base` is the
single-replica, non-HA rendering** (the dev-shaped seam); the **ha** and **staging**
overlays diverge from it (the always-on **staging** box is its own `overlays/staging`).

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
| redpanda | StatefulSet + PVC | 9092, 19092, 8081, 18081, 8082, 18082, 9644 | [ADR-IC-001](../../docs/product-management/integration_concepts/adrs/ADR-IC-001-event-backbone-message-broker.md) |
| redpanda-console | Deployment | 8080 | dev convenience |
| kong | Deployment | 8000, 8001 | [ADR-IC-006](../../docs/product-management/integration_concepts/adrs/ADR-IC-006-edge-api-gateway.md) |
| openbao | Deployment | 8200 | [ADR-PC-004](../../docs/product-management/product_concepts/adrs/ADR-PC-004-pii-crypto-shredding.md) |
| grafana-lgtm | Deployment | **3000 only** | [ADR-IC-007](../../docs/product-management/integration_concepts/adrs/ADR-IC-007-observability-stack.md) |
| otel-collector | Deployment | 4317, 4318, 13133 | [ADR-IC-007](../../docs/product-management/integration_concepts/adrs/ADR-IC-007-observability-stack.md) |
| registry | StatefulSet + PVC | 5000 | [ADR-PC-007](../../docs/product-management/product_concepts/adrs/ADR-PC-007-signed-yaml-oci-pack.md) |
| backstage (catalogue portal) | Deployment | 7007 | [ADR-IC-015](../../docs/product-management/integration_concepts/adrs/ADR-IC-015-event-catalog-governance-tooling-backstage.md) (supersedes the retired ADR-IC-008) — renders `catalog-info.yaml` from the baked image; in-memory SQLite (no Postgres), rebuilt from the baked `/catalog` on boot (bd babelstone-zla1.6.6) |
| core-acl-stub (v1 Core-ACL settlement stub) | Deployment | 8080 | [ADR-PC-016](../../docs/product-management/product_concepts/adrs/ADR-PC-016-legacy-current-account-adapter.md) / [ADR-PC-029](../../docs/product-management/product_concepts/adrs/ADR-PC-029-engine-command-ingress.md) — WireMock; real ACL is DEF-1 (bd babelstone-ub9s) |

All Services are `ClusterIP` — in the `base` and `ha` renderings they are
reached via `kubectl port-forward`. **The `staging` overlay is the one exception**
([see below](#staging-overlay--the-always-on-public-demo-box-bd-babelstone-zla1)):
it adds a public Traefik `Ingress` + cert-manager/Let's Encrypt TLS fronting the
Kong edge, the Backstage portal, the Mission Control demo UI, the Logto OAuth/OIDC
auth subdomain, the Logto **admin console** on its own `auth-admin` host, and — since
bd zla1.10.1/zla1.10.6 — the **Grafana** observability UI at `grafana.babelstone.dev`
([ADR-IC-021](../../docs/product-management/integration_concepts/adrs/ADR-IC-021-iam-oauth-authorization-server.md))
— the always-on demo box. The IAM admin console and the Grafana observability plane are
each auth-gated (Logto login/2FA; Grafana login + Logto SSO + §P6 RBAC, anonymous OFF) and
are meant to sit behind a Cloudflare Access identity gate (bd zla1.10.6). That is a deliberate,
recorded extension of the previous "no ingress/gateway exposure beyond Kong"
posture: an
[ADR-PC-020 §D3](../../docs/product-management/product_concepts/adrs/ADR-PC-020-llm-toolchain-and-conformance-governance.md)
explicit-drift event, acknowledged here and in the introducing PR body. Kong stays
the [ADR-IC-006](../../docs/product-management/integration_concepts/adrs/ADR-IC-006-edge-api-gateway.md)
authz edge — now *behind* Traefik — and no OTLP port (4317/4318) is ever exposed
([ADR-IC-007](../../docs/product-management/integration_concepts/adrs/ADR-IC-007-observability-stack.md)
§P1). **That drift now NARROWS** (bd babelstone-zla1.10.8.3): the Mission Control demo UI is no
longer an *unauthenticated* public surface — staging runs it with `MC_AUTH_MODE=oidc`, a Logto
OIDC login gate ([ADR-IC-021](../../docs/product-management/integration_concepts/adrs/ADR-IC-021-iam-oauth-authorization-server.md)
Boundary-1 owned channel), so `app.babelstone.dev` is gated-by-default. This owned-channel gate is
enforced in the Mission Control BFF (`serve.py`) itself, **not** Kong's OIDC/JWT plugin as ADR-IC-021
step-2's prose literally reads — because this host is Traefik-fronted and same-origin-proxies its
backends rather than being a Kong route — while the pinned PKCE (C2) / SCA (C4) commitments still
hold. The `X-Client-Id` on the proxied `/api/v1/*` calls is **no longer forged/static** (bd
babelstone-zla1.10.8.4): in oidc mode the BFF attests it from the validated OIDC session `sub`,
mirroring Kong's own attest-from-`sub` algorithm — real attestation, not a stand-in. The residual
is that this demo host still bypasses Kong's edge (no rate-limiting, request validation, or SCA
step-up on that path); that Kong-bypass is now RECORDED via the
[ADR-IC-006](../../docs/product-management/integration_concepts/adrs/ADR-IC-006-edge-api-gateway.md)
2026-07-08 amendment and tracked for full Kong-fronted conformance as epic bd babelstone-zla1.10.9.

## Layout

```
infra/k8s/
├── base/                       # environment-agnostic resources
│   ├── kustomization.yaml      # namespace, resources, configMapGenerator
│   ├── namespace.yaml          # babelstone-dev
│   ├── <service>.yaml          # one file per backing service
│   └── secrets.example.yaml    # DEV-ONLY placeholder Secret (see below)
└── overlays/                   # base itself is the single-replica, non-HA rendering
    ├── ha/                     # production-shaped HA topology (P.7 — see below)
    │   ├── kustomization.yaml
    │   ├── redpanda-ha.yaml          # 1->3 node seed-discovered cluster
    │   ├── redpanda-headless-svc.yaml# per-pod DNS for the quorum
    │   ├── postgres-primary-ha.yaml  # synchronous-replication primary
    │   ├── postgres-headless-svc.yaml# per-pod DNS for the primary (replication host)
    │   ├── postgres-write-svc-ha.yaml# narrows the `postgres` write Service to the primary
    │   ├── postgres-standby-ha.yaml  # off-site warm standby (RPO ~ 0)
    │   ├── ha-secrets.example.yaml   # DEV-ONLY replication credential
    │   ├── postgres-pitr-resources.yaml  # M.4 PITR base-backup CronJob + cipher Secret
    │   ├── postgres-pitr-pgbackrest.yaml # M.4 patch: WAL archiving + pgBackRest sidecar on the primary
    │   ├── openbao-dr-ha.yaml        # M.4 OpenBao key-store DR snapshot seam
    │   └── files/                    # primary replication-setup shell hook + pg_hba.conf
    └── staging/                # always-on public demo box (single-node; see below)
        ├── kustomization.yaml        # ns babelstone-staging; replicas=1; storage + edge patches
        ├── ingress.yaml              # public Traefik Ingress (Kong edge + Backstage + Mission Control + Logto), cert-manager TLS
        ├── logto.yaml                # Logto OAuth/OIDC AS (ADR-IC-021): Service + Deployment — the auth.babelstone.dev issuer
        ├── logto-jobs.yaml           # Logto DB lifecycle: init/seed Job + alteration-deploy upgrade Job + annual key-rotation CronJob
        ├── storageclass.patch.yaml   # JSON6902: pin hcloud-volumes on the stateful VCTs
        └── bootstrap/                # cluster-scoped, account-gated, applied ONCE (NOT kustomized)
            ├── clusterissuer-letsencrypt.yaml # Let's Encrypt ClusterIssuer (cert-manager CRD)
            └── README.md             # bootstrap apply order + prereqs
```

Render the manifests (`base` is the single-replica rendering; swap it for
`overlays/ha` or `overlays/staging` to render those):

```bash
mise exec -- kustomize build --load-restrictor=LoadRestrictionsNone infra/k8s/base
```

Validate them (this is the CI gate — CI runs it for `base` and **both** overlays):

```bash
mise exec -- kustomize build --load-restrictor=LoadRestrictionsNone infra/k8s/base \
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

Everything lands in the **`babelstone-dev`** namespace (set by
`base/kustomization.yaml`; the `staging` overlay renames it to `babelstone-staging`).

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
- **The staging overlay never renders it** (bd babelstone-zla1.12.4):
  `overlays/staging/drop-dev-secrets.patch.yaml` deletes the Secret from the
  staging build, so the deployed bundle carries no credential bodies and a
  redeploy cannot clobber the operator-provisioned real `babelstone-dev-secrets`
  (created once — runbook
  [`staging-ops.md` §1 step 5](../runbooks/staging-ops.md)).
  `scripts/cd-secret-preflight.sh` is the fail-closed half: `cd.yml` refuses the
  staging apply if the render still emits a placeholder body (`--render`, also
  gated on the hermetic lane) or the live Secret is missing / still holds a
  placeholder value (`--live`, before `kubectl apply`).
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

## HA overlay — production-shaped topology (P.7)

The **`overlays/ha`** overlay (babelstone-ixkp) diverges from **`base`** in the HA
direction. It is the
topology [ADR-PC-005 §P1](../../docs/product-management/product_concepts/adrs/ADR-PC-005-dr-rto-rpo.md)
mandates for the source of truth (`events`, `outbox`, `saga_state`): a committed
event durable on **two** nodes before acknowledgement → **RPO ≈ 0**.

| Concern | base (single-node) | ha overlay |
|---|---|---|
| Redpanda | 1 node (`--mode=dev-container`) | **3-node Raft quorum**, seed-discovered via a headless Service ([ADR-IC-001](../../docs/product-management/integration_concepts/adrs/ADR-IC-001-event-backbone-message-broker.md)) |
| Postgres | 1 node | **primary + synchronous off-site warm standby** (streaming replication; `synchronous_standby_names`) |

```bash
mise exec -- kustomize build --load-restrictor=LoadRestrictionsNone infra/k8s/overlays/ha \
  | mise exec -- kubeconform -strict -summary -kubernetes-version 1.31.0
```

**Postgres is hand-rolled, not an operator.** The primary + standby are raw
`StatefulSet`s wired with native PostgreSQL streaming replication
(`pg_basebackup` bootstrap, a physical replication slot, `synchronous_commit=on`
naming the standby). This is deliberate: the standing preference is
fully-controlled native primitives over frameworks that own their own
tables/state, and streaming replication **is** the PG-native posture ADR-PC-005
chose (its candidate A). A fixed primary + one warm standby needs no CRD-driven
failover controller (CloudNativePG / Zalando) — adopting one would interpose a
framework owning failover and its own state for no benefit at this topology. The
rationale is recorded in the introducing PR body ([ADR-PC-020 §D3](../../docs/product-management/product_concepts/adrs/ADR-PC-020-llm-toolchain-and-conformance-governance.md):
no silent divergence — this *honours* ADR-PC-005).

**Off-site** is expressed as a topology constraint: the standby carries a
required zone anti-affinity against the primary, so a same-zone placement is a
scheduling failure, not a silent co-location (a co-located standby protects
nothing against a site/AZ loss).

**Postgres Service split (writes go only to the primary).** Both PG pods carry
`app.kubernetes.io/name: postgres`, so the overlay role-scopes the Services:
- `postgres` — the **write** entrypoint, narrowed to `pg-role: primary` (the
  base's broad selector would otherwise load-balance writes onto the read-only
  standby).
- `postgres-headless` — a headless (`clusterIP: None`) governing Service for the
  primary, the only thing that publishes the per-pod DNS
  (`postgres-0.postgres-headless`) the standby's `pg_basebackup` /
  `primary_conninfo` address. Same split the Redpanda overlay uses (a ClusterIP
  Service for clients + a headless Service for per-pod addressing).
- `postgres-standby` — a role-scoped headless Service for read-only / failover
  traffic to the standby.

**The infra CI job validates only.** The infra job kustomize-builds + kubeconforms
*both* overlays and asserts the HA commitments mechanically (3-node Redpanda; primary
`synchronous_commit`/`synchronous_standby_names`; standby slot + zone
anti-affinity). It does **not** spin up a real cluster. Two downstream lanes
ride on this wiring but are out of scope here:

- **Sync-replication append-latency benchmark — L.3** (the Q-AK load test named
  in ADR-PC-005 §P1): the RPO-vs-write-latency trade-off is *validated*, not
  assumed — but that is a load test, not this topology.
- **DR drill / PITR — M.4**: failover rehearsal, WAL archiving, and
  point-in-time recovery (ADR-PC-005 §P2, §P5).

*Applying* either overlay is the **CD / promotion pipeline — Q.6**
([`.github/workflows/cd.yml`](../../.github/workflows/cd.yml)): a human-dispatched,
environment-gated `promote` job kubectl-applies the chosen overlay, applies the
forward-only DB migrations ([`scripts/cd-migrate.sh`](../../scripts/cd-migrate.sh)),
and `deck sync`s the edge with real OpenBao material
([`scripts/deck-sync.sh`](../../scripts/deck-sync.sh)). This README's CI job stays
validate-only; the deploy lives in `cd.yml`.

The `ha-secrets.example.yaml` replication credential is a **DEV-ONLY
placeholder**, same seam contract as `base/secrets.example.yaml` — never commit
real credentials; M.2 replaces it with OpenBao-backed provisioning.

## staging overlay — the always-on public demo box (bd babelstone-zla1)

The **`overlays/staging`** overlay is the single, always-on, **public** demo /
staging environment: one CAX41 ARM node running single-node k3s in Hetzner
Helsinki, on the domain `babelstone.dev`. It diverges from **`base`** — the *same*
seam the `ha` overlay does — but in the **staging** direction, not the HA one. It
runs one copy of everything (HA would not fit a single node — do **not** promote
`overlays/ha` here), but adds two things `base` deliberately omits:
**durable storage** and a **public TLS edge**.

| Concern | base (single-node) | staging overlay |
|---|---|---|
| Namespace | `babelstone-dev` | `babelstone-staging` |
| Storage | unset → k3s `local-path` (node-local, ephemeral) | `hcloud-volumes` (Hetzner CSI block — survives node rebuild, **snapshot-able**) |
| Public access | `ClusterIP` + `kubectl port-forward` | a Traefik `Ingress` + cert-manager/Let's Encrypt TLS |

**Storage — durable by choice.** The base `volumeClaimTemplates` carry no
`storageClassName`, so on stock k3s they bind to the ephemeral `local-path`
provisioner. The staging overlay pins **`hcloud-volumes`** (the Hetzner CSI block
class) on the Postgres / Redpanda / registry claims and gives the Backstage DB its
own PVC, so the always-on box's data survives a node rebuild and is snapshot-able
for the Phase-6 backups (bd babelstone-zla1.7). This honours the locked
staging-env decision. The pin is a JSON6902 add-op (`storageclass.patch.yaml`) —
`volumeClaimTemplates` has no strategic-merge key, so a merge patch would *replace*
the whole VCT list and drop the base storage request. At `kustomize build` /
`kubeconform` time `storageClassName` is just a string; it binds to a real driver
only at apply, once Phase-1 [`hetzner-k3s`](../hetzner-k3s/) installs the Hetzner CCM + CSI.

**Public edge — the recorded drift.** `ingress.yaml` adds a public Traefik
`Ingress` (the `traefik` `IngressClass` is provided by the Traefik controller
installed at [`bootstrap/`](./overlays/staging/bootstrap/helm/traefik-values.yaml) —
hetzner-k3s disables the *bundled* Traefik + servicelb, so on this single node
Traefik is installed back and binds the node's `:80`/`:443` directly via hostPort,
there being no LoadBalancer; bd babelstone-zla1.14) for **six** hosts:
`api.babelstone.dev` → the **Kong** proxy (8000), `backstage.babelstone.dev` →
**Backstage** (7007), `app.babelstone.dev` → **Mission Control** (9000, the demo
UI, bd babelstone-zla1.5.5 — the browser hits only this host and Mission Control
same-origin-proxies the engine/orchestrator Services internally; **gated-by-default via a Logto
OIDC login** since bd babelstone-zla1.10.8.3 — `MC_AUTH_MODE=oidc`, so this public host is not an
unauthenticated surface, [ADR-IC-021](../../docs/product-management/integration_concepts/adrs/ADR-IC-021-iam-oauth-authorization-server.md)
Boundary-1), and
`auth.babelstone.dev` → **Logto** (3001, the OAuth 2.1 / OIDC Authorization Server,
[ADR-IC-021](../../docs/product-management/integration_concepts/adrs/ADR-IC-021-iam-oauth-authorization-server.md),
bd babelstone-zla1.10.2 — the staging box's token **issuer**: login, SCA, and the
MCP-agent authority). Logto is the **4th public host**; it is the token issuer, not
a product route, so it sits beside Backstage/Mission Control straight through Traefik
and Kong stays the [ADR-IC-006](../../docs/product-management/integration_concepts/adrs/ADR-IC-006-edge-api-gateway.md)
edge for the product API (Logto's core service port 3001 is fronted at `auth` — sign-in +
OIDC discovery + JWKS; and, since bd zla1.10, its admin console is fronted
separately at `auth-admin` — Logto OSS v1.41 rejects the console's Management-API
tokens unless it is reached at a stable HTTPS host matching `ADMIN_ENDPOINT`, so the
port-forward path no longer works. Since bd zla1.10.6 the console runs with
`ADMIN_DISABLE_LOCALHOST=true`, which stops Logto binding the separate 3002 admin listener
altogether — the console is served on the **core 3001 listener**, routed by the `auth-admin`
Host, so the `logto-admin` Ingress fronts 3001, not 3002. The admin console is auth-gated by
the Logto admin login + Cloudflare Access, the residual mitigation tracked as bd zla1.10.6).
The 6th host is
`grafana.babelstone.dev` → **Grafana** (3000, the observability UI, bd zla1.10.1/zla1.10.6) —
the regulated observability plane (ADR-IC-007 §P4), gated by Grafana login + Logto SSO + §P6
RBAC (anonymous OFF) and meant to sit behind the same Cloudflare Access gate; only the UI is
fronted (OTLP 4317/4318 stay Collector-only). Adding any public ingress extends
the previous
"no ingress/gateway exposure beyond Kong" posture, so this is an
[ADR-PC-020 §D3](../../docs/product-management/product_concepts/adrs/ADR-PC-020-llm-toolchain-and-conformance-governance.md)
**explicit-drift event**, acknowledged in the same change (this section, the scope
note above, and the introducing PR body — no silent divergence). The load-bearing
invariants are preserved:

- **Kong stays the [ADR-IC-006](../../docs/product-management/integration_concepts/adrs/ADR-IC-006-edge-api-gateway.md)
  authz edge, now *behind* Traefik.** Traefik terminates TLS and forwards to the
  Kong proxy, which still applies every declarative route + edge policy; nothing
  routes around Kong for the product API. Backstage (the catalogue portal — not
  part of the ADR-IC-006 product surface) is fronted directly.
- **No OTLP exposure** ([ADR-IC-007](../../docs/product-management/integration_concepts/adrs/ADR-IC-007-observability-stack.md)
  §P1): the Grafana **UI** (3000) is now ingressed at `grafana.babelstone.dev`
  behind Grafana login + Logto SSO + §P6 RBAC (bd zla1.10.1/zla1.10.6), but the
  collector's OTLP `4317/4318` are never fronted — they stay admitted from the
  Collector podSelector only and never reach a public route.
- **No POC cert/key literals committed** — TLS is issued at runtime by
  cert-manager, exactly as the edge mTLS material is sourced at `deck sync` time.
- **In-cluster network walls** (bd babelstone-zla1.12.8;
  [ADR-IC-006](../../docs/product-management/integration_concepts/adrs/ADR-IC-006-edge-api-gateway.md)
  §P5 / [ADR-IC-016](../../docs/product-management/integration_concepts/adrs/ADR-IC-016-service-identity-and-mtls.md)
  plane (i)): `network-policies.yaml` applies **default-deny ingress** to every pod
  with one targeted allow per real traffic edge — no arbitrary pod can reach the
  engine command surface (`POST /v1/deposits`), Postgres, OpenBao, Redpanda, or the
  Kong Admin API. Staging also binds the Kong Admin API to the pod loopback and
  drops its 8001 Service port (`kong-admin-localhost.patch.yaml` — the base table
  above still lists 8001 for the dev rendering); operator `deck sync` reaches it via
  `kubectl port-forward` (which targets the pod loopback). The internal-mTLS
  extension to the engine/orchestrator hops is authored but **gated off**
  (`internal-mtls.patch.yaml` + `bootstrap/internal-mtls.yaml` — rollout order in
  the patch header).

**TLS issuance is a cluster CRD, kept out of the kustomize build.** cert-manager's
`ClusterIssuer` (and `Certificate`) are CRDs, and the CI gate runs
`kubeconform -strict` with no CRD schemas — a CRD inside `kustomize build` would
hard-fail it. So only the built-in `Ingress` lives in the overlay (with a
`cert-manager.io/cluster-issuer` annotation; cert-manager's ingress-shim
auto-creates the per-host `Certificate`). The issuer itself lives in
[`staging/bootstrap/`](./overlays/staging/bootstrap/), **deliberately not
referenced** by the kustomization, and is applied once at cluster bootstrap
(Phase 2) alongside the cert-manager install.

**ACL — the WireMock stub, unchanged.** Staging inherits the base
`core-acl-stub` (WireMock) as-is; the real Core-ACL adapter is DEF-1
(bd babelstone-ub9s), out of scope here.

**Resource sizing — no OOM mid-demo.** `resources.patch.yaml` adds per-service
CPU/memory requests + limits sized for the single 16 vCPU / 32 GB node (a
multi-document strategic-merge patch — one document per workload container, each
adding only its `resources:` block). Every container declares a **memory limit**
(a runaway pod is capped, not allowed to take the node down — the locked
staging-env priority), with the sum of limits (≈18 GiB) kept comfortably under
node-allocatable (≈28 GiB after the k3s system reserve) and requests (≈4.6 GiB)
low enough that the whole stack schedules on one node. The per-service budget
table is in the patch file's header. CI asserts every staging workload container
carries a memory limit, so the sizing stays an invariant rather than a one-off
(the `base` and `ha` renderings are intentionally unsized — `base` is a laptop
stack, ha is multi-node and would size differently).

Validate (the same CI gate as `base`/`ha` — CI loops `base` plus both overlays):

```bash
mise exec -- kustomize build --load-restrictor=LoadRestrictionsNone infra/k8s/overlays/staging \
  | mise exec -- kubeconform -strict -summary -kubernetes-version 1.31.0
```

**Account-gated / deferred (not in this overlay yet):** provisioning the node,
installing the CSI driver + cert-manager + the issuer, pointing DNS at the node IP,
and the end-to-end cert verification all need the Hetzner account + DNS (Phases
0–2). The Phase-1 provisioner config now lives in [`../hetzner-k3s/`](../hetzner-k3s/)
(`hetzner-k3s create`); the apply itself stays account-gated. The engine, orchestrator, notification, Mission Control, and mcp-server
manifests have landed (zla1.5.1/.2/.3/.5 — mcp-server behind Kong over mutual TLS,
its internal-CA chain in `overlays/staging/bootstrap/mcp-mtls.yaml`); the only
remaining piece is the deferred real-Claude **agent host** (zla1.5.6); the real Backstage **image**
is zla1.6 (the base still pins `:placeholder`).

## Out of scope (downstream)

The `base` is a single, non-HA, dev-shaped rendering; the `ha`
overlay adds the production-shaped topology; the `staging` overlay (both above)
adds the always-on public demo box. The remaining scope split:

- **CD / promotion pipeline — Q.6** (babelstone-4c81): **implemented** in
  [`.github/workflows/cd.yml`](../../.github/workflows/cd.yml) — it cosign-verifies
  the promoted images by digest, gates the forward-only migrations, renders the
  overlay, and `deck sync`s the edge with real OpenBao mTLS + IAM key material
  (babelstone-4c81.1). The infra CI job *here* still only validates
  (`kustomize build` + `kubeconform`); the apply lives in `cd.yml`.
- **Real OpenBao provisioning — M.2** (babelstone-puu3): see the secret seam
  above. The CD pipeline *consumes* this boundary (it reads the edge mTLS + IAM
  key material from OpenBao at `deck sync` time); standing up real OpenBao
  storage/auth/HA is still M.2.
- **Application / engine service images**: out of scope here, exactly as in the
  Compose stack (backing infra only).
