#!/usr/bin/env bash
# scripts/staging-bootstrap.sh — fail-closed, idempotent Phase-2 staging bring-up
# (bd babelstone-zla1.12.23; ADR-IC-006 minimise-surface posture in the preflight spirit).
#
# In plain English: bringing the always-on staging box up by hand means running a long,
# order-sensitive list of helm/kubectl commands from bootstrap/README.md — and getting one
# wrong (skipping a controller, applying a manifest whose CRDs aren't installed yet, or
# starting before the real app secrets exist) leaves a half-built cluster that fails in
# confusing ways later. This script automates exactly the DATA-INDEPENDENT glue of that
# list: install cert-manager + Traefik + the system-upgrade-controller, create the
# namespace and the Cloudflare DNS-01 token Secret, apply the cluster-scoped bootstrap, and
# mint the least-privilege CD kubeconfig. It refuses to run a HALF bootstrap — like
# provision.sh refuses a bad SSH CIDR — checking every required tool, a reachable cluster,
# and the operator-provisioned app secrets up front, and it does NOT touch the account-gated
# pieces (Cloudflare DNS records, the Logto client secrets, the firewall). It is idempotent
# (helm upgrade --install, kubectl apply), so re-running it converges rather than duplicates.
#
# What it does NOT do (irreducibly human / out of scope — see the closing checklist):
#   - set the Cloudflare DNS A records
#   - register the Logto apps + provision their client secrets in babelstone-dev-secrets
#     (staging-ops.md §1 step 5 — this script REFUSES to mint the account-gated secrets)
#   - set Cloudflare SSL/TLS = Full (strict)
#   - open the Hetzner web firewall (infra/hetzner-k3s/firewall-web.sh --apply)
#   - deploy the overlay itself (kustomize build --load-restrictor=LoadRestrictionsNone … |
#     kubectl apply -f -, or gh workflow run cd.yml — NOT `kubectl apply -k`, which can't pass
#     the load-restrictor the out-of-root kong.yml ConfigMap needs)
#
# Required env:
#   KUBECONFIG              path to the cluster-admin kubeconfig (the gitignored hetzner-k3s
#                           one) — minting the CD SA token needs admin rights. Under
#                           --check-only a live cluster is NOT required (CI-runnable).
#   CLOUDFLARE_API_TOKEN    a scoped Zone.DNS:Edit token for babelstone.dev — used to create
#                           the cert-manager `cloudflare-api-token` Secret for the DNS-01 ACME
#                           solver. NOT required if that Secret already exists, nor under
#                           --check-only. The token value is never echoed.
#
# Usage:
#   scripts/staging-bootstrap.sh                 # full bootstrap (needs a live cluster)
#   scripts/staging-bootstrap.sh --check-only    # preflight + print the ordered plan; NO
#                                                # cluster mutation, NO live cluster required
#   scripts/staging-bootstrap.sh --set-cd-secret # after minting the CD kubeconfig, set the
#                                                # KUBECONFIG_B64 env secret via gh (needs gh);
#                                                # without it, the base64 is written to a mode-600
#                                                # temp file to set by hand (never printed)
#   scripts/staging-bootstrap.sh -h|--help       # this header
set -euo pipefail

# ── repo root from the script's own location (so infra/ paths work from any CWD) ─────────
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"

BOOTSTRAP_DIR="$REPO_ROOT/infra/k8s/overlays/staging/bootstrap"
TRAEFIK_VALUES="$BOOTSTRAP_DIR/helm/traefik-values.yaml"
CD_KUBECONFIG_SCRIPT="$REPO_ROOT/scripts/cd-kubeconfig.sh"
SECRET_PREFLIGHT_SCRIPT="$REPO_ROOT/scripts/cd-secret-preflight.sh"

