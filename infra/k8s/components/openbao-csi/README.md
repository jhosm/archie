# `openbao-csi` — source app-tier secrets from OpenBao (Secrets Store CSI)

Plain English: this folder is a self-contained kustomize **component** that lets
application pods read their secrets straight from OpenBao instead of from a plain
Kubernetes Secret sitting in etcd. It bundles the upstream **Secrets Store CSI
driver** (vendored, pinned) and a **SecretProviderClass** that maps the app-tier
secrets — the Postgres password, the Logto KEK + OIDC signing key, and the
engine's own OpenBao-auth anchor — out of OpenBao via its Kubernetes auth method.
It pairs with the persistent (raft) OpenBao in
[`../../base/openbao.yaml`](../../base/openbao.yaml) and the engine's
`OpenBao__Enabled=true` flip in [`../../apps/engine.yaml`](../../apps/engine.yaml)
(bd babelstone-zla1.12.13, ADR-PC-004 §A1).

## What is here

| File | What it is |
|---|---|
| [`kustomization.yaml`](./kustomization.yaml) | The component (`kind: Component`). Lists the vendored driver install + the SecretProviderClass. |
| [`secret-provider-class.yaml`](./secret-provider-class.yaml) | The babelstone `SecretProviderClass` (`provider: vault`) — maps OpenBao KV paths → files + a synced k8s Secret (`babelstone-app-secrets`). |
| [`upstream/`](./upstream/) | The upstream **Secrets Store CSI driver** manifests, **vendored and pinned to `v1.6.0`** (`kubernetes-sigs/secrets-store-csi-driver`): the two CRDs, the `CSIDriver`, the node `DaemonSet`, and its RBAC. Cluster-scoped, lands in `kube-system`. |

**Provider note.** OpenBao is Vault-API-compatible, so the SecretProviderClass
uses `provider: vault` and the upstream **HashiCorp vault-csi-provider** talks to
it unchanged. The base Secrets Store CSI driver is provider-agnostic; the vault
provider is a **separate cluster-scoped install** — see *Apply order* below. Only
the base driver is vendored here (the vault provider install is out-of-band,
exactly like cert-manager / the CSI snapshot controller in
[`../../overlays/staging/bootstrap/`](../../overlays/staging/bootstrap/README.md)).

## How it splits across the staging overlay and the out-of-band bootstrap

The driver install carries **CRDs** (`SecretProviderClass`,
`SecretProviderClassPodStatus`) and a **`CSIDriver`**, and the SecretProviderClass
itself is a **custom resource** — none of which the CI `kubeconform -strict` gate
has a vendored schema for. Dropping the whole component into the staging overlay
would break that gate. So the wiring is split (bd babelstone-zla1.12.21):

1. The **driver install** lands in the out-of-band bootstrap layer (`kubectl apply`
   of the vendored `upstream/` files + the vault-csi-provider Helm chart, like
   cert-manager), so the CRDs/`CSIDriver` never enter `kustomize build
   overlays/staging`. See [`../../overlays/staging/bootstrap/README.md`](../../overlays/staging/bootstrap/README.md)
   step 1c and `scripts/staging-bootstrap.sh`.
2. The **SecretProviderClass alone** is applied out-of-band at bootstrap too — with the
   operator's cluster-admin kubeconfig, NOT rendered into the overlay (the least-privilege
   `cd-deployer` holds no `secretproviderclasses` grant, bd babelstone-zla1.12.14.2):

   ```bash
   # scripts/staging-bootstrap.sh STEP 5b
   kubectl apply -n babelstone-staging \
     -f infra/k8s/components/openbao-csi/secret-provider-class.yaml
   ```

   Because the `SecretProviderClass` (the only kubeconform-schema-less Kind) is no longer in
   any overlay render, the strict gate needs **no** `-ignore-missing-schemas` for the routine
   renders — `base`/`ha`/`staging` are all fully `-strict`. (The standalone component validate
   below still uses the flag, since building the component directly does render the CR.)

> **Sizing follow-up (bd babelstone-zla1.12.21).** The persistent OpenBao container
> in [`../../base/openbao.yaml`](../../base/openbao.yaml) carries **no memory
> limit** today. When this component is registered in the staging overlay, add an
> `openbao` container memory limit in `overlays/staging/resources.patch.yaml` — the
> staging sizing fitness-check in `ci.yml` requires every workload container to
> carry one.

### Validate standalone

```bash
mise exec -- kustomize build infra/k8s/components/openbao-csi \
  | mise exec -- kubeconform -strict -ignore-missing-schemas -summary \
      -kubernetes-version 1.31.0 \
      -schema-location 'infra/k8s/schemas/{{.NormalizedKubernetesVersion}}-standalone-strict/{{.ResourceKind}}{{.KindSuffix}}.json'
```

