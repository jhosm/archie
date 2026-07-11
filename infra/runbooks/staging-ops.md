# Staging ops runbook (bd babelstone-zla1.7)

Plain English: the short operator guide for the always-on demo box — how to bring it up,
redeploy it, get it back after a failure, keep it patched, and know when it's down. It's the
single-node staging environment (`overlays/staging`), not the HA topology; the heavier DR drill
for the production-shaped topology is [`dr-recovery-drill.md`](./dr-recovery-drill.md).

Scope: one Hetzner CPX42 x86 running single-node k3s, domain `babelstone.dev`. Stateful data is on
the k3s built-in **`local-path`** provisioner (node-local storage — bd babelstone-zla1.12.20).
It survives a **pod restart** but **NOT node loss**: there is no block-storage / cloud snapshot
layer, because DR is deliberately **out of scope on staging** (the production-shaped DR drill is
[`dr-recovery-drill.md`](./dr-recovery-drill.md)).

---

## 1. Provision / first bring-up (Phases 0–2, account-gated)

> **⚠️ Execution order — read before you start.** The numbered steps below are grouped by topic, but
> three real dependencies force an *interleaved* order; a first bring-up that follows the numbers
> literally will stall:
> - **Provision `babelstone-dev-secrets` (step 5) BEFORE running the bootstrap (step 3).** The
>   bootstrap's secret gate (`cd-secret-preflight.sh --live`) refuses to run until that Secret exists
>   and is non-placeholder.
> - **Initialize OpenBao AFTER the deploy (step 6), not before** — `bao operator init` execs into the
>   `openbao` pod, which only exists once the overlay is applied. The engine stays NotReady
>   (fail-closed) until then. See step 7 and [`../k8s/components/openbao-csi/README.md`](../k8s/components/openbao-csi/README.md).
> - **The 3 Logto client secrets can only be minted after Logto is running (post-deploy).** Seed them
>   with throwaway non-placeholder values in step 5, then register the apps and replace them in step 7.
>
> Net order: DNS (2) → secrets (5) → bootstrap (3–4) → deploy (6) → OpenBao init + engine restart (7)
> → Logto registration + real client secrets + client restart (7) → verify (§7).

1. Provision the node + k3s (Phase 1, `hetzner-k3s`) — see **§1.1** below. The Hetzner **CCM and
   CSI are DISABLED** via `cluster.yaml` `addons` (bd babelstone-zla1.12.20); the k3s built-in
   `local-path` provisioner is the storage class. hetzner-k3s still plants an all-powerful Hetzner
   API token as the kube-system `hcloud` Secret on every create regardless of the addon toggles,
   so `provision.sh` **scrubs that orphaned Secret post-create** (no in-cluster Hetzner token remains).
