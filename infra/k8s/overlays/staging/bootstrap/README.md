# staging bootstrap — cluster-scoped, account-gated, applied ONCE

Plain English: these are the one-time cluster setup pieces for the always-on
staging box that can't live in the Kustomize overlay — either because they are
cert-manager CRDs (and the CI gate, `kubeconform -strict`, carries no CRD schemas,
so a CRD in the `kustomize build` output would hard-fail it), or because they are
privilege-granting setup a routine deploy must not be able to self-apply (the CD
deploy RBAC). They are applied by hand (or by the Phase-2 provisioning script)
once the cluster and DNS exist.

This directory is **deliberately not referenced** by
[`../kustomization.yaml`](../kustomization.yaml), so it never enters
`kustomize build infra/k8s/overlays/staging`.

## Contents

- [`helm/traefik-values.yaml`](./helm/traefik-values.yaml) — Helm **values** for the
  Traefik ingress controller that provides the `traefik` `IngressClass` the overlay's
  [`../ingress.yaml`](../ingress.yaml) references (bd babelstone-zla1.14). hetzner-k3s
  ships k3s with the *bundled* Traefik + servicelb **disabled**, so without this there is
  no ingress controller and every `https://*.babelstone.dev` URL is dead. On a single node
  with no LoadBalancer, Traefik binds the node's `:80`/`:443` directly via **hostPort**.
  It lives in the `helm/` subfolder — not the top level — precisely so the
  `kubectl apply -f bootstrap/` step below (non-recursive) never tries to `kubectl apply` a
  Helm values file; it is consumed by `helm install -f` (see apply order), never `kubectl`.
- [`clusterissuer-letsencrypt.yaml`](./clusterissuer-letsencrypt.yaml) — the
  Let's Encrypt `ClusterIssuer` the overlay's Ingress references by name
  (`cert-manager.io/cluster-issuer: letsencrypt-babelstone`). cert-manager's
  ingress-shim provisions per-host `Certificate`s from the Ingress `tls:` blocks.
- [`mcp-mtls.yaml`](./mcp-mtls.yaml) — the **internal** mutual-TLS chain for the
  Kong↔mcp-server hop (bd babelstone-zla1.5.3): a self-signed Issuer → an internal
  CA → a CA Issuer → the mcp-server **server** cert (`babelstone-mcp-tls`, mounted
  by the mcp-server Deployment) **and** Kong's **client** cert (`mcp-kong-client-tls`),
  both chaining to the one CA. Distinct from the public Let's Encrypt edge above —
  this is internal east-west mTLS, never public.
- [`volume-snapshot-class.yaml`](./volume-snapshot-class.yaml) — the
  `VolumeSnapshotClass` (`hcloud-volumes`, driver `csi.hetzner.cloud`) the
  volume-snapshot CronJob references (bd babelstone-zla1.7). Needs the **external CSI
  snapshot controller + CRDs** installed (NOT bundled with k3s).
- [`k3s-upgrade-plan.yaml`](./k3s-upgrade-plan.yaml) — the system-upgrade-controller
  `Plan` tracking the k3s `stable` channel (bd babelstone-zla1.7). Needs Rancher's
  **system-upgrade-controller** installed (it creates the `system-upgrade` namespace +
  ServiceAccount the Plan uses).
- [`cd-deploy-rbac.yaml`](./cd-deploy-rbac.yaml) — the least-privilege **CD deploy
  identity** (bd babelstone-zla1.12.1): the `cd-deployer` ServiceAccount + its
  long-lived token Secret, a namespaced Role covering exactly the kinds
  `kustomize build overlays/staging` renders, and a name-scoped ClusterRole for the
  Namespace object in the render. After applying it, mint the deploy kubeconfig with
  [`scripts/cd-kubeconfig.sh`](../../../../../scripts/cd-kubeconfig.sh) and store it
  as `cd.yml`'s `KUBECONFIG_B64` environment secret — **never** the hetzner-k3s
  cluster-admin kubeconfig (`cd.yml` probes and refuses a cluster-admin credential
  at apply time). Lives here, not in the overlay, so a routine deploy can never
  widen its own grant.

## Apply order (Phase 2 — needs the live cluster + DNS)

