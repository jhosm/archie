# Application services layer (`infra/k8s/apps/`)

Plain English: this directory holds the babelstone **application** services (the engine, and
in later slices the orchestrator, notification worker, and MCP server) as Kubernetes manifests.
It's kept separate from [`../base`](../base), which is deliberately *backing-infra only*
(Postgres, Redpanda, Kong, …). The always-on **staging** overlay composes both
([`../overlays/staging`](../overlays/staging) references `../../base` **and** `../../apps`);
the `ha` overlay references only `base`, so it's unaffected and base keeps its
documented scope. Governed by [ADR-IC-013 §D2](../../docs/product-management/integration_concepts/adrs/ADR-IC-013-in-house-estate-build-and-repository-placement.md)
(IaC co-located in the monorepo); tracked under bd babelstone-zla1.5.

This layer carries **no namespace** — the composing overlay sets `namespace: babelstone-staging`,
which stamps every resource here. It references base's Secret/ConfigMap seam
(`babelstone-dev-secrets`, `postgres-config`) by name, so it is only meaningful composed onto
base; `kustomize build` of this dir alone still renders (it just emits the app resources
without the infra they wire to).

| Slice | Resource | Kind | Notes |
|---|---|---|---|
| zla1.5.1 | `engine` | Deployment + Service (8080) | the event-sourced product kernel (ADR-PC-010) |
| zla1.5.1 | `event-store-migrate` | Job | applies the event-store schema before the engine boots |
| zla1.5.2 | `orchestrator` | Deployment + Service (8080) | constitution-saga edge + workers (ADR-IC-003); its own `babelstone_orchestrator` DB |
| zla1.5.2 | `notification` | Deployment (no Service) | headless reminder worker (ADR-IC-011); replicas:1 |

## zla1.5.1 — engine + the event-store migration

| Resource | Kind | Notes |
|---|---|---|
| `engine` | Deployment + Service (8080) | the event-sourced product kernel (ADR-PC-010) |
| `event-store-migrate` | Job | applies the event-store schema before the engine boots |

### Why a migration Job (the engine doesn't self-migrate the event store)

The engine applies **only** its family read-model on boot, and that needs the
`babelstone_engine` role created by event-store migration `0002`. So the event store
(`engine/src/Babelstone.EventStore.Migrations/Sql/0001..0017+`) must be applied **first**, by
something else. The flow:

1. **`event-store-migrate` Job** (stock multi-arch `postgres:18-alpine`) runs
   [`migration-apply.sh`](./migration-apply.sh) — a forward-only, ledger-guarded apply
   (each SQL file + its `schema_migrations` row in one transaction, `ON_ERROR_STOP=1`),
   mirroring `apply_event_store_schema()` in `scripts/demo-lib.sh`. The SQL arrives as the
   `event-store-sql` ConfigMap. When it finishes it writes a sentinel table
   `_event_store_apply_complete`.
2. The **engine pod's initContainer** polls for that sentinel before the engine container
   starts — decoupled from any hardcoded migration ceiling, needs no kube RBAC, and is
   race-free (only the Job applies; the init only waits).

The migration SQL is listed file-by-file in [`kustomization.yaml`](./kustomization.yaml)'s
`configMapGenerator` (a flat ConfigMap can't glob a directory). **Adding a new `00NN_*.sql`
migration to the engine means adding it here too** — a `ci.yml` assertion fails the build if
this list drifts from the on-disk `Sql/` dir, so the drift can't land silently.

### Pack-baked engine image

The engine runs in **disk** pack-mode on the box, so pack `pt.2026.1` (a directory tree —
a flat ConfigMap can't carry its subdirs) is baked into a thin derived image,
[`engine/Dockerfile`](./engine/Dockerfile): `FROM` the base
`ghcr.io/jhosm/babelstone-engine` image + `COPY packs/pt.2026.1`. A dependent
`build-engine-staging` job in `.github/workflows/image-build.yml` builds + pushes it
multi-arch (arm64) and cosign-signs it by digest (so `cd.yml` can verify it on promotion).
The base tag is a build-arg — pin it by digest for a real promotion; a movable `:latest` is
fine for the demo box.

### Pack-baked notification image

The notification host resolves the same instance-pinned pack off disk at startup
(ADR-PC-007 §P4), so `pt.2026.1` is baked into a twin derived image the identical way,
[`notification/Dockerfile`](./notification/Dockerfile): `FROM` the base
`ghcr.io/jhosm/babelstone-notification` image + `COPY packs/pt.2026.1`. The dependent
`build-notification-staging` job in `.github/workflows/image-build.yml` builds + pushes it
multi-arch and cosign-signs it by digest, exactly as `build-engine-staging` does (bd
zla1.5.10); `notification.yaml` sets `Engine__PacksDir=/app/packs` to point the host's disk
walk at the baked pack.

### Secret seam

The DB password is the one secret on this path: `POSTGRES_PASSWORD` from base's
`babelstone-dev-secrets` (a **dev-only placeholder**; real OpenBao-backed provisioning is M.2
/ bd babelstone-puu3 — never commit real credentials). `OpenBao__Enabled=false`, so the engine
resolves the password from config; no PII crosses the bus (ADR-PC-004).

## zla1.5.2 — orchestrator + notification

The **orchestrator** (ADR-IC-003) is the constitution-saga edge *and* its workers in one pod:
the edge HTTP front door (`POST /api/v1/deposits/constitute` → 202 + SSE), the per-module
Redpanda consume loops, and the dispatcher (saga_outbox → HTTP to the engine + the settlement
stub). Two things differ from the engine:

- **Its own database.** It owns `babelstone_orchestrator`, separate from the engine's
  `babelstone` DB (they share table names, so they can't co-locate). An **initContainer** creates
  that DB idempotently (guarded `CREATE DATABASE`, which can't run in a transaction) before the
  orchestrator's `SagaMigrationHostedService` self-applies the saga schema on boot. Both its
  connection strings (the DDL migration role + the runtime role) collapse onto the dev superuser
  for staging — separate least-privilege logins are later hardening (ADR-PC-001 §P3).
- **`ASPNETCORE_URLS` is mandatory** — its image's runtime base has no aspnet default, so Kestrel
  binds nothing unless told to (`http://0.0.0.0:8080`).

The **notification** worker (ADR-IC-011) is a headless `BackgroundService` — **no port, no
Service, no probes** (nothing listens), `replicas:1` (its v1 dedupe ledger is in-memory and
per-pod). It only reads the engine API and exports telemetry; no DB/Kafka/Kong/OpenBao, no PII.

Both run the stock multi-arch images (`babelstone-orchestrator`, `babelstone-notification`) — no
derived image or pack-bake needed (only the engine loads a pack).

## Validate

```bash
mise exec -- kustomize build --load-restrictor=LoadRestrictionsNone infra/k8s/overlays/staging \
  | mise exec -- kubeconform -strict -summary -kubernetes-version 1.31.0
```