The CRDs, `CSIDriver`, `DaemonSet`, RBAC, and the `SecretProviderClass` are
*Skipped* (no vendored schema); the `ServiceAccount` validates. `-strict` still
catches structural drift on the Kinds it does know.

## ⚠️ Single-node MANUAL-UNSEAL caveat (READ before a node drain / k3s upgrade)

The staging box is a **single k3s node with no Hetzner KMS**, so this slice ships
OpenBao **without auto-unseal** (no `seal` stanza in
[`../../base/openbao-config.yaml`](../../base/openbao-config.yaml)). Consequences:

- OpenBao boots **SEALED** after every restart. A **k3s node drain / auto-upgrade**
  reboots the node → the OpenBao pod restarts sealed → **every dependent stays
  unavailable** until an operator unseals it: the engine's PII boundary
  (`OpenBao__Enabled=true`) and any pod whose CSI mount sources a secret from
  OpenBao.
- **Unseal is manual.** The unseal key shares + the initial root token are
  **secret-zero** — they live **outside** the cluster (an operator's password
  manager / offline store), never in OpenBao and never committed.

### Ordered restart after a node event

1. **Unseal OpenBao first:**
   ```bash
   kubectl -n babelstone-staging exec deploy/openbao -- \
     bao operator unseal <key-share-1>   # repeat for the required threshold
   kubectl -n babelstone-staging exec deploy/openbao -- bao status   # Sealed: false
   ```
2. **Then bounce the dependents** so their secret mounts / AppRole logins resolve:
   ```bash
   kubectl -n babelstone-staging rollout restart deploy/engine
   # …and any other pod mounting the babelstone-app-secrets SecretProviderClass
   ```

Auto-unseal (Transit/KMS) + raft-snapshot DR (ADR-PC-005 §P4) are a **later
slice** — out of scope here — because they need a real key-management anchor the
single-node box does not yet have.

## Live apply + init (account-gated operator steps — NOT run from CI)

These need the live cluster and produce **secret-zero**; they are the human
residual (also in the PR's Live-apply checklist). Static validation only lands in
CI.

1. **Install the base CSI driver** (this component's `upstream/`, or the upstream
   Helm chart) — cluster-scoped, `kube-system`.
2. **Install the vault-csi-provider** (out-of-band) so `provider: vault` resolves.
3. **Initialise + unseal OpenBao** (`bao operator init`, then `bao operator
   unseal` × threshold). Store the unseal shares + root token as secret-zero,
   **out of the cluster**.
4. **Enable the Kubernetes auth method** and bind the app ServiceAccount(s) to a
   `babelstone-app` role over a read-only policy on `secret/data/babelstone/*`
   (the `roleName`/`audience` the SecretProviderClass uses).
5. **Populate the KV paths** the SecretProviderClass reads (values NEVER
   committed):
   - `secret/data/babelstone/postgres` → `password`
   - `secret/data/babelstone/logto` → `secret_vault_kek`, `oidc_private_keys`
   - `secret/data/babelstone/engine-approle` → `role_id`, `secret_id`
   - `secret/data/babelstone/engine-transit` → `token`
   - `secret/data/Engine` → `Engine` (the engine's DB connection string — the
     engine's `OpenBaoKvSecretProvider` reads this directly)
6. **Enable the transit engine** (`bao secrets enable transit`) for the
   per-subject crypto-shred boundary (ADR-PC-004).
7. **Deploy the staging overlay** and let the engine roll. The overlay already
   registers the `SecretProviderClass` (the `resources:` reference above), so no
   extra registration step is needed here — only the out-of-band driver install
   (steps 1–2) and OpenBao init (steps 3–6). The engine pod goes Ready once its
   OpenBao anchor + `secret/data/Engine` resolve.

## Bumping the vendored driver

Re-vendor from the pinned upstream tag, then re-validate:

```bash
V=v1.6.0   # bump this
BASE="https://raw.githubusercontent.com/kubernetes-sigs/secrets-store-csi-driver/$V/deploy"
for f in \
  secrets-store.csi.x-k8s.io_secretproviderclasses.yaml \
  secrets-store.csi.x-k8s.io_secretproviderclasspodstatuses.yaml \
  rbac-secretproviderclass.yaml rbac-secretprovidersyncing.yaml \
  csidriver.yaml secrets-store-csi-driver.yaml ; do
  curl -sSfL "$BASE/$f" -o "infra/k8s/components/openbao-csi/upstream/$f"
done
mise exec -- kustomize build infra/k8s/components/openbao-csi >/dev/null   # must build
```
