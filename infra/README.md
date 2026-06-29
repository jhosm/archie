# /infra

Deploy, runbook, and operational tooling for the engine and the in-house estate —
"one codebase, one set of images" ([01 §6](../docs/product-management/product_concepts/01-product-architecture.md)).

- **Build provenance:** in-house
- **CODEOWNERS:** engine team
- **Path-scoped CI:** infra manifest lint / validate

Layout governed by [ADR-PC-019 §P1](../docs/product-management/product_concepts/adrs/ADR-PC-019-repository-strategy-monorepo.md).

---

## Local dev stack (`compose.yaml`)

The minimum to run the engine's walking skeleton on a laptop — **backing
infrastructure only**, no application images (the service subtrees are still
skeletons). Brought up via the repo-root `Makefile`.

| Service | Role | ADR |
|---|---|---|
| **PostgreSQL** | Event store | [ADR-PC-001](../docs/product-management/product_concepts/adrs/ADR-PC-001-event-store-technology.md) |
| **Redpanda** | Kafka-compatible event backbone | [ADR-IC-001](../docs/product-management/integration_concepts/adrs/ADR-IC-001-event-backbone-message-broker.md) |
| └─ built-in **Schema Registry** | Confluent SR API (`/subjects`, `/schemas`) | [ADR-IC-002](../docs/product-management/integration_concepts/adrs/ADR-IC-002-schema-format-and-registry.md) |
| **Redpanda Console** | Web UI for topics + schema registry | dev convenience |
| **Kong Gateway CE** | Edge API gateway, DB-less declarative mode | [ADR-IC-006](../docs/product-management/integration_concepts/adrs/ADR-IC-006-edge-api-gateway.md) |
| **OpenBao** | Per-subject key store (crypto-shredding), dev mode | [ADR-PC-004](../docs/product-management/product_concepts/adrs/ADR-PC-004-pii-crypto-shredding.md) |
| **OTel Collector** | Telemetry pipeline boundary; config `otel/collector.yaml` | [ADR-IC-007](../docs/product-management/integration_concepts/adrs/ADR-IC-007-observability-stack.md) |
| **Grafana LGTM** | Loki + Grafana + Tempo + Prometheus (all-in-one) | [ADR-IC-007](../docs/product-management/integration_concepts/adrs/ADR-IC-007-observability-stack.md) |
| **OCI registry** | Distribution registry for `oras`-pushed packs (by digest) | [ADR-PC-007](../docs/product-management/product_concepts/adrs/ADR-PC-007-signed-yaml-oci-pack.md) |
| **Backstage catalogue portal** | Backstage host (Node.js service + PostgreSQL) that renders the governed AsyncAPI catalogue from `contracts/catalog/catalog-info.yaml`; manifests shipped, app image is human-handoff | [ADR-IC-015](../docs/product-management/integration_concepts/adrs/ADR-IC-015-event-catalog-governance-tooling-backstage.md) (supersedes the retired ADR-IC-008) — bd babelstone-s4ol.1 |
| **Core-ACL settlement stub** | WireMock stub for the saga's gated settlement legs (reserve/confirm/release/reverse); `Settlement:BaseUrl` → `localhost:8089`; mappings `wiremock/mappings/` | [ADR-PC-016](../docs/product-management/product_concepts/adrs/ADR-PC-016-legacy-current-account-adapter.md) / [ADR-PC-029](../docs/product-management/product_concepts/adrs/ADR-PC-029-engine-command-ingress.md); real ACL is DEF-1 — bd babelstone-ub9s |

### Quick start

From the repo root (needs Docker — see [`INSTALL.md`](../INSTALL.md)):

```bash
make up        # start, wait until healthy, print endpoints
make verify    # smoke-test every service
make logs      # follow logs
make down      # stop, keep data
make reset     # wipe data volumes and start fresh
```

### Endpoints (host)

