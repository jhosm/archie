#!/usr/bin/env bash
# scripts/cd-secret-preflight.sh — fail-closed guard against dev-placeholder credentials
# reaching the staging deploy (bd babelstone-zla1.12.4; ADR-PC-004 §A1 — never inline
# literal credentials).
#
# In plain English: the repo commits an example Secret with trivial values (database
# password "babelstone", OpenBao token "root", placeholder Logto keys) so a laptop
# `kustomize build` resolves the wiring. Two things must therefore be true before a
# real deploy: (1) the rendered staging manifests carry NO credential bodies at all,
# and (2) the Secret living in the cluster is the operator-provisioned REAL one, not a
# leftover placeholder. This script checks both, and cd.yml refuses the apply when
# either fails — so a forgotten swap can never reach the public IAM.
#
# Modes:
#   --render   read a rendered manifest stream on STDIN (kustomize build ... | this);
#              FAIL if it contains the placeholder Secret or any known placeholder
#              credential body. Hermetic — no cluster, CI-runnable.
#   --live     inspect the LIVE cluster (uses $KUBECONFIG): FAIL if the
#              babelstone-dev-secrets Secret is missing, lacks a required key, or any
#              key still holds a known dev placeholder value. Run before kubectl apply.
#
# Options:
#   -n <namespace>   live-mode namespace (default: babelstone-staging)
#
# Usage:
#   kustomize build --load-restrictor=LoadRestrictionsNone infra/k8s/overlays/staging \
#     | scripts/cd-secret-preflight.sh --render
#   scripts/cd-secret-preflight.sh --live -n babelstone-staging
set -euo pipefail

# The known dev placeholder values, single-sourced here. Must track
# infra/k8s/base/secrets.example.yaml — if a placeholder is added there, add it here.
PLACEHOLDER_POSTGRES_PASSWORD="babelstone"
PLACEHOLDER_OPENBAO_DEV_TOKEN="root"
PLACEHOLDER_SECRET_VAULT_KEK="ZGV2LXBsYWNlaG9sZGVyLXZhdWx0LWtlay1kby1ub3QtdXNl"
PLACEHOLDER_OIDC_PREFIX="dev-placeholder"   # OIDC_PRIVATE_KEYS placeholder starts with this
SECRET_NAME="babelstone-dev-secrets"
# LOGTO_GRAFANA_CLIENT_SECRET has no committed placeholder (it was never in
# secrets.example.yaml) but grafana-oidc.patch.yaml secretKeyRefs it non-optionally,
# so presence is still required for the pod to start.
REQUIRED_KEYS="POSTGRES_PASSWORD OPENBAO_DEV_TOKEN SECRET_VAULT_KEK OIDC_PRIVATE_KEYS LOGTO_GRAFANA_CLIENT_SECRET"

MODE=""
NAMESPACE="babelstone-staging"

while [ $# -gt 0 ]; do
  case "$1" in
    --render) MODE="render"; shift ;;
    --live)   MODE="live"; shift ;;
    -n) NAMESPACE="${2:?-n needs a value}"; shift 2 ;;
    -h|--help) sed -n '2,29p' "$0" | sed 's/^# \{0,1\}//'; exit 0 ;;
    *) echo "ERROR: unknown argument: $1 (want --render or --live)" >&2; exit 2 ;;
  esac
done
[ -n "$MODE" ] || { echo "ERROR: pass --render (stdin) or --live" >&2; exit 2; }

fail() { echo "PREFLIGHT FAIL: $*" >&2; exit 1; }

