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
#   - deploy the overlay itself (kubectl apply -k … / gh workflow run cd.yml)
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
#                                                # without it, the base64 is printed to set by hand
#   scripts/staging-bootstrap.sh -h|--help       # this header
set -euo pipefail

# ── repo root from the script's own location (so infra/ paths work from any CWD) ─────────
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"

BOOTSTRAP_DIR="$REPO_ROOT/infra/k8s/overlays/staging/bootstrap"
TRAEFIK_VALUES="$BOOTSTRAP_DIR/helm/traefik-values.yaml"
CD_KUBECONFIG_SCRIPT="$REPO_ROOT/scripts/cd-kubeconfig.sh"
SECRET_PREFLIGHT_SCRIPT="$REPO_ROOT/scripts/cd-secret-preflight.sh"
# Same source URL bootstrap/README.md step 1b uses — keep them identical.
SUC_MANIFEST_URL="https://github.com/rancher/system-upgrade-controller/releases/latest/download/system-upgrade-controller.yaml"

APP_NAMESPACE="babelstone-staging"
CERT_MANAGER_NAMESPACE="cert-manager"
CLOUDFLARE_SECRET="cloudflare-api-token"

CHECK_ONLY=false
SET_CD_SECRET=false

case "${1:-}" in
  "") ;;
  --check-only)   CHECK_ONLY=true ;;
  --set-cd-secret) SET_CD_SECRET=true ;;
  -h|--help) sed -n '2,42p' "$0" | sed 's/^# \{0,1\}//'; exit 0 ;;
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
  1. helm upgrade --install cert-manager jetstack/cert-manager -n ${CERT_MANAGER_NAMESPACE} (CRDs on); wait for rollout
  2. helm upgrade --install traefik traefik/traefik -n traefik (ingress controller); wait for rollout
  3. kubectl apply -f ${SUC_MANIFEST_URL}
  4. kubectl create namespace ${APP_NAMESPACE} (idempotent)
  5. kubectl apply the cert-manager/${CLOUDFLARE_SECRET} Secret from \$CLOUDFLARE_API_TOKEN (DNS-01 solver)
  6. kubectl apply the cluster-scoped bootstrap ${BOOTSTRAP_DIR}/*.yaml (except volume-snapshot-class.yaml)
  7. mint the least-privilege CD kubeconfig (scripts/cd-kubeconfig.sh) → KUBECONFIG_B64
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
  --namespace "$CERT_MANAGER_NAMESPACE" --create-namespace --set crds.enabled=true
kubectl -n "$CERT_MANAGER_NAMESPACE" rollout status deploy/cert-manager --timeout=300s
kubectl -n "$CERT_MANAGER_NAMESPACE" rollout status deploy/cert-manager-webhook --timeout=300s

# ── STEP 2 · Traefik ingress controller (Helm; idempotent) ───────────────────────────────
step "2. Traefik ingress controller (helm upgrade --install)"
[ -f "$TRAEFIK_VALUES" ] || fail "Traefik values file missing: $TRAEFIK_VALUES"
helm repo add traefik https://traefik.github.io/charts >/dev/null 2>&1 || true
helm repo update traefik >/dev/null
helm upgrade --install traefik traefik/traefik \
  --namespace traefik --create-namespace \
  -f "$TRAEFIK_VALUES"
kubectl -n traefik rollout status deploy/traefik --timeout=300s

# ── STEP 3 · system-upgrade-controller (same source URL as bootstrap/README.md step 1b) ──
step "3. Rancher system-upgrade-controller (kubectl apply)"
kubectl apply -f "$SUC_MANIFEST_URL"

# ── STEP 4 · the app namespace (idempotent) ──────────────────────────────────────────────
step "4. namespace ${APP_NAMESPACE} (idempotent apply)"
kubectl create namespace "$APP_NAMESPACE" --dry-run=client -o yaml | kubectl apply -f -

# ── STEP 5 · the Cloudflare DNS-01 token Secret (idempotent; never echoes the token) ─────
if $CLOUDFLARE_SECRET_EXISTS; then
  step "5. cert-manager/${CLOUDFLARE_SECRET} Secret already exists — leaving it as-is"
else
  step "5. cert-manager/${CLOUDFLARE_SECRET} Secret (idempotent apply from \$CLOUDFLARE_API_TOKEN)"
  # Build the Secret YAML and apply it; the token rides through kubectl on stdin, never echoed.
  kubectl create secret generic "$CLOUDFLARE_SECRET" \
    --namespace "$CERT_MANAGER_NAMESPACE" \
    --from-literal=api-token="$CLOUDFLARE_API_TOKEN" \
    --dry-run=client -o yaml | kubectl apply -f -
fi

# ── STEP 6 · the cluster-scoped bootstrap (glob, excluding volume-snapshot-class.yaml) ───
# The Hetzner CSI is dropped (bd babelstone-zla1.12.20), so volume-snapshot-class.yaml's
# VolumeSnapshotClass has no CRDs installed and a blanket apply of it would fail. Skip it
# here. Once bd babelstone-zla1.12.21 removes that file from the tree, this exclusion can go.
step "6. cluster-scoped bootstrap (${BOOTSTRAP_DIR}/*.yaml, minus volume-snapshot-class.yaml)"
shopt -s nullglob
applied_any=false
for manifest in "$BOOTSTRAP_DIR"/*.yaml; do
  case "$(basename "$manifest")" in
    volume-snapshot-class.yaml)
      echo "   skipping $(basename "$manifest") (Hetzner CSI dropped — bd babelstone-zla1.12.20; remove after bd babelstone-zla1.12.21)"
      continue ;;
  esac
  echo "   apply $(basename "$manifest")"
  kubectl apply -f "$manifest"
  applied_any=true
done
shopt -u nullglob
$applied_any || fail "no bootstrap manifests found under $BOOTSTRAP_DIR — is the repo layout intact?"

# ── STEP 7 · mint the least-privilege CD kubeconfig ──────────────────────────────────────
step "7. mint the CD kubeconfig (scripts/cd-kubeconfig.sh)"
[ -x "$CD_KUBECONFIG_SCRIPT" ] || fail "cannot find the CD kubeconfig minter at $CD_KUBECONFIG_SCRIPT"
CD_KUBECONFIG_TMP="$(mktemp "${TMPDIR:-/tmp}/cd-deployer.kubeconfig.XXXXXX")"
# Ensure the token file is removed even if a later step fails.
cleanup() { rm -f "$CD_KUBECONFIG_TMP"; }
trap cleanup EXIT
"$CD_KUBECONFIG_SCRIPT" -o "$CD_KUBECONFIG_TMP"

CD_KUBECONFIG_B64="$(base64 < "$CD_KUBECONFIG_TMP")"
if $SET_CD_SECRET; then
  step "   setting the KUBECONFIG_B64 env secret (gh secret set --env p6-staging)"
  printf '%s' "$CD_KUBECONFIG_B64" | gh secret set KUBECONFIG_B64 --env p6-staging --body -
  echo "   KUBECONFIG_B64 set on the p6-staging environment."
else
  echo
  echo "   --set-cd-secret NOT passed. Set the KUBECONFIG_B64 environment secret manually with the base64 below:"
  echo "   gh secret set KUBECONFIG_B64 --env p6-staging --body '<paste the base64>'"
  echo "   --- KUBECONFIG_B64 (base64) ---"
  printf '%s\n' "$CD_KUBECONFIG_B64"
  echo "   --- end KUBECONFIG_B64 ---"
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
  [ ] Set the Cloudflare SSL/TLS mode to "Full (strict)" for babelstone.dev.
  [ ] Open inbound TCP 80/443 on the Hetzner firewall (Cloudflare-scoped):
        infra/hetzner-k3s/firewall-web.sh            # dry-run
        infra/hetzner-k3s/firewall-web.sh --apply    # apply
  [ ] Deploy the overlay:
        kubectl apply -k infra/k8s/overlays/staging
      or:
        gh workflow run cd.yml -f overlay=staging -f apply=true
===============================================================================
CHECKLIST

echo "staging bootstrap: OK."