| What | Address | Notes |
|---|---|---|
| PostgreSQL | `localhost:5432` | db `babelstone`, user `babelstone`, password `babelstone` |
| Kafka API | `localhost:19092` | from the host; **inside** the compose network use `redpanda:9092` |
| Schema Registry | `http://localhost:18081` | inside the network: `http://redpanda:8081` |
| Redpanda HTTP Proxy | `http://localhost:18082` | pandaproxy (Kafka REST); inside the network: `http://redpanda:8082`. Mission Control's Topic·Avro lens reads topic records over it (no Kafka client) |
| Redpanda Admin | `localhost:9644` | `rpk` / health |
| Redpanda Console | `http://localhost:8080` | browse topics + schemas |
| Kong proxy | `http://localhost:8000` | the edge — external surfaces route through here |
| Kong admin | `http://localhost:8001` | declarative config + status (local dev only) |
| OpenBao | `http://localhost:8200` | API + UI (`/ui`); dev root token `root` |
| OTLP endpoint | `localhost:4317` (gRPC) / `:4318` (HTTP) | **export all telemetry here** — the collector boundary |
| Grafana | `http://localhost:3000` | logs/traces/metrics in one UI; anonymous admin (dev) |
| Prometheus | `http://localhost:9090` | the appliance's metrics query API (`/api/v1/query…`); Mission Control's Metrics lens reads the real SLI series |
| Loki | `http://localhost:3100` | the appliance's logs query API (`/loki/api/v1/query_range…`); Mission Control's Logs lens reads the real structured logs |
| Tempo | `http://localhost:3200` | the appliance's traces query API (`/api/traces/{id}`); Mission Control's Telemetry tab reads real spans by trace id |
| OCI registry | `localhost:5001` | `oras push/pull` packs (host 5001 → 5000; 5000 collides with macOS AirPlay) |
| Backstage portal | `http://localhost:7007` | renders the AsyncAPI catalogue; profile-gated behind `catalog` (skipped by default `make up`) and up only once the app image is built+pushed — `docker compose --profile catalog up` (see below) |

.NET connection string (engine, Npgsql):
`Host=localhost;Port=5432;Database=babelstone;Username=babelstone;Password=babelstone`

All credentials are **local-dev only**, not secrets. Override any of them via
environment variables (`POSTGRES_PASSWORD`, `POSTGRES_PORT`, `CONSOLE_PORT`, …)
before `make up`; defaults live in `compose.yaml`.

### Scope boundaries

- **Bootstrap = database + role only.** The `POSTGRES_*` env vars create the
  database and login role on first start. The engine's `events` / outbox **tables**
  are owned by **Epic A.1** migrations, not by this stack. `postgres/initdb.d/` is
  the seam for any future *non-engine* seed.
- **No separate Schema Registry container.** Redpanda Community Edition ships the
  Confluent SR API in-process ([ADR-IC-002](../docs/product-management/integration_concepts/adrs/ADR-IC-002-schema-format-and-registry.md)),
  so the local stack is two infra containers, not three.
- **Topics auto-create** in `dev-container` mode; explicit topic + schema
  definitions arrive with the producing services (Epics A/E).
- **Kong runs DB-less.** Its entire config is `kong/kong.yml` ([ADR-IC-006](../docs/product-management/integration_concepts/adrs/ADR-IC-006-edge-api-gateway.md)) — no gateway database. The config now fronts the **client-facing edge surface** (I.3): the I.1 edge-over-saga front door (`POST /api/v1/deposits/constitute` + the SSE `GET /api/v1/processes/{id}/stream`, on the orchestrator upstream) and the CQRS query reads (`GET /v1/deposits/{id}` and `/v1/deposits/maturities`, on the engine upstream), with the edge policies attached (jwt, rate-limiting, payload validation, OTel `traceparent` propagation, upstream mTLS). The engine **command** surface (`POST /v1/deposits`) is deliberately **not** a public route — it is the orchestrator's internal, mTLS-only saga target ([ADR-IC-006 §P5](../docs/product-management/integration_concepts/adrs/ADR-IC-006-edge-api-gateway.md)). The config is validated in CI by `make kong-config-check` (`deck file validate` + `kong config parse` + edge-contract assertions). PSD2 SCA enforcement (I.4, bd babelstone-6imx) and the MCP route ([ADR-IC-010](../docs/product-management/integration_concepts/adrs/ADR-IC-010-mcp-server-runtime-and-sdk.md)) land as further PRs on that file.
- **OpenBao runs in dev mode** — in-memory, auto-unsealed, fixed root token `root`. It is the local crypto boundary ([ADR-PC-004](../docs/product-management/product_concepts/adrs/ADR-PC-004-pii-crypto-shredding.md)); the transit engine + per-subject keys are enabled by the engine (**Epic A.5**), not this stack. **Not production**: real storage, unseal, and HA/DR ([ADR-PC-005](../docs/product-management/product_concepts/adrs/ADR-PC-005-dr-rto-rpo.md)) come with P.6/P.7.
- **Services export to the OTel Collector, never to a backend directly** ([ADR-IC-007](../docs/product-management/integration_concepts/adrs/ADR-IC-007-observability-stack.md) §P1). `otel/collector.yaml` is the owned single export-config point — sampling and PII/attribute redaction (pseudonymous IDs in traces) land there with Epic K. The collector forwards OTLP to the Grafana LGTM appliance, whose own OTLP ingest is **not** exposed to the host so the collector stays the single entry. Loki/Tempo are single-node and **non-HA** (dev only); production storage/replication is P.6/P.7.
- **The OCI registry hosts packs, not pack content.** Packs are `oras`-pushed as OCI artefacts and pulled **by digest** ([ADR-PC-007](../docs/product-management/product_concepts/adrs/ADR-PC-007-signed-yaml-oci-pack.md)); the registry runs with defaults (no auth, plain-HTTP — dev only). The pack build/sign pipeline (CUE validate → cosign → `oras push`) and the `babelstone-packs/*` content land with **Epic C.4/C.5**.
- **The Backstage catalogue portal renders the AsyncAPI catalogue ([ADR-IC-015](../docs/product-management/integration_concepts/adrs/ADR-IC-015-event-catalog-governance-tooling-backstage.md)).** The `backstage` service (Node.js + its own `backstage-db` PostgreSQL) mounts `backstage/app-config.yaml` and the read-only `contracts/catalog/` descriptor tree; its **only** catalogue source is the registered `catalog-info.yaml`, which `$text`-references the AsyncAPI files under `contracts/catalog/events/` — Backstage restates **no** schema (the no-drift invariant, ADR-IC-015 Decision §1–§2). This retired the EventCatalog nginx placeholder (`eventcatalog/site/`, ADR-IC-008) that the supersession left behind.
  - **Human-handoff tail (this is what is NOT done headless).** Backstage ships **no turnkey image** — a real portal additionally needs: (1) an app scaffolded with `npx @backstage/create-app`, the AsyncAPI catalog-provider plugins added, and an image built + pushed, then the `image:` placeholder in `compose.yaml` / `k8s/base/backstage.yaml` pointed at it; (2) for the K8s path, the whole `contracts/catalog/` tree mounted (a ConfigMap is flat and can't carry the `events/`+`reconciliation/` subdirs the `$text` refs resolve against — use an init-container `git`-clone or a build-time COPY); (3) the Backstage **GDPR surface** entered in the data inventory once user/team identity data exists (ADR-IC-015 Residual Risk). Until that image exists, the `backstage` + `backstage-db` services are gated behind the `catalog` Compose profile so the default `make up` **skips** them — a placeholder `image:` would otherwise fail to pull (`denied`) and abort the whole bring-up. Once a real image is pushed, start the portal with `docker compose --profile catalog up`. Meanwhile the estate operates Git-native (AsyncAPI files + the [`asyncapi-catalog-validate`](../scripts/asyncapi-catalog-validate.sh) CI gate + GitHub's renderer) — the documented fallback posture [ADR-IC-015 §9](../docs/product-management/integration_concepts/adrs/ADR-IC-015-event-catalog-governance-tooling-backstage.md) records. `make verify` treats the portal as a non-fatal, informational check for exactly this reason.
