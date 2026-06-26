# Application services layer (`infra/k8s/apps/`)

Plain English: this directory holds the babelstone **application** services (the engine, and
in later slices the orchestrator, notification worker, and MCP server) as Kubernetes manifests.
It's kept separate from [`../base`](../base), which is deliberately *backing-infra only*
(Postgres, Redpanda, Kong, …). The always-on **staging** overlay composes both
([`../overlays/staging`](../overlays/staging) references `../../base` **and** `../../apps`);
the `dev` and `ha` overlays reference only `base`, so they're unaffected and base keeps its
documented scope. Governed by [ADR-IC-013 §D2](../../docs/product-management/integration_concepts/adrs/ADR-IC-013-in-house-estate-build-and-repository-placement.md)
(IaC co-located in the monorepo); tracked under bd babelstone-zla1.5.

This layer carries **no namespace** — the composing overlay sets `namespace: babelstone-staging`,
which stamps every resource here. It references base's Secret/ConfigMap seam
(`babelstone-dev-secrets`, `postgres-config`) by name, so it is only meaningful composed onto
base; `kustomize build` of this dir alone still renders (it just emits the app resources
without the infra they wire to).

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

### Secret seam

The DB password is the one secret on this path: `POSTGRES_PASSWORD` from base's
`babelstone-dev-secrets` (a **dev-only placeholder**; real OpenBao-backed provisioning is M.2
/ bd babelstone-puu3 — never commit real credentials). `OpenBao__Enabled=false`, so the engine
resolves the password from config; no PII crosses the bus (ADR-PC-004).

## Validate

```bash
mise exec -- kustomize build --load-restrictor=LoadRestrictionsNone infra/k8s/overlays/staging \
  | mise exec -- kubeconform -strict -summary -kubernetes-version 1.31.0
```