# ── PINNED third-party versions (supply-chain: never install "latest") ───────────────────
# The three cluster controllers below are installed at a PINNED version, never floating
# `latest`, so a bring-up is reproducible and an upstream release can't silently change what
# lands on the box (same ethos as the digest-pinned first-party images, PR #531). Keep these
# IDENTICAL to bootstrap/README.md's "Apply order" — the two sources are one contract.
# The pinned k3s is `v1.35.6+k3s1` (`infra/hetzner-k3s/cluster.yaml` `k3s_version`), i.e. k8s
# server 1.35. These controller versions were VERIFIED against k8s 1.35 and bumped to a
# 1.35-supported line on 2026-07-10 (bd babelstone-zla1.12.26): cert-manager v1.21 supports
# k8s 1.33→1.36 (v1.16 was EOL and stopped at 1.32); system-upgrade-controller v0.19 is the
# first release built for k8s 1.35; the hashicorp/vault chart 0.34.0 ships vault-csi-provider
# v1.7.3 (GA APIs, no kubeVersion ceiling — the earlier `4.1.0` was not a resolvable chart
# version). Re-verify against the support matrices before any future k8s bump.
# Verify / bump:
#   cert-manager  → `helm search repo jetstack/cert-manager --versions` (after `helm repo update`)
#   Traefik chart → `helm search repo traefik/traefik --versions`
#   sys-upgrade-c → https://github.com/rancher/system-upgrade-controller/releases (pick a tag)
#   vault-csi-pr  → `helm search repo hashicorp/vault --versions` (the chart's csi: subcomponent)
CERT_MANAGER_VERSION="v1.21.0"      # jetstack/cert-manager Helm chart (== app version)
TRAEFIK_CHART_VERSION="33.2.1"      # traefik/traefik Helm chart (ships Traefik proxy v3.x)
SUC_VERSION="v0.19.2"               # rancher/system-upgrade-controller release tag
VAULT_CHART_VERSION="0.34.0"        # hashicorp/vault Helm chart — used ONLY for the vault-csi-provider (ships vault-csi-provider v1.7.3)

# The Secrets Store CSI driver is the VENDORED, pinned (v1.6.0) material under the openbao-csi
# component's upstream/ dir — applied file-by-file (hermetic, no remote fetch), NOT via Helm.
# These are the driver install's plain manifests: the two CRDs, RBAC, the CSIDriver, the
# node DaemonSet. Keep this list in lockstep with bootstrap/README.md step 1c.
OPENBAO_CSI_UPSTREAM_DIR="$REPO_ROOT/infra/k8s/components/openbao-csi/upstream"
OPENBAO_CSI_UPSTREAM_FILES=(
  secrets-store.csi.x-k8s.io_secretproviderclasses.yaml
  secrets-store.csi.x-k8s.io_secretproviderclasspodstatuses.yaml
  rbac-secretproviderclass.yaml
  rbac-secretprovidersyncing.yaml
  csidriver.yaml
  secrets-store-csi-driver.yaml
)

# Same source URL bootstrap/README.md step 1b uses — keep them identical. Pinned to
# ${SUC_VERSION} (releases/download/<TAG>/…), NOT releases/latest/download/….
SUC_MANIFEST_URL="https://github.com/rancher/system-upgrade-controller/releases/download/${SUC_VERSION}/system-upgrade-controller.yaml"

APP_NAMESPACE="babelstone-staging"
CERT_MANAGER_NAMESPACE="cert-manager"
CLOUDFLARE_SECRET="cloudflare-api-token"

CHECK_ONLY=false
SET_CD_SECRET=false

case "${1:-}" in
  "") ;;
  --check-only)   CHECK_ONLY=true ;;
  --set-cd-secret) SET_CD_SECRET=true ;;
  -h|--help) sed -n '2,43p' "$0" | sed 's/^# \{0,1\}//'; exit 0 ;;
  *) echo "ERROR: unknown argument: $1 (want --check-only, --set-cd-secret, or --help)" >&2; exit 2 ;;
esac