```bash
# 1. Install cert-manager (with its CRDs) — Helm is the upstream-recommended path.
helm repo add jetstack https://charts.jetstack.io && helm repo update
helm install cert-manager jetstack/cert-manager \
  --namespace cert-manager --create-namespace --set crds.enabled=true

# 1a. Install the Traefik ingress controller (bd babelstone-zla1.14). hetzner-k3s disables
#     the bundled Traefik + servicelb, so this is what provides the `traefik` IngressClass and
#     binds the node's :80/:443 (hostPort). Without it every https://*.babelstone.dev is dead.
helm repo add traefik https://traefik.github.io/charts && helm repo update
helm install traefik traefik/traefik \
  --namespace traefik --create-namespace \
  -f infra/k8s/overlays/staging/bootstrap/helm/traefik-values.yaml

# 1b. Install the controllers the zla1.7 ops bootstrap depends on:
#   - the external CSI snapshot controller + its CRDs (VolumeSnapshot/Class/Content) —
#     NOT bundled with k3s; install the upstream kubernetes-csi/external-snapshotter manifests.
#   - Rancher's system-upgrade-controller (creates the `system-upgrade` ns + SA the Plan uses).
kubectl apply -f https://github.com/rancher/system-upgrade-controller/releases/latest/download/system-upgrade-controller.yaml

# 2. Create the namespace (idempotent) — the namespaced bootstrap objects (mcp-mtls certs,
#    the cd-deployer RBAC) land in it, and the CD ServiceAccount may only CONVERGE the
#    namespace, not create it (RBAC `create` cannot be name-restricted).
kubectl create namespace babelstone-staging --dry-run=client -o yaml | kubectl apply -f -

# 3. Apply the cluster-scoped bootstrap (this directory): issuers, VolumeSnapshotClass,
#    k3s Plan, and the cd-deployer deploy RBAC.
kubectl apply -f infra/k8s/overlays/staging/bootstrap/

# 4. Mint the least-privilege deploy kubeconfig (identity: the cd-deployer SA) and store it
#    as the KUBECONFIG_B64 environment secret — NOT the cluster-admin kubeconfig.
scripts/cd-kubeconfig.sh -o /tmp/cd-deployer.kubeconfig
base64 < /tmp/cd-deployer.kubeconfig   # → GitHub environment secret KUBECONFIG_B64
rm -f /tmp/cd-deployer.kubeconfig

# 5. Apply the overlay itself (cd.yml does this on promote, using the cd-deployer identity).
kubectl apply -k infra/k8s/overlays/staging

# 6. Open inbound TCP 80/443 on the Hetzner firewall (bd babelstone-zla1.14). cluster.yaml's
#    allowed_networks can only express ssh/api, so the web ports are added out-of-band, scoped
#    to Cloudflare's ranges (only the proxy reaches the origin). DRY-RUN first, then --apply:
export HCLOUD_TOKEN=...                       # same token used to provision
infra/hetzner-k3s/firewall-web.sh             # prints the rules it WOULD add
infra/hetzner-k3s/firewall-web.sh --apply     # adds inbound TCP 80 + 443 (Cloudflare-scoped)

# 7. In the Cloudflare dashboard, set SSL/TLS mode to "Full (strict)" for babelstone.dev — the
#    origin now presents a valid Let's Encrypt cert, so the proxy re-encrypts end-to-end.
```

Prereqs: DNS A records `api.babelstone.dev`, `backstage.babelstone.dev`,
`app.babelstone.dev`, and `auth.babelstone.dev` (the four Ingress hosts) resolve (proxied
is fine), and a `cloudflare-api-token` Secret (scoped `Zone.DNS:Edit` for `babelstone.dev`)
exists in the `cert-manager` namespace for the DNS-01 ACME challenge. Certs are issued via
DNS-01, so the A records may stay behind the Cloudflare proxy. **The Hetzner firewall must
also allow inbound 80/443** — hetzner-k3s synthesises the firewall from `cluster.yaml`'s
`allowed_networks`, which only models `ssh`/`api`, so those web ports are provisioned
out-of-band by [`firewall-web.sh`](../../../../hetzner-k3s/firewall-web.sh) (step 6 above;
Cloudflare-scoped, and the list *rotates* — re-run after Cloudflare publishes range changes).
The full provision/restore/upgrade runbook is Phase 6 (bd babelstone-zla1.7).

**Kong↔mcp-server mTLS swap (after `mcp-mtls.yaml` is applied).** The committed
`infra/kong/kong.yml` carries only POC placeholder certs for the mcp-server upstream
(`client_certificate` + `ca_certificates`, with `tls_verify: false`). At deploy,
`scripts/deck-sync.sh` renders Kong with the **real** internal-CA material — Kong's
client cert from `mcp-kong-client-tls` and its `ca_certificates` from the internal CA
(`mcp-mtls-ca`'s `ca.crt`) — and flips `tls_verify: true`. The same CA underwrites
both sides, so uvicorn's `CERT_REQUIRED` verify and Kong's server-cert verify both pass.
No real cert/key material is ever committed.