- This is **not** the production topology. The production-shaped HA topology
  (3-node Redpanda + PG primary/synchronous warm standby) is **P.7** and lives in
  the [`k8s/overlays/ha`](./k8s/README.md) overlay, not this dev Compose stack;
  P.3/P.4 add Kong + OpenBao and the Grafana LGTM + OTel observability stack.

---

## Deployed environment (K8s) (`k8s/`)

The deployed counterpart of the Compose stack lives under [`k8s/`](./k8s/) —
Kustomize manifests (base + `overlays/dev` + `overlays/ha` + `overlays/staging`), per
[ADR-IC-013 §D2](../docs/product-management/integration_concepts/adrs/ADR-IC-013-in-house-estate-build-and-repository-placement.md)
(IaC subtree co-located in the monorepo). It deploys the **same 10 services**
(9 backing-infra + the v1 Core-ACL settlement stub) to Kubernetes: `overlays/dev` is single-replica
dev; `overlays/ha` is the production-shaped HA topology (P.7); `overlays/staging` is the always-on
public demo box (single-node k3s on Hetzner — bd babelstone-zla1). The staging cluster itself is
provisioned (Phase 1) from [`hetzner-k3s/`](./hetzner-k3s/), a layer below these manifests.

```bash
mise exec -- kustomize build --load-restrictor=LoadRestrictionsNone infra/k8s/overlays/dev
```

The `kong/kong.yml` and `otel/collector.yaml` above are the **single source of
truth** — the K8s ConfigMaps are *generated* from them (not duplicated), so a
config change updates both stacks.

Scope is deliberately narrow (matching the Compose stack):

- **`overlays/dev` is single-replica, non-HA.** The production-shaped HA
  topology (3-node Redpanda, Postgres primary + synchronous off-site warm
  standby) is **P.7** (babelstone-ixkp), in the sibling `overlays/ha` — see
  [`k8s/README.md`](./k8s/README.md). Both overlays are CI-validated.
- **CD is the `cd.yml` promotion pipeline (Q.6, babelstone-4c81).** The infra CI job here only
  validates manifests (`kustomize build` + `kubeconform`); the human-dispatched promotion/apply
  lives in [`.github/workflows/cd.yml`](../.github/workflows/cd.yml).
- **OpenBao is a dev-mode seam** — real provisioning is **M.2** (babelstone-puu3).

See [`k8s/README.md`](./k8s/README.md) for the full layout and scope boundaries.
