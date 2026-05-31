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
| **EventCatalog host** | Static-site host (nginx) for the built event catalog | [ADR-IC-008](../docs/product-management/integration_concepts/adrs/ADR-IC-008-event-catalog-governance-tooling.md) |

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
| Redpanda Admin | `localhost:9644` | `rpk` / health |
| Redpanda Console | `http://localhost:8080` | browse topics + schemas |
| Kong proxy | `http://localhost:8000` | the edge — external surfaces route through here |
| Kong admin | `http://localhost:8001` | declarative config + status (local dev only) |
| OpenBao | `http://localhost:8200` | API + UI (`/ui`); dev root token `root` |
| OTLP endpoint | `localhost:4317` (gRPC) / `:4318` (HTTP) | **export all telemetry here** — the collector boundary |
| Grafana | `http://localhost:3000` | logs/traces/metrics in one UI; anonymous admin (dev) |
| OCI registry | `localhost:5001` | `oras push/pull` packs (host 5001 → 5000; 5000 collides with macOS AirPlay) |
| EventCatalog | `http://localhost:8082` | static catalog site |

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
- **Kong runs DB-less.** Its entire config is `kong/kong.yml` ([ADR-IC-006](../docs/product-management/integration_concepts/adrs/ADR-IC-006-edge-api-gateway.md)) — no gateway database. The config is empty of routes today; the Deposits REST API, SSE saga stream, and the MCP route ([ADR-IC-010](../docs/product-management/integration_concepts/adrs/ADR-IC-010-mcp-server-runtime-and-sdk.md)) land with Epic I as PRs on that file.
- **OpenBao runs in dev mode** — in-memory, auto-unsealed, fixed root token `root`. It is the local crypto boundary ([ADR-PC-004](../docs/product-management/product_concepts/adrs/ADR-PC-004-pii-crypto-shredding.md)); the transit engine + per-subject keys are enabled by the engine (**Epic A.5**), not this stack. **Not production**: real storage, unseal, and HA/DR ([ADR-PC-005](../docs/product-management/product_concepts/adrs/ADR-PC-005-dr-rto-rpo.md)) come with P.6/P.7.
- **Services export to the OTel Collector, never to a backend directly** ([ADR-IC-007](../docs/product-management/integration_concepts/adrs/ADR-IC-007-observability-stack.md) §P1). `otel/collector.yaml` is the owned single export-config point — sampling and PII/attribute redaction (pseudonymous IDs in traces) land there with Epic K. The collector forwards OTLP to the Grafana LGTM appliance, whose own OTLP ingest is **not** exposed to the host so the collector stays the single entry. Loki/Tempo are single-node and **non-HA** (dev only); production storage/replication is P.6/P.7.
- **The OCI registry hosts packs, not pack content.** Packs are `oras`-pushed as OCI artefacts and pulled **by digest** ([ADR-PC-007](../docs/product-management/product_concepts/adrs/ADR-PC-007-signed-yaml-oci-pack.md)); the registry runs with defaults (no auth, plain-HTTP — dev only). The pack build/sign pipeline (CUE validate → cosign → `oras push`) and the `babelstone-packs/*` content land with **Epic C.4/C.5**.
- **EventCatalog is host-only.** nginx serves `eventcatalog/site/` (a placeholder today). The catalog itself — AsyncAPI specs rendered to a static EventCatalog build ([ADR-IC-008](../docs/product-management/integration_concepts/adrs/ADR-IC-008-event-catalog-governance-tooling.md)) — is generated into that dir by **Epic G.4**.
- This is **not** the production topology. P.7 adds 3-node Redpanda + PG HA; P.3/P.4
  add Kong + OpenBao and the Grafana LGTM + OTel observability stack.

---

## Deployed environment (K8s) (`k8s/`)

The deployed counterpart of the Compose stack lives under [`k8s/`](./k8s/) —
Kustomize manifests (base + `overlays/dev`), per
[ADR-IC-013 §D2](../docs/product-management/integration_concepts/adrs/ADR-IC-013-in-house-estate-build-and-repository-placement.md)
(IaC subtree co-located in the monorepo). It deploys the **same 9 backing-infra
services** to Kubernetes, shaped for a single **dev / staging** environment.

```bash
mise exec -- kustomize build --load-restrictor=LoadRestrictionsNone infra/k8s/overlays/dev
```

The `kong/kong.yml` and `otel/collector.yaml` above are the **single source of
truth** — the K8s ConfigMaps are *generated* from them (not duplicated), so a
config change updates both stacks.

Scope is deliberately narrow (matching the Compose stack):

- **Single env, non-HA** — single-replica everywhere. The HA topology (3-node
  Redpanda, Postgres HA, warm standby) is **P.7** (babelstone-ixkp).
- **No CD pipeline** — CI only validates manifests (`kustomize build` +
  `kubeconform`); promotion/apply is **Q.6** (babelstone-4c81).
- **OpenBao is a dev-mode seam** — real provisioning is **M.2** (babelstone-puu3).

See [`k8s/README.md`](./k8s/README.md) for the full layout and scope boundaries.
