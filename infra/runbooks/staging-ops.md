# Staging ops runbook (bd babelstone-zla1.7)

Plain English: the short operator guide for the always-on demo box — how to bring it up,
redeploy it, get it back after a failure, keep it patched, and know when it's down. It's the
single-node staging environment (`overlays/staging`), not the HA topology; the heavier DR drill
for the production-shaped topology is [`dr-recovery-drill.md`](./dr-recovery-drill.md).

Scope: one Hetzner CAX41 running single-node k3s, domain `babelstone.dev`. Stateful data is on
`hcloud-volumes` CSI block storage (durable across a node rebuild).

---

## 1. Provision / first bring-up (Phases 0–2, account-gated)

1. Provision the node + k3s with the Hetzner CCM + CSI (Phase 1, `hetzner-k3s`).
2. Point DNS A records `app`, `api`, `backstage`.`babelstone.dev` at the node IP.
3. Install the cluster add-ons (all under [`../k8s/overlays/staging/bootstrap/`](../k8s/overlays/staging/bootstrap/)):
   - **cert-manager** (Helm) — see `bootstrap/README.md`.
   - the **external CSI snapshot controller + CRDs** (NOT bundled with k3s) — required by the
     `VolumeSnapshotClass` and the volume-snapshot CronJob.
   - Rancher **system-upgrade-controller** (creates the `system-upgrade` namespace + SA the
     k3s upgrade Plan uses).
4. `kubectl apply -f infra/k8s/overlays/staging/bootstrap/` (the issuers, the
   `VolumeSnapshotClass`, the k3s upgrade `Plan`).
5. Provision the real secrets (swap the dev placeholders): `babelstone-dev-secrets`
   (Postgres/OpenBao), `babelstone-backup-secret` (Hetzner Object Storage keys + bucket),
   and the Kong mTLS material (via `deck-sync`).
6. Deploy: `kubectl apply -k infra/k8s/overlays/staging` (or dispatch `cd.yml` with
   `overlay: staging`).

## 2. Redeploy / promote a new build

`cd.yml` (workflow_dispatch, `overlay: staging`, `apply: true`) cosign-verifies the images by
digest, gates the forward-only migrations, renders + kubeconforms the overlay, applies it, and
`deck sync`s Kong. The event-store migration Job + the engine initContainer handle schema
ordering automatically (the engine waits on the migration sentinel).

## 3. Restore

Two independent recovery paths (use whichever fits the incident):

**A. Logical (portable, off-box) — the `db-logical-backup` CronJob.** Daily dumps live in
Hetzner Object Storage at `s3://$S3_BUCKET/postgres/all-*.sql.gz` and `.../backstage/backstage-*.sql.gz`.
To restore the main cluster:
```bash
aws --endpoint-url "$S3_ENDPOINT" s3 cp s3://$S3_BUCKET/postgres/all-<TS>.sql.gz - \
  | gunzip | psql -h postgres -U babelstone -d postgres   # pg_dumpall output recreates each DB
```
Restore Backstage from its `backstage-<TS>.sql.gz` against `backstage-db` the same way.

**B. Block-level — CSI VolumeSnapshots** (the `volume-snapshot` CronJob). List them
(`kubectl get volumesnapshot`), then provision a new PVC `dataSource:` that VolumeSnapshot and
re-point the StatefulSet/Deployment. Faster for a whole-volume rollback (incl. Redpanda state),
but same-cluster only — the logical dumps are the off-box copy.

## 4. Upgrade (k3s)

The `k3s-server` `Plan` (system-upgrade-controller, `bootstrap/k3s-upgrade-plan.yaml`) follows
the k3s `stable` channel. On a single node an upgrade **cordons + drains the one node**, so there
is a brief downtime window — expect it, schedule off-hours. To upgrade on demand, bump/re-apply
the Plan or annotate it; the controller does the in-place binary swap. App pods reschedule once
the node is back `Ready`.

## 5. Backups — what runs, when

| Job | Schedule (UTC) | What | Where |
|---|---|---|---|
| `db-logical-backup` | 01:30 daily | `pg_dumpall` (engine + orchestrator DBs) + `pg_dump` backstage | Hetzner Object Storage (S3) |
| `volume-snapshot` | 02:30 daily | CSI `VolumeSnapshot` of postgres / redpanda / backstage-db PVCs | in-cluster (CSI) |

Check: `kubectl get cronjob,job` and the job logs. **Retention/pruning is manual for v1** — prune
old S3 objects (lifecycle policy on the bucket) and old `VolumeSnapshot`s (`kubectl delete
volumesnapshot -l babelstone.io/dr-role=volume-snapshot --field-selector ...`) periodically.

## 6. Uptime / alerting

- **End-to-end uptime = an external HTTP pinger** (e.g. UptimeRobot / healthchecks.io, free tier)
  on `https://app.babelstone.dev/` (and `https://api.babelstone.dev/`). This is the authoritative
  "is the box reachable" signal — it tests DNS + TLS + ingress + app from outside the cluster.
  Configure it during Phase 0/2 (account-gated; no in-cluster manifest).
- **In-cluster signal**: the `EngineMetricsAbsent` rule (`infra/grafana/prometheus/alert-rules.yaml`,
  `staging-liveness` group) fires when the engine stops emitting its SLI metric. NOTE: loading
  `alert-rules.yaml` into the k8s grafana-lgtm appliance (`rule_files:`) is a follow-up — today the
  rules file is wired for compose / a standalone Prometheus. Until then, the external pinger is the
  live uptime check.

## 7. Common checks

```bash
kubectl -n babelstone-staging get pods,svc,ingress,cronjob
kubectl -n babelstone-staging get certificate          # cert-manager TLS health (needs the CRDs)
kubectl -n babelstone-staging logs deploy/engine       # app logs
```
