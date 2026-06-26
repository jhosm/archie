# staging bootstrap — cluster-scoped, account-gated, applied ONCE

Plain English: these are the one-time cluster setup pieces for the always-on
staging box that can't live in the Kustomize overlay — because they are
cert-manager CRDs, and the CI gate (`kubeconform -strict`) carries no CRD schemas,
so a CRD in the `kustomize build` output would hard-fail it. They are applied by
hand (or by the Phase-2 provisioning script) once the cluster and DNS exist.

This directory is **deliberately not referenced** by
[`../kustomization.yaml`](../kustomization.yaml), so it never enters
`kustomize build infra/k8s/overlays/staging`.

## Contents

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

## Apply order (Phase 2 — needs the live cluster + DNS)

```bash
# 1. Install cert-manager (with its CRDs) — Helm is the upstream-recommended path.
helm repo add jetstack https://charts.jetstack.io && helm repo update
helm install cert-manager jetstack/cert-manager \
  --namespace cert-manager --create-namespace --set crds.enabled=true

# 2. Apply the cluster-scoped issuer (this directory).
kubectl apply -f infra/k8s/overlays/staging/bootstrap/

# 3. Apply the overlay itself (cd.yml does this on promote).
kubectl apply -k infra/k8s/overlays/staging
```

Prereqs: DNS A records `api.babelstone.dev`, `backstage.babelstone.dev`, and
`app.babelstone.dev` point at the node IP, and Traefik (k3s-bundled) is reachable on
`:80` for the HTTP-01 ACME challenge. The full provision/restore/upgrade runbook is
Phase 6 (bd babelstone-zla1.7).

**Kong↔mcp-server mTLS swap (after `mcp-mtls.yaml` is applied).** The committed
`infra/kong/kong.yml` carries only POC placeholder certs for the mcp-server upstream
(`client_certificate` + `ca_certificates`, with `tls_verify: false`). At deploy,
`scripts/deck-sync.sh` renders Kong with the **real** internal-CA material — Kong's
client cert from `mcp-kong-client-tls` and its `ca_certificates` from the internal CA
(`mcp-mtls-ca`'s `ca.crt`) — and flips `tls_verify: true`. The same CA underwrites
both sides, so uvicorn's `CERT_REQUIRED` verify and Kong's server-cert verify both pass.
No real cert/key material is ever committed.