if [ "$MODE" = "render" ]; then
  RENDER="$(cat)"
  [ -n "$RENDER" ] || fail "empty render on stdin — pipe 'kustomize build …' into this script"

  # 1. The placeholder Secret must not be in the applied set AT ALL. Split the stream
  #    into YAML documents; flag any document that is a Secret carrying the seam name
  #    or the dev-mode-placeholder boundary annotation. (secretKeyRef mentions of the
  #    name in workloads are fine — only a `kind: Secret` document is a rendered body.)
  BAD_DOCS="$(printf '%s\n' "$RENDER" | awk -v secret_name="$SECRET_NAME" '
    BEGIN { RS = "\n---\n" }
    /(^|\n)kind: Secret(\n|$)/ {
      if (index($0, "name: " secret_name) > 0 || index($0, "dev-mode-placeholder") > 0) n++
    }
    END { print n + 0 }')"
  [ "$BAD_DOCS" -eq 0 ] || fail "the render still emits the ${SECRET_NAME} placeholder Secret (${BAD_DOCS} doc(s)) — the staging overlay must drop it (drop-dev-secrets.patch.yaml)"

  # 2. Belt-and-braces: no known placeholder credential BODY anywhere in the render,
  #    whatever Secret it rides in. (POSTGRES_USER=babelstone in the ConfigMap is a
  #    username, not a credential — the patterns below match the credential keys only.)
  printf '%s\n' "$RENDER" | grep -Eq "POSTGRES_PASSWORD:[[:space:]]*[\"']?${PLACEHOLDER_POSTGRES_PASSWORD}[\"']?[[:space:]]*$" \
    && fail "placeholder POSTGRES_PASSWORD body found in the render"
  printf '%s\n' "$RENDER" | grep -Eq "OPENBAO_DEV_TOKEN:[[:space:]]*[\"']?${PLACEHOLDER_OPENBAO_DEV_TOKEN}[\"']?[[:space:]]*$" \
    && fail "placeholder OPENBAO_DEV_TOKEN body found in the render"
  printf '%s\n' "$RENDER" | grep -q "$PLACEHOLDER_SECRET_VAULT_KEK" \
    && fail "placeholder SECRET_VAULT_KEK body found in the render"
  printf '%s\n' "$RENDER" | grep -q "${PLACEHOLDER_OIDC_PREFIX}-oidc" \
    && fail "placeholder OIDC_PRIVATE_KEYS body found in the render"

  echo "render preflight OK: no placeholder Secret and no placeholder credential bodies in the applied set"
  exit 0
fi

# ── --live ───────────────────────────────────────────────────────────────────────────
command -v kubectl >/dev/null || fail "kubectl not found on PATH (live mode needs the deploy kubeconfig)"

kubectl -n "$NAMESPACE" get secret "$SECRET_NAME" >/dev/null 2>&1 \
  || fail "Secret ${NAMESPACE}/${SECRET_NAME} not found — provision the REAL secrets first (runbook staging-ops.md §1 step 5); the deploy no longer creates it"

get_key() { # decoded value of .data[$1], empty if absent
  kubectl -n "$NAMESPACE" get secret "$SECRET_NAME" \
    -o jsonpath="{.data.$1}" 2>/dev/null | base64 -d 2>/dev/null || true
}

for key in $REQUIRED_KEYS; do
  val="$(get_key "$key")"
  [ -n "$val" ] || fail "live Secret ${SECRET_NAME} is missing key ${key} — the workloads secretKeyRef it; provision the full real Secret (runbook §1 step 5)"
  case "$key" in
    POSTGRES_PASSWORD)
      [ "$val" != "$PLACEHOLDER_POSTGRES_PASSWORD" ] || fail "live POSTGRES_PASSWORD is still the dev placeholder" ;;
    OPENBAO_DEV_TOKEN)
      [ "$val" != "$PLACEHOLDER_OPENBAO_DEV_TOKEN" ] || fail "live OPENBAO_DEV_TOKEN is still the dev placeholder ('root')" ;;
    SECRET_VAULT_KEK)
      [ "$val" != "$(printf '%s' "$PLACEHOLDER_SECRET_VAULT_KEK" | base64 -d)" ] || fail "live SECRET_VAULT_KEK is still the dev placeholder" ;;
    OIDC_PRIVATE_KEYS)
      case "$val" in
        "$PLACEHOLDER_OIDC_PREFIX"*) fail "live OIDC_PRIVATE_KEYS is still the dev placeholder — the public IAM must never sign with it" ;;
      esac ;;
  esac
done

echo "live preflight OK: ${NAMESPACE}/${SECRET_NAME} present with all required keys, no placeholder values"
