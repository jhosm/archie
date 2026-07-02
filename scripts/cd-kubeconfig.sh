#!/usr/bin/env bash
# scripts/cd-kubeconfig.sh — mint the least-privilege deploy kubeconfig cd.yml consumes
# (bd babelstone-zla1.12.1; ADR-IC-013 CD design).
#
# In plain English: the deploy pipeline used to authenticate with the cluster-admin
# kubeconfig hetzner-k3s generates — an all-powerful, non-revocable credential. This
# script builds the narrow replacement: a kubeconfig whose identity is the
# `cd-deployer` ServiceAccount (RBAC scoped to exactly what
# `kubectl apply -k overlays/staging` needs — see
# infra/k8s/overlays/staging/bootstrap/cd-deploy-rbac.yaml). THAT kubeconfig, not the
# admin one, is what you base64 into the KUBECONFIG_B64 GitHub environment secret.
#
# Run ONCE by the human operator at Phase-2 bootstrap, on a shell whose current
# KUBECONFIG is the (gitignored) cluster-admin one — reading the SA token requires
# admin rights, which is exactly why cd.yml can't mint this itself. Revoke by
# deleting the cd-deployer-token Secret; re-run this script to re-mint.
#
# Usage:
#   scripts/cd-kubeconfig.sh [-n <namespace>] [-o <outfile>]
#     -n   namespace holding the cd-deployer SA (default: babelstone-staging)
#     -o   write the kubeconfig here, chmod 600 (default: stdout)
#
#   # typical flow (operator, once):
#   export KUBECONFIG=infra/hetzner-k3s/kubeconfig          # the admin credential
#   scripts/cd-kubeconfig.sh -o /tmp/cd-deployer.kubeconfig
#   base64 < /tmp/cd-deployer.kubeconfig                    # → KUBECONFIG_B64 env secret
#   rm -f /tmp/cd-deployer.kubeconfig                       # don't leave the token around
set -euo pipefail

NAMESPACE="babelstone-staging"
SA_TOKEN_SECRET="cd-deployer-token"
OUTFILE=""

usage() { sed -n '2,26p' "$0" | sed 's/^# \{0,1\}//'; exit "${1:-0}"; }

while [ $# -gt 0 ]; do
  case "$1" in
    -n) NAMESPACE="${2:?-n needs a value}"; shift 2 ;;
    -o) OUTFILE="${2:?-o needs a value}"; shift 2 ;;
    -h|--help) usage ;;
    *) echo "ERROR: unknown argument: $1" >&2; usage 1 ;;
  esac
done

command -v kubectl >/dev/null || { echo "ERROR: kubectl not found on PATH" >&2; exit 1; }

# ── 1 · cluster coordinates from the CURRENT (admin) kubeconfig ─────────────────────
SERVER="$(kubectl config view --minify -o jsonpath='{.clusters[0].cluster.server}')"
CA_DATA="$(kubectl config view --minify --raw -o jsonpath='{.clusters[0].cluster.certificate-authority-data}')"
CLUSTER_NAME="$(kubectl config view --minify -o jsonpath='{.clusters[0].name}')"
[ -n "$SERVER" ] || { echo "ERROR: could not read the API server address from the current kubeconfig" >&2; exit 1; }
[ -n "$CA_DATA" ] || { echo "ERROR: current kubeconfig carries no certificate-authority-data (inline CA required)" >&2; exit 1; }

# ── 2 · the ServiceAccount token (bootstrap must have applied cd-deploy-rbac.yaml) ──
TOKEN="$(kubectl -n "$NAMESPACE" get secret "$SA_TOKEN_SECRET" -o jsonpath='{.data.token}' 2>/dev/null | base64 -d || true)"
if [ -z "$TOKEN" ]; then
  echo "ERROR: no token in secret ${NAMESPACE}/${SA_TOKEN_SECRET}." >&2
  echo "       Apply infra/k8s/overlays/staging/bootstrap/cd-deploy-rbac.yaml first (Phase-2" >&2
  echo "       bootstrap, needs the admin kubeconfig), then give the control plane a moment" >&2
  echo "       to populate the kubernetes.io/service-account-token Secret." >&2
  exit 1
fi

# ── 3 · emit the self-contained deploy kubeconfig ───────────────────────────────────
render() {
  cat <<EOF
apiVersion: v1
kind: Config
clusters:
  - name: ${CLUSTER_NAME}
    cluster:
      server: ${SERVER}
      certificate-authority-data: ${CA_DATA}
users:
  - name: cd-deployer
    user:
      token: ${TOKEN}
contexts:
  - name: cd-deployer@${CLUSTER_NAME}
    context:
      cluster: ${CLUSTER_NAME}
      user: cd-deployer
      namespace: ${NAMESPACE}
current-context: cd-deployer@${CLUSTER_NAME}
EOF
}

if [ -n "$OUTFILE" ]; then
  umask 077
  render > "$OUTFILE"
  chmod 600 "$OUTFILE"
  echo "wrote deploy kubeconfig → $OUTFILE (identity: system:serviceaccount:${NAMESPACE}:cd-deployer)" >&2
  # Sanity: the minted credential must NOT be cluster-admin (the whole point). The same
  # probe cd.yml runs fail-closed at apply time.
  if kubectl --kubeconfig "$OUTFILE" auth can-i '*' '*' --all-namespaces >/dev/null 2>&1; then
    echo "ERROR: the minted kubeconfig can do '*' on '*' — that is cluster-admin, refusing." >&2
    rm -f "$OUTFILE"
    exit 1
  fi
  echo "verified: not cluster-admin. Next: base64 < $OUTFILE → the KUBECONFIG_B64 environment secret; then delete the file." >&2
else
  render
  echo "NOTE: kubeconfig (with the SA token) written to stdout — pipe it somewhere safe; base64 it into KUBECONFIG_B64." >&2
fi
