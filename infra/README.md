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

### Quick start

From the repo root (needs Docker — see [`INSTALL.md`](../INSTALL.md)):

```bash
make up        # start, wait until healthy, print endpoints
make verify    # smoke-test all three services
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
- This is **not** the production topology. P.7 adds 3-node Redpanda + PG HA; P.3/P.4
  add Kong + OpenBao and the Grafana LGTM + OTel observability stack.
