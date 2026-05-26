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
- This is **not** the production topology. P.7 adds 3-node Redpanda + PG HA; P.3/P.4
  add Kong + OpenBao and the Grafana LGTM + OTel observability stack.