2. Point DNS A records `app`, `api`, `backstage`, `auth`.`babelstone.dev` at the node IP
   (the four Ingress hosts: Mission Control, Kong, Backstage, and the Logto OIDC issuer). With the
   CCM off, `kubectl get nodes -o wide` shows **EXTERNAL-IP `<none>`** — that is **expected, not a
   fault** (nothing populates the node's external address without the cloud controller). Read the
   real server IP from Hetzner instead: `hcloud server ip <name>` — **not** the node object. A
   delete+create re-provision lands a **NEW Hetzner server IP**, so the A records must be
   re-pointed to it each time (again from `hcloud server ip <name>`).
3. Install the cluster add-ons (all under [`../k8s/overlays/staging/bootstrap/`](../k8s/overlays/staging/bootstrap/)).
   **Automated path:** run [`scripts/staging-bootstrap.sh`](../../scripts/staging-bootstrap.sh)
   (bd babelstone-zla1.12.23) — it does steps 3 and 4 here (the data-independent glue) fail-closed
   and idempotently, at **pinned** third-party versions (never `latest`). `--check-only` dry-runs it
   with no live cluster. The add-ons it installs:
   - **Traefik** (Helm, `bootstrap/helm/traefik-values.yaml`) — the ingress controller
     providing the `traefik` IngressClass, binding the node's :80/:443 (hostPort). hetzner-k3s
     disables the bundled Traefik + servicelb, so without this every `https://*.babelstone.dev`
     is dead (bd babelstone-zla1.14). See `bootstrap/README.md` step 1a.
   - **cert-manager** (Helm) — see `bootstrap/README.md`.
   - Rancher **system-upgrade-controller** (creates the `system-upgrade` namespace + SA the
     k3s upgrade Plan uses).
   - **Secrets Store CSI driver** (vendored, pinned v1.6.0 — `kubectl apply` of the openbao-csi
     component's `upstream/` files) **+ the HashiCorp vault-csi-provider** (Helm, csi-only) — the
     out-of-band half of the openbao-csi component that sources app-tier secrets from OpenBao
     (bd babelstone-zla1.12.21). Its CRDs/CSIDriver/DaemonSet land cluster-scoped in kube-system,
     NEVER in the strict overlay render. The `SecretProviderClass` custom resource — plus the
     `openbao` ServiceAccount + `openbao-auth-delegator` ClusterRoleBinding — are applied
     out-of-band at bootstrap too (bd babelstone-zla1.12.14.2: the least-privilege cd-deployer
     holds no grant on them), so `kustomize build overlays/staging` is fully `-strict`. See
     `bootstrap/README.md` (its `openbao-auth.yaml` entry + step 1c) and
     `../k8s/components/openbao-csi/README.md`.
   (The **external CSI snapshot controller** is no longer installed — the Hetzner CSI is dropped;
   bd babelstone-zla1.12.20 — so there is no `VolumeSnapshotClass` to back.)
4. `kubectl apply` the cluster-scoped bootstrap (the issuers and the k3s upgrade `Plan`). A blanket
   `kubectl apply -f infra/k8s/overlays/staging/bootstrap/` is now safe — the dead
   `volume-snapshot-class.yaml` (its CRD was gone with the Hetzner CSI) was removed in bd
   babelstone-zla1.12.24, so nothing in that glob references a missing CRD — or just run
   `staging-bootstrap.sh` (step 3 above), which applies the same set. The `helm/` subfolder is
   skipped by the non-recursive glob — those are Helm values, applied in step 3, not `kubectl apply`.
   Then **open inbound 80/443 on the Hetzner firewall and set Cloudflare TLS** (bd babelstone-zla1.14):
   `infra/hetzner-k3s/firewall-web.sh --apply` (Cloudflare-scoped; dry-runs without `--apply`),
   then set the Cloudflare SSL/TLS mode to **Full (strict)**. `cluster.yaml` can't express these
   web ports — see `bootstrap/README.md` steps 6–7 and `../hetzner-k3s/README.md` Posture notes.
5. Provision the real secrets — **required before the first deploy**: the staging render
   deliberately carries NO Secret bodies (bd babelstone-zla1.12.4 — the committed dev
   placeholders are dropped from the build, so a redeploy can never overwrite what you set
   here), and the deploy **fails closed** if `babelstone-dev-secrets` is missing or still
   holds a placeholder value (`scripts/cd-secret-preflight.sh`, wired into `cd.yml`).
   Create it once with real values (all ten keys are secretKeyRef'd by workloads):

   ```bash
   kubectl -n babelstone-staging create secret generic babelstone-dev-secrets \
     --from-literal=POSTGRES_PASSWORD="$(openssl rand -base64 24)" \
     --from-literal=OPENBAO_DEV_TOKEN="<real OpenBao token — never 'root'>" \
     --from-literal=SECRET_VAULT_KEK="$(openssl rand -base64 32)" \
     --from-literal=LOGTO_GRAFANA_CLIENT_SECRET="<the Logto grafana app client secret>" \
     --from-literal=LOGTO_MISSION_CONTROL_CLIENT_SECRET="<the Logto mission-control app client secret>" \
     --from-literal=MC_SESSION_SIGNING_KEY="$(openssl rand -base64 32)" \
     --from-literal=LOGTO_BACKSTAGE_CLIENT_SECRET="<the Logto backstage app client secret>" \
     --from-literal=BACKSTAGE_AUTH_SESSION_SECRET="$(openssl rand -base64 32)" \
     --from-literal=MC_READONLY_DB_PASSWORD="$(openssl rand -base64 24)"
   ```

   > **No `OIDC_PRIVATE_KEYS` is provisioned.** Logto **generates and owns** its own OIDC signing key
   > (`oidc.privateKeys`, created by `db seed`, persisted in Logto's DB, annually rotated in place) —
   > it is not injected. An operator-injected key broke the admin console (`id_token_signed_response_alg
   > must be 'ES256'`); a Logto-generated one works, and nothing else reads the OIDC signing key
   > (ADR-IC-021 amendment 2026-07-11, bd babelstone-zla1.10.16).

   `MC_READONLY_DB_PASSWORD` (bd zla1.17.3) is the password for the dedicated read-only Postgres
   role `babelstone_readonly` that Mission Control's Outbox·Inbox `/pg` lens connects as: the
   `mission-control-db-readonly` Job SETS the role's password from this key and the Mission Control
   Deployment reads it for the `/pg` DSNs — mint it with `openssl rand` as shown. To add it to an
   already-provisioned Secret without disturbing the other keys:
   `kubectl -n babelstone-staging patch secret babelstone-dev-secrets --type merge -p "{\"stringData\":{\"MC_READONLY_DB_PASSWORD\":\"$(openssl rand -base64 24)\"}}"`,
   then re-run the Job (`kubectl -n babelstone-staging delete job mission-control-db-readonly` +
   re-apply) and `kubectl -n babelstone-staging rollout restart deploy/mission-control`.

   `LOGTO_GRAFANA_CLIENT_SECRET`, `LOGTO_MISSION_CONTROL_CLIENT_SECRET`, and
   `LOGTO_BACKSTAGE_CLIENT_SECRET` come from **hand-registered Logto applications** (DCR is the
   accepted [ADR-IC-021](../../docs/product-management/integration_concepts/adrs/ADR-IC-021-iam-oauth-authorization-server.md)
   §C6 gap): register the Grafana app per that ADR's rollout step 3, the Mission Control app per
   [`mission-control-oidc-registration.md`](./mission-control-oidc-registration.md), and the Backstage
   app per [`backstage-oidc-registration.md`](./backstage-oidc-registration.md), then paste each client
   secret here. **Because Logto must be *running* before you can register these apps (step 7 / the
   Execution-order note above), seed these three keys with throwaway non-placeholder values now** — e.g.
   `LOGTO_GRAFANA_CLIENT_SECRET="TEMP-$(openssl rand -hex 12)"` — which passes `cd-secret-preflight.sh`
   (it rejects only the known `dev-placeholder-…` values), then replace them with the real secrets after
   registration (step 7). `MC_SESSION_SIGNING_KEY` and `BACKSTAGE_AUTH_SESSION_SECRET` are freshly generated HMAC
   keys (Mission Control / Backstage each sign their own session cookie with one — NOT a Logto value),
   so mint them with `openssl rand` as shown. Rotating one invalidates that app's live sessions.

   > **Keep these keys identical across both stores (cross-store consistency).** With the engine's
   > `OpenBao__Enabled=true`, `POSTGRES_PASSWORD` and `SECRET_VAULT_KEK` **also**
   > live in OpenBao KV (`secret/data/babelstone/{postgres,logto}`), seeded during OpenBao init (step 7).
   > Set them to the **same values** in both stores, or a rotation drifts into split-brain. In
   > particular, the password inside the engine's OpenBao connection string (`secret/data/Engine`) **MUST
   > equal this `POSTGRES_PASSWORD`**: Postgres is seeded from it and the engine's migration
   > init-container polls Postgres with it, so a mismatch makes the engine's *runtime* DB auth diverge
   > from what Postgres was created with. (The engine reads its three `OPENBAO_*` anchors from the
   > CSI-synced `babelstone-app-secrets`, not from `babelstone-dev-secrets` — see
   > [`../k8s/components/openbao-csi/README.md`](../k8s/components/openbao-csi/README.md).)

   Also provision `babelstone-backup-secret` (Hetzner Object Storage keys + bucket) and
   the Kong mTLS material (via `deck-sync`).
6. Deploy: `mise exec -- kustomize build --load-restrictor=LoadRestrictionsNone infra/k8s/overlays/staging | kubectl apply -f -`
   (or dispatch `cd.yml` with `overlay: staging`, which runs exactly this). **Do NOT use plain
   `kubectl apply -k`** — the `base` generates a ConfigMap from `../../kong/kong.yml` (above the
   kustomization root), so the build needs `--load-restrictor=LoadRestrictionsNone`, a flag
   `kubectl apply -k` (kubectl's embedded kustomize, default `LoadRestrictionsRootOnly`) cannot pass;
   it fails with "file …/infra/kong/kong.yml is not in or below infra/k8s/base". This brings up Postgres/Redpanda/Kong, the event-store migration Job, Logto
   (+ its seed jobs), OpenBao (**sealed/uninitialised**), and the engine (**NotReady** — its CSI mount
   can't resolve until step 7). Grafana/Mission-Control/Backstage come up but OIDC login stays broken
   until their real client secrets land (step 7).
7. Finish the wiring — **post-deploy**, once the step-6 pods are up (these two are independent of each
   other; Logto does not depend on OpenBao):
   - **Initialize OpenBao** (secret-zero — the big manual lift): `bao operator init` + unseal, enable
     the Kubernetes auth method, create the `babelstone-app` role/policy, populate the KV paths
     (`secret/data/babelstone/{postgres,logto,engine-approle,engine-transit}` **and** `secret/data/Engine`),
     and `bao secrets enable transit`. Full ordered commands + the single-node manual-unseal caveat are
     in [`../k8s/components/openbao-csi/README.md`](../k8s/components/openbao-csi/README.md) "Live apply +
     init". Then `kubectl -n babelstone-staging rollout restart deploy/engine` so its CSI mount + AppRole
     login resolve and the engine goes Ready.
   - **Register the 3 Logto apps + seed the real client secrets.** At `https://auth-admin.babelstone.dev`
     (**not** a `kubectl port-forward` — Logto 401s a mismatched issuer) register Grafana / Mission
     Control / Backstage per the runbooks in step 5, then merge-patch the three real `LOGTO_*_CLIENT_SECRET`
     values over the step-5 placeholders, pin any newly-minted client IDs into the manifests (the
     `configure-logto` job fails loud on a mismatch), and
     `kubectl -n babelstone-staging rollout restart deploy/mission-control deploy/grafana-lgtm deploy/backstage`.

### 1.1 Phase 1 — provision the cluster (`hetzner-k3s`)

Step 1 above is one `hetzner-k3s` command. The cluster config lives at
[`../hetzner-k3s/cluster.yaml`](../hetzner-k3s/cluster.yaml) (1× CPX42 x86, Helsinki `hel1`,
single-node k3s); the full walk-through + prereqs are in
[`../hetzner-k3s/README.md`](../hetzner-k3s/README.md). In short:

```bash
cd infra/hetzner-k3s
export HCLOUD_TOKEN=<read/write Hetzner Cloud API token>   # never commit; takes precedence over the config
export SSH_ALLOWED_CIDR=<your operator IP>/32              # REQUIRED — provision.sh refuses REPLACE_ME / 0.0.0.0/0 / non-/32
# pin a valid `hetzner-k3s releases` version in cluster.yaml, then:
./provision.sh    # fail-closed SSH-allow-list preflight → renders cluster.rendered.yaml → `hetzner-k3s create`
                  # (creates the node + k3s, CCM/CSI DISABLED via addons + local-path enabled; then
                  #  SCRUBS the orphaned kube-system/hcloud token Secret; writes ./kubeconfig — bd babelstone-zla1.12.6/.20)
```

The generated `./kubeconfig` is a cluster-admin credential — **gitignored, never committed**, and
**operator-only** (bootstrap + break-glass). It is *not* what `cd.yml` deploys with: the
`KUBECONFIG_B64` environment secret must carry the least-privilege `cd-deployer` ServiceAccount
kubeconfig instead (bd babelstone-zla1.12.1) — apply
`infra/k8s/overlays/staging/bootstrap/cd-deploy-rbac.yaml` at Phase 2, then mint it with
`scripts/cd-kubeconfig.sh` (walk-through in `bootstrap/README.md`; `cd.yml` probes and refuses a
cluster-admin credential at apply time). The Hetzner CCM + CSI are **disabled** (bd babelstone-zla1.12.20):
the k3s built-in **`local-path`** provisioner is the storage class the staging overlay's stateful
claims bind (`storageClassName: local-path`) — node-local, no cloud API call, no `hcloud-volumes`.
Because hetzner-k3s plants the kube-system `hcloud` token Secret on every create regardless of the
addon toggles, `provision.sh` scrubs it post-create, so no Hetzner API token remains in the cluster.
Then continue with step 2 (DNS) → step 3 (`bootstrap/`, Phase 2) above.

## 2. Redeploy / promote a new build

`cd.yml` (workflow_dispatch, `overlay: staging`, `apply: true`) cosign-verifies the images by
digest, gates the forward-only migrations, renders + kubeconforms the overlay, applies it,
`deck sync`s Kong, and finally reconciles the first-party Logto config (the `configure-logto`
job). The event-store migration Job + the engine initContainer handle schema ordering
automatically (the engine waits on the migration sentinel).

**Logto Management-API config is now pipeline-driven (bd babelstone-zla1.10.x).** The three
`scripts/iam/*.py` reproduce-path scripts — the MCP API resource + scopes, the ops-console client,
and default-tenant MFA — are **no longer run by hand** after a deploy. The `configure-logto` job
runs them idempotently on every staging promote, so a Logto re-onboard self-heals on the next
deploy instead of leaving the ADR-IC-021 C1/C5/C4/C7 substrate silently wiped. It also runs
**standalone**: dispatch `cd.yml` with `apply: false, configure_logto: true` to re-heal a
hand-re-onboarded Logto **without** re-promoting images. It **fails loud** if the Logto-registered
ops-console App ID drifts from the deployed `OIDC_CLIENT_ID` (`mission-control.yaml`; see
[`mission-control-oidc-registration.md`](./mission-control-oidc-registration.md) §1.3).

One-time prerequisite (Phase 2, like the deploy kubeconfig): seed the deploy-scoped `babelstone-mgmt`
M2M app's credentials as the `p6-staging` environment secrets `LOGTO_MGMT_APP_ID` /
`LOGTO_MGMT_APP_SECRET` (client_credentials against `…/api`, **never** the root token — same
secrets discipline as §1 step 5). `prove-refresh-family-revoke.py` stays a **manual** verification
(a timing-sensitive proof, not config) and is deliberately **not** in the build lane.

## 3. Restore

Two independent recovery paths (use whichever fits the incident):

**A. Logical (portable, off-box) — the `db-logical-backup` CronJob.** Daily dumps live in
Hetzner Object Storage at `s3://$S3_BUCKET/postgres/all-*.sql.gz`. To restore the main cluster:
```bash
aws --endpoint-url "$S3_ENDPOINT" s3 cp s3://$S3_BUCKET/postgres/all-<TS>.sql.gz - \
  | gunzip | psql -h postgres -U babelstone -d postgres   # pg_dumpall output recreates each DB
```
Backstage is NOT backed up: it uses in-memory SQLite (no Postgres) and rebuilds its catalogue
from the baked `/catalog` tree on every boot (bd babelstone-zla1.6.6), so there is nothing to
restore — a fresh pod is already fully populated.

**B. Block-level — CSI VolumeSnapshots** (the `volume-snapshot` CronJob). **RETIRED on staging /
production-topology-only** — the Hetzner CSI is dropped and staging now runs on the node-local
`local-path` provisioner (bd babelstone-zla1.12.20), which has no `VolumeSnapshot` support, so this
path does not apply here. On the production-shaped topology (which keeps the CSI) it still holds:
list the snapshots (`kubectl get volumesnapshot`), then provision a new PVC `dataSource:` that
VolumeSnapshot and re-point the StatefulSet/Deployment — faster for a whole-volume rollback (incl.
Redpanda state), but same-cluster only. **On staging, path A (the logical pg_dumpall) is the only
restore path**, and it is off-box.

## 4. Upgrade (k3s)

The `k3s-server` `Plan` (system-upgrade-controller, `bootstrap/k3s-upgrade-plan.yaml`) follows
the k3s `stable` channel. On a single node an upgrade **cordons + drains the one node**, so there
is a brief downtime window — expect it, schedule off-hours. To upgrade on demand, bump/re-apply
the Plan or annotate it; the controller does the in-place binary swap. App pods reschedule once
the node is back `Ready`.

## 5. Backups — what runs, when

| Job | Schedule (UTC) | What | Where |
|---|---|---|---|
| `db-logical-backup` | 01:30 daily | `pg_dumpall` (engine + orchestrator DBs) | Hetzner Object Storage (S3) |
| ~~`volume-snapshot`~~ | ~~02:30 daily~~ | ~~CSI `VolumeSnapshot` of postgres / redpanda PVCs~~ | **RETIRED (production-topology-only)** |

The `volume-snapshot` CronJob is **retired on staging** — it needs the Hetzner CSI, which is dropped
(bd babelstone-zla1.12.20); staging is on the node-local `local-path` provisioner. Only the logical
`db-logical-backup` runs here; the block-snapshot row applies solely to the production-shaped
topology that keeps the CSI.

Check: `kubectl get cronjob,job` and the job logs. **Retention/pruning is manual for v1** — prune
old S3 objects (lifecycle policy on the bucket) periodically. (On the production topology, also prune
old `VolumeSnapshot`s: `kubectl delete volumesnapshot -l babelstone.io/dr-role=volume-snapshot
--field-selector ...` — not applicable on staging, no CSI.)

## 6. Uptime / alerting

- **End-to-end uptime = an external HTTP pinger** (e.g. UptimeRobot / healthchecks.io, free tier)
  on `https://app.babelstone.dev/` (and `https://api.babelstone.dev/`). This is the authoritative
  "is the box reachable" signal — it tests DNS + TLS + ingress + app from outside the cluster.
  Configure it during Phase 0/2 (account-gated; no in-cluster manifest).
- **In-cluster signal**: the `EngineMetricsAbsent` rule (`infra/grafana/prometheus/alert-rules.yaml`,
  `staging-liveness` group) fires when the engine stops emitting its SLI metric. This rule file is
  now loaded into the staging k8s grafana-lgtm appliance (`rule_files:`) — the staging overlay
  generates a `grafana-lgtm-rules` ConfigMap from `infra/grafana/prometheus/{prometheus,alert-rules}.yaml`
  and subPath-mounts both into the appliance at `/otel-lgtm/` (bd babelstone-zla1.9). The external
  pinger remains the authoritative end-to-end check; the in-cluster rule is the complementary
  "engine is reporting" signal.

## 7. Common checks

```bash
kubectl -n babelstone-staging get pods,svc,ingress,cronjob
kubectl -n babelstone-staging get certificate          # cert-manager TLS health (needs the CRDs)
kubectl -n babelstone-staging logs deploy/engine       # app logs

# Public edge health (bd babelstone-zla1.14). If any host returns 000, work outward:
#   Traefik pod up?  →  kubectl -n traefik get pods,ingressclass    (IngressClass 'traefik' present)
#   Ingress exists?  →  kubectl -n babelstone-staging get ingress   (ADDRESS is EXPECTED empty with hostPort — not a fault)
#   Firewall open?   →  hcloud firewall describe babelstone-staging (inbound TCP 80/443 present)
#   Direct origin?   →  curl -sk --resolve app.babelstone.dev:443:<node-ip> https://app.babelstone.dev/ -o /dev/null -w '%{http_code}\n'
for h in app api auth; do
  echo -n "$h.babelstone.dev → "; curl -s -o /dev/null -w '%{http_code}\n' "https://$h.babelstone.dev/"
done   # expect real (non-000, non-5xx) codes end-to-end through Cloudflare with valid TLS
```

### 7.1 Logto crashloops with "Row-level security has to be enforced" (bd babelstone-zla1.12.34)

In plain English: if the `logto` pod crashloops at boot complaining that `_logto_configs` has no
row-level security, its `logto` database was bootstrapped down the wrong path and carries a stray
**legacy** `_logto_configs` table that modern Logto rejects. The durable fix is already in
`logto-jobs.yaml` (the `wait-for-logto-seed` init gates the alteration Job on the seed completing, so
the race that creates the stray table can't happen). To recover a box that is *already* in the broken
state, do a clean reseed — note the clean-up must drop **cluster-global roles** too, which survive a
`DROP DATABASE`:

```bash
# Confirm the failure mode: the ONLY tables without RLS should be systems + service_logs; a stray
# `_logto_configs` here is the fault.
kubectl -n babelstone-staging exec postgres-0 -- psql -U babelstone -d logto -tAc \
  "SELECT tablename FROM pg_catalog.pg_tables WHERE schemaname=current_schema() AND rowsecurity=false"

# Quick fix (keeps the seeded data): drop just the stray legacy table, then restart Logto.
kubectl -n babelstone-staging exec postgres-0 -- psql -U babelstone -d logto -c "DROP TABLE IF EXISTS _logto_configs"
kubectl -n babelstone-staging rollout restart deploy/logto

# Full clean reseed (from scratch): scale Logto down, delete the two DB Jobs, then drop the DB AND the
# cluster-global tenant roles (they survive DROP DATABASE), and re-apply the overlay so the guarded
# init/seed + alteration Jobs rebuild a clean schema.
kubectl -n babelstone-staging scale deploy/logto --replicas=0
kubectl -n babelstone-staging delete job logto-db-init logto-db-alteration --ignore-not-found
kubectl -n babelstone-staging exec postgres-0 -- psql -U babelstone -d babelstone \
  -c "DROP DATABASE IF EXISTS logto WITH (FORCE)" \
  -c "DROP ROLE IF EXISTS logto_tenant_logto" \
  -c "DROP ROLE IF EXISTS logto_tenant_logto_admin" \
  -c "DROP ROLE IF EXISTS logto_tenant_logto_default"
mise exec -- kustomize build --load-restrictor=LoadRestrictionsNone infra/k8s/overlays/staging | kubectl apply -f -
```

## 8. Public edge security posture (bd babelstone-zla1.10.6)

In plain English: the two most sensitive UIs — the Logto **admin console**
(`auth-admin.babelstone.dev`, the IAM control plane) and **Grafana**
(`grafana.babelstone.dev`, the regulated observability plane) — sit behind several
independent gates, so no single failure exposes them. The other public hosts
(`app`/`api`/`backstage`) rely on Cloudflare + their own app auth only.

**The four layers protecting `auth-admin` and `grafana` (verified 2026-07-08):**

1. **Hetzner firewall** — inbound `:80`/`:443` is scoped to Cloudflare's published IP
   ranges (`infra/hetzner-k3s/firewall-web.sh`), **not** `0.0.0.0/0`. A direct hit on the
   node IP from any other address times out. Verify:
   ```bash
   hcloud firewall describe babelstone-staging   # tcp/80 + tcp/443 sources = CF ranges, never 0.0.0.0/0
   curl -sk -m8 --resolve auth-admin.babelstone.dev:443:<node-ip> https://auth-admin.babelstone.dev/ -o /dev/null -w '%{http_code}\n'
   # expect 000/timeout from a non-Cloudflare IP
   ```
2. **Cloudflare Access** — an identity gate (email-code / IdP) in front of both hosts.
   Unauthenticated requests 302 to `<team>.cloudflareaccess.com/cdn-cgi/access/login/…`
   **before** the app loads. Verify (no Access session):
   ```bash
   curl -s -o /dev/null -w '%{http_code} %{redirect_url}\n' https://auth-admin.babelstone.dev/
   curl -s -o /dev/null -w '%{http_code} %{redirect_url}\n' https://grafana.babelstone.dev/
   # expect 302 -> …cloudflareaccess.com/cdn-cgi/access/login/…  (NOT the app)
   ```
   Keep the OIDC **issuer** `auth.babelstone.dev` UNGATED (Grafana SSO + the Logto
   Management API depend on it): `curl …/oidc/.well-known/openid-configuration` → 200.
3. **App login** — Logto admin login **+ 2FA** on the console; Grafana login + Logto SSO
   + §P6 RBAC (anonymous OFF) on Grafana.
4. **`ADMIN_DISABLE_LOCALHOST=true`** on Logto (`logto.yaml`) — the admin console is
   reachable only via its real `ADMIN_ENDPOINT` host, never a bare `localhost` origin.
   **Operational consequence — do not "fix" it back:** this flag stops Logto binding
   the separate admin port (3002) at all; the console is served on the *core* listener
   (3001), routed to the admin tenant by the `auth-admin` Host. So the `logto-admin`
   Ingress **must** front `logto:3001`, not `3002` — pointing it at 3002 hits a closed
   port and every post-Access request returns **502** (bd babelstone-zla1.10.10, the
   regression that shipped with this hardening). Verify end-to-end *through* Access:
   ```bash
   # with a valid Cloudflare Access session cookie, expect 200 (was 502 when Ingress → 3002)
   kubectl -n babelstone-staging get ingress logto-admin \
     -o jsonpath='{.spec.rules[0].http.paths[0].backend.service.port.number}'   # -> 3001
   kubectl -n babelstone-staging exec deploy/logto -- \
     sh -c 'wget -qO- --header="Host: auth-admin.babelstone.dev" http://127.0.0.1:3001/ >/dev/null && echo core-serves-admin-OK'
   ```

**Resolved — the Cloudflare origin bypass, closed by a Cloudflare Tunnel (bd babelstone-zla1.12.14).**
The hole: because the firewall had to allow *all* Cloudflare IPs on inbound `:80`/`:443`, an
attacker who discovered the origin IP could proxy through their *own* Cloudflare zone with a
spoofed `Host` header, reaching the origin from a CF IP and bypassing the babelstone.dev edge
— including Cloudflare Access. (They still hit each app's own login + 2FA, so it was **not**
access, only loss of the edge layer + exposure of the pre-auth surface. Risk was **LOW on
staging** — targeted-only; origin-IP discovery the limiter, though `:6443` is world-open and
reveals a live cluster — rising to **MEDIUM in production**. Cluster-wide: every public host.)
The fix: a **Cloudflare Tunnel** (`cloudflared`,
[`bootstrap/cloudflare-tunnel.yaml`](../k8s/overlays/staging/bootstrap/cloudflare-tunnel.yaml))
now runs in-cluster and dials **outbound** to Cloudflare, so the inbound web ports are removed
entirely ([`firewall-web.sh`](../hetzner-k3s/firewall-web.sh) no longer adds them and removes
the two `cloudflare-web-*` rules — tunnel up first, ports down second). With **no inbound origin
web port**, there is nothing left to spoof, closing the bypass for all six public hosts at once.
(Authenticated Origin Pulls — Traefik requiring Cloudflare's origin client cert — was the lighter
alternative considered; it leaves the ports open-but-mTLS-gated rather than removed, so the Tunnel
was chosen.) This satisfies the **MUST-before-production-promotion gate** on the
parent epic (bd babelstone-zla1.12.14). **Human residual (account-gated):** create the tunnel + connector token in the
Cloudflare Zero Trust dashboard, apply the manifest, point the CNAMEs at the tunnel, run
`firewall-web.sh --apply`, then verify a spoofed-`Host` request to the origin IP (the node's
public IP, resolved at run time — never hardcode it) is refused host-by-host — see
[`bootstrap/README.md`](../k8s/overlays/staging/bootstrap/README.md) "Apply order" steps 6-8.