fail() { echo "STAGING BOOTSTRAP PREFLIGHT FAIL: $*" >&2; exit 1; }
warn() { echo "STAGING BOOTSTRAP WARN: $*" >&2; }
step() { echo ">> $*"; }

# ── 1 · preflight: required tools on PATH, fail closed ───────────────────────────────────
REQUIRED_TOOLS=(helm kubectl base64 openssl dig)
$SET_CD_SECRET && REQUIRED_TOOLS+=(gh)
for tool in "${REQUIRED_TOOLS[@]}"; do
  command -v "$tool" >/dev/null 2>&1 || fail "required tool not on PATH: $tool"
done

# ── 2 · preflight: a reachable cluster (skipped-with-a-note under --check-only) ───────────
if $CHECK_ONLY; then
  echo "NOTE (--check-only): skipping the live-cluster reachability check — no cluster required."
else
  [ -n "${KUBECONFIG:-}" ] || fail "KUBECONFIG is unset — export the cluster-admin kubeconfig (the gitignored hetzner-k3s one)"
  kubectl cluster-info >/dev/null 2>&1 || fail "kubectl cluster-info is not reachable — is KUBECONFIG the live cluster-admin kubeconfig?"
fi

# ── 3 · preflight: the Cloudflare DNS-01 token (unless --check-only or the Secret exists) ─
# CLOUDFLARE_SECRET_EXISTS is only meaningful with a live cluster.
CLOUDFLARE_SECRET_EXISTS=false
if ! $CHECK_ONLY; then
  if kubectl -n "$CERT_MANAGER_NAMESPACE" get secret "$CLOUDFLARE_SECRET" >/dev/null 2>&1; then
    CLOUDFLARE_SECRET_EXISTS=true
  fi
fi
if ! $CHECK_ONLY && ! $CLOUDFLARE_SECRET_EXISTS; then
  [ -n "${CLOUDFLARE_API_TOKEN:-}" ] \
    || fail "CLOUDFLARE_API_TOKEN is unset and the cert-manager/${CLOUDFLARE_SECRET} Secret does not exist — export a scoped Zone.DNS:Edit token for babelstone.dev (the DNS-01 ACME solver needs it; never commit it)"
fi

# ── 4 · preflight: the app-tier Secret gate (reuse cd-secret-preflight.sh --live) ────────
# The operator-provisioned babelstone-dev-secrets must exist and carry no placeholder
# bodies BEFORE we bring the cluster up — this script must NEVER mint the account-gated
# Logto/OIDC secrets (staging-ops.md §1 step 5). Skipped under --check-only (no cluster).
if $CHECK_ONLY; then
  echo "NOTE (--check-only): skipping the app-tier Secret gate (cd-secret-preflight.sh --live) — no cluster required."
else
  [ -x "$SECRET_PREFLIGHT_SCRIPT" ] || fail "cannot find the app-secret gate at $SECRET_PREFLIGHT_SCRIPT"
  "$SECRET_PREFLIGHT_SCRIPT" --live -n "$APP_NAMESPACE" \
    || fail "app-tier Secret gate failed — provision the REAL babelstone-dev-secrets first (runbook infra/runbooks/staging-ops.md §1 step 5). This script does NOT mint the account-gated Logto/OIDC secrets."
fi

# ── 5 · preflight: DNS resolution WARN (loud, never fatal) ───────────────────────────────
for host in app.babelstone.dev api.babelstone.dev auth.babelstone.dev backstage.babelstone.dev; do
  if [ -z "$(dig +short "$host" 2>/dev/null)" ]; then
    warn "DNS does not resolve for ${host} — set its Cloudflare A record (proxied is fine) before the certs can issue."
  fi
done

echo "preflight OK."
echo

# ── the ordered plan (printed in every mode; the only side-effecting output in --check-only) ─
cat <<PLAN
Ordered Phase-2 bootstrap plan (data-independent glue automated by this script):
  1. helm upgrade --install cert-manager jetstack/cert-manager --version ${CERT_MANAGER_VERSION} -n ${CERT_MANAGER_NAMESPACE} (CRDs on); wait for rollout
  2. helm upgrade --install traefik traefik/traefik --version ${TRAEFIK_CHART_VERSION} -n traefik (ingress controller); wait for rollout
  3. kubectl apply -f ${SUC_MANIFEST_URL}   (system-upgrade-controller ${SUC_VERSION})
  4. kubectl apply the VENDORED Secrets Store CSI driver (${OPENBAO_CSI_UPSTREAM_DIR}, pinned v1.6.0) +
     helm upgrade --install vault-csi-provider hashicorp/vault --version ${VAULT_CHART_VERSION} -n kube-system
     (csi-only) — the out-of-band half of the openbao-csi component (CRDs/CSIDriver/DaemonSet, kube-system)
  5. kubectl create namespace ${APP_NAMESPACE} (idempotent)
  6. kubectl apply the cert-manager/${CLOUDFLARE_SECRET} Secret from \$CLOUDFLARE_API_TOKEN (DNS-01 solver)
  7. kubectl apply the cluster-scoped bootstrap ${BOOTSTRAP_DIR}/*.yaml
  8. mint the least-privilege CD kubeconfig (scripts/cd-kubeconfig.sh) → KUBECONFIG_B64
PLAN
echo

if $CHECK_ONLY; then
  echo "--check-only: preflight ran, plan printed, cluster UNTOUCHED. Stopping before any mutation."
  exit 0
fi

# ── STEP 1 · cert-manager (Helm, upstream-recommended; idempotent) ───────────────────────
step "1. cert-manager (helm upgrade --install)"
helm repo add jetstack https://charts.jetstack.io >/dev/null 2>&1 || true
helm repo update jetstack >/dev/null
helm upgrade --install cert-manager jetstack/cert-manager \
  --version "$CERT_MANAGER_VERSION" \
  --namespace "$CERT_MANAGER_NAMESPACE" --create-namespace --set crds.enabled=true
kubectl -n "$CERT_MANAGER_NAMESPACE" rollout status deploy/cert-manager --timeout=300s
kubectl -n "$CERT_MANAGER_NAMESPACE" rollout status deploy/cert-manager-webhook --timeout=300s

# ── STEP 2 · Traefik ingress controller (Helm; idempotent) ───────────────────────────────
step "2. Traefik ingress controller (helm upgrade --install)"
[ -f "$TRAEFIK_VALUES" ] || fail "Traefik values file missing: $TRAEFIK_VALUES"
helm repo add traefik https://traefik.github.io/charts >/dev/null 2>&1 || true
helm repo update traefik >/dev/null
helm upgrade --install traefik traefik/traefik \
  --version "$TRAEFIK_CHART_VERSION" \
  --namespace traefik --create-namespace \
  -f "$TRAEFIK_VALUES"
kubectl -n traefik rollout status deploy/traefik --timeout=300s

# ── STEP 3 · system-upgrade-controller (same source URL as bootstrap/README.md step 1b) ──
# Pinned to ${SUC_VERSION} (releases/download/<TAG>/…, never releases/latest/download/…).
step "3. Rancher system-upgrade-controller ${SUC_VERSION} (kubectl apply)"
kubectl apply -f "$SUC_MANIFEST_URL"

# ── STEP 4 · Secrets Store CSI driver (vendored) + vault-csi-provider (Helm) ──────────────
# The out-of-band half of the openbao-csi component (bd babelstone-zla1.12.21): the CRDs
# (SecretProviderClass, SecretProviderClassPodStatus), RBAC, the CSIDriver, and the node
# DaemonSet land cluster-scoped in kube-system — NEVER in `kustomize build overlays/staging`
# (the strict kubeconform gate has no schema for them). The overlay registers ONLY the
# SecretProviderClass custom resource. The driver is the VENDORED, pinned-v1.6.0 material
# under the component's upstream/ (applied file-by-file so it is hermetic — no remote fetch);
# the vault-csi-provider is the HashiCorp chart's csi: subcomponent (server/injector off).
step "4. Secrets Store CSI driver (vendored v1.6.0) + vault-csi-provider (helm ${VAULT_CHART_VERSION})"
[ -d "$OPENBAO_CSI_UPSTREAM_DIR" ] || fail "vendored CSI driver dir missing: $OPENBAO_CSI_UPSTREAM_DIR"
for csi_file in "${OPENBAO_CSI_UPSTREAM_FILES[@]}"; do
  [ -f "$OPENBAO_CSI_UPSTREAM_DIR/$csi_file" ] || fail "vendored CSI driver manifest missing: $csi_file"
  echo "   apply upstream/$csi_file"
  kubectl apply -f "$OPENBAO_CSI_UPSTREAM_DIR/$csi_file"
done
helm repo add hashicorp https://helm.releases.hashicorp.com >/dev/null 2>&1 || true
helm repo update hashicorp >/dev/null
helm upgrade --install vault-csi-provider hashicorp/vault \
  --version "$VAULT_CHART_VERSION" \
  --namespace kube-system \
  --set "csi.enabled=true" --set "server.enabled=false" --set "injector.enabled=false"
# The DaemonSet name is from the VENDORED upstream manifest (metadata.name: csi-secrets-store),
# NOT the Helm-chart-derived name `csi-secrets-store-secrets-store-csi-driver` (this is a
# `kubectl apply` of upstream/, not a Helm install — bd babelstone-zla1.12.29).
kubectl -n kube-system rollout status ds/csi-secrets-store --timeout=300s

# ── STEP 5 · the app namespace (idempotent) ──────────────────────────────────────────────
step "5. namespace ${APP_NAMESPACE} (idempotent apply)"
kubectl create namespace "$APP_NAMESPACE" --dry-run=client -o yaml | kubectl apply -f -

# ── STEP 6 · the Cloudflare DNS-01 token Secret (idempotent; never echoes the token) ─────
if $CLOUDFLARE_SECRET_EXISTS; then
  step "6. cert-manager/${CLOUDFLARE_SECRET} Secret already exists — leaving it as-is"
else
  step "6. cert-manager/${CLOUDFLARE_SECRET} Secret (idempotent apply from \$CLOUDFLARE_API_TOKEN)"
  # Keep the token OUT of the process argv (visible via `ps`) and out of stdout: base64 it into
  # a Secret manifest applied on stdin. The value never appears as a command argument.
  cf_token_b64="$(printf '%s' "$CLOUDFLARE_API_TOKEN" | base64 | tr -d '\n')"
  kubectl apply -f - <<EOF
apiVersion: v1
kind: Secret
metadata:
  name: ${CLOUDFLARE_SECRET}
  namespace: ${CERT_MANAGER_NAMESPACE}
type: Opaque
data:
  api-token: ${cf_token_b64}
EOF
  unset cf_token_b64
fi

# ── STEP 7 · the cluster-scoped bootstrap (glob over ${BOOTSTRAP_DIR}/*.yaml) ─────────────
# Every file here is kubectl apply-safe: the dead volume-snapshot-class.yaml (a
# VolumeSnapshotClass whose CRD is not installed since the Hetzner CSI was dropped, bd
# babelstone-zla1.12.20) was removed in bd babelstone-zla1.12.24, so a blanket apply no
# longer fails and no exclusion is needed.
step "7. cluster-scoped bootstrap (${BOOTSTRAP_DIR}/*.yaml)"
shopt -s nullglob
applied_any=false
for manifest in "$BOOTSTRAP_DIR"/*.yaml; do
  echo "   apply $(basename "$manifest")"
  kubectl apply -f "$manifest"
  applied_any=true
done
shopt -u nullglob
$applied_any || fail "no bootstrap manifests found under $BOOTSTRAP_DIR — is the repo layout intact?"

# ── STEP 8 · mint the least-privilege CD kubeconfig ──────────────────────────────────────
step "8. mint the CD kubeconfig (scripts/cd-kubeconfig.sh)"
[ -x "$CD_KUBECONFIG_SCRIPT" ] || fail "cannot find the CD kubeconfig minter at $CD_KUBECONFIG_SCRIPT"
CD_KUBECONFIG_TMP="$(mktemp "${TMPDIR:-/tmp}/cd-deployer.kubeconfig.XXXXXX")"
# Ensure the token file is removed even if a later step fails.
cleanup() { rm -f "$CD_KUBECONFIG_TMP"; }
trap cleanup EXIT
"$CD_KUBECONFIG_SCRIPT" -o "$CD_KUBECONFIG_TMP"

if $SET_CD_SECRET; then
  step "   setting the KUBECONFIG_B64 env secret (gh secret set --env p6-staging)"
  base64 < "$CD_KUBECONFIG_TMP" | gh secret set KUBECONFIG_B64 --env p6-staging
  echo "   KUBECONFIG_B64 set on the p6-staging environment."
else
  # Do NOT print the credential to stdout (terminal scrollback / CI logs). Write the base64 to a
  # private (mode 600) temp file OUTSIDE this script's cleanup trap; the operator sets the secret
  # from it, then shreds it.
  cd_b64_out="$(mktemp "${TMPDIR:-/tmp}/cd-deployer-b64.XXXXXX")"
  ( umask 077; base64 < "$CD_KUBECONFIG_TMP" > "$cd_b64_out" )
  echo
  echo "   --set-cd-secret NOT passed. The base64 CD kubeconfig (a CREDENTIAL) was written to:"
  echo "       $cd_b64_out   (mode 600 — do NOT commit or share it)"
  echo "   Set the environment secret from it, then shred it:"
  echo "       gh secret set KUBECONFIG_B64 --env p6-staging < \"$cd_b64_out\" && shred -u \"$cd_b64_out\""
fi
# cleanup trap removes the temp token file on exit (success or failure).

# ── closing checklist: the irreducibly-human steps this script did NOT perform ───────────
cat <<'CHECKLIST'

===============================================================================
Bootstrap glue done. REMAINING HUMAN / ACCOUNT-GATED STEPS (this script did NOT do these):

  [ ] Set the Cloudflare DNS A records for app / api / auth / backstage.babelstone.dev
      (proxied/orange-cloud is fine — certs issue via DNS-01).
  [ ] Register the Logto apps and put their client secrets into babelstone-dev-secrets
      (runbook infra/runbooks/staging-ops.md §1 step 5). This script REFUSES to mint the
      account-gated Logto/OIDC secrets.
  [ ] Initialise + unseal OpenBao and populate its KV paths (bao operator init/unseal, then
      the secret/data/babelstone/* paths the SecretProviderClass reads) — the CSI mount stays
      unresolved until this is done. These produce secret-zero and are the operator's job
      (infra/k8s/components/openbao-csi/README.md "Live apply + init").
  [ ] Set the Cloudflare SSL/TLS mode to "Full (strict)" for babelstone.dev.
  [ ] Open inbound TCP 80/443 on the Hetzner firewall (Cloudflare-scoped):
        infra/hetzner-k3s/firewall-web.sh            # dry-run
        infra/hetzner-k3s/firewall-web.sh --apply    # apply
  [ ] Deploy the overlay (NOT 'kubectl apply -k' — the out-of-root kong.yml ConfigMap needs
      --load-restrictor, which kubectl's embedded kustomize can't pass):
        mise exec -- kustomize build --load-restrictor=LoadRestrictionsNone infra/k8s/overlays/staging | kubectl apply -f -
      or:
        gh workflow run cd.yml -f overlay=staging -f apply=true
===============================================================================
CHECKLIST

echo "staging bootstrap: OK."
