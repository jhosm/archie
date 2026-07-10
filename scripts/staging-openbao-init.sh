#!/usr/bin/env bash
set -euo pipefail

# scripts/staging-openbao-init.sh — one-command OpenBao secret-zero init for the staging box
# (bd babelstone-zla1.12.33). Automates the "Live apply + init" checklist of
# infra/k8s/components/openbao-csi/README.md so the operator runs ONE command instead of ~30
# order-sensitive `bao` commands.
#
# In plain English: OpenBao boots SEALED and empty. Before the engine can read its PII/secret
# boundary it must be initialised (which produces the unseal keys + root token — "secret-zero"),
# unsealed, told how to trust in-cluster ServiceAccounts (Kubernetes auth), given a read-only
# policy + role, and seeded with the app-tier secrets the SecretProviderClass reads. This script
# does all of that, idempotently and fail-closed.
#
# SECRET-ZERO: `bao operator init` prints the unseal key shares + the initial root token. Those are
# the master credentials — this script writes them to a mode-0600 file and prints ONLY the path.
# It NEVER echoes them to stdout and NEVER commits them. Move that file to an OFFLINE store (a
# password manager) and shred the local copy once captured. They live OUTSIDE the cluster, forever.
#
# NO SECRET IN ARGV: every secret value (the app-tier secrets, the AppRole secret_id, the transit
# token) is passed to `bao` over the `kubectl exec` STDIN stream — never as a command argument — so
# nothing sensitive lands in the kube-apiserver audit log (which records exec command args at
# Request level). The shared values (POSTGRES_PASSWORD / SECRET_VAULT_KEK / OIDC_PRIVATE_KEYS) are
# read straight FROM the operator-provisioned babelstone-dev-secrets, so OpenBao and that Secret can
# never drift — in particular the password inside secret/data/Engine is guaranteed to match
# babelstone-dev-secrets:POSTGRES_PASSWORD (the cross-store trap in staging-ops.md §1 step 5).
#
# Prereqs (checked in preflight): a reachable cluster (KUBECONFIG), the openbao pod Running, the
# babelstone-dev-secrets Secret present, and the openbao SA's system:auth-delegator binding
# (bd babelstone-zla1.12.31) — without which the k8s-auth TokenReview is forbidden.
#
# Usage:
#   scripts/staging-openbao-init.sh                # full init + unseal + provisioning (idempotent)
#   scripts/staging-openbao-init.sh --check-only   # preflight only; NO mutation, NO live cluster edit
#   scripts/staging-openbao-init.sh --unseal       # post-reboot: unseal + ordered engine restart
#   scripts/staging-openbao-init.sh -h|--help      # this header
#
# Env (all optional):
#   KUBECONFIG              cluster-admin kubeconfig (required except under --check-only offline)
#   NAMESPACE              app namespace                         (default: babelstone-staging)
#   OPENBAO_INIT_SECRETS   path for the secret-zero file          (default: $TMPDIR/babelstone-openbao-init-secrets.json)
#   KEY_SHARES/KEY_THRESHOLD  unseal shares/threshold             (default: 1 / 1 — single operator)
#   ENGINE_SA              the mounting pod's ServiceAccount       (default: auto-detected from deploy/engine)

NAMESPACE="${NAMESPACE:-babelstone-staging}"
APP_SECRET="babelstone-dev-secrets"
KEY_SHARES="${KEY_SHARES:-1}"
KEY_THRESHOLD="${KEY_THRESHOLD:-1}"
INIT_SECRETS_FILE="${OPENBAO_INIT_SECRETS:-${TMPDIR:-/tmp}/babelstone-openbao-init-secrets.json}"
APP_ROLE_NAME="babelstone-app"            # MUST match secret-provider-class.yaml roleName
AUDIENCE="openbao"                        # MUST match secret-provider-class.yaml audience
KV_MOUNT="secret"                         # MUST match OpenBaoKvSecretProvider default mount

MODE="init"
case "${1:-}" in
  --check-only) MODE="check" ;;
  --unseal)     MODE="unseal" ;;
  -h|--help)    sed -n '3,45p' "$0"; exit 0 ;;
  "")           MODE="init" ;;
  *) echo "ERROR: unknown argument: $1 (want --check-only, --unseal, or --help)" >&2; exit 2 ;;
esac

fail() { echo "OPENBAO-INIT FAIL: $*" >&2; exit 1; }
log()  { echo ">> $*"; }
note() { echo "   NOTE: $*"; }

OPENBAO_POD=""
kbao()   { kubectl -n "$NAMESPACE" exec "$OPENBAO_POD" -- bao "$@"; }          # no stdin
kbao_i() { kubectl -n "$NAMESPACE" exec -i "$OPENBAO_POD" -- bao "$@"; }        # stdin piped
ksh_i()  { kubectl -n "$NAMESPACE" exec -i "$OPENBAO_POD" -- sh -c "$1"; }      # sh -c in pod

# Decode one key of the live app Secret to stdout (a READ — audited at Metadata level only).
get_app_secret() {
  kubectl -n "$NAMESPACE" get secret "$APP_SECRET" -o "jsonpath={.data.$1}" 2>/dev/null | base64 -d
}
# Build a JSON object from key/envvar pairs — VALUES come from the environment (never argv).
json_obj() { python3 -c '
import json, os, sys
a = sys.argv[1:]
print(json.dumps({a[i]: os.environ[a[i+1]] for i in range(0, len(a), 2)}))' "$@"; }
# Pull a field out of a bao -format=json response on stdin.
json_get() { python3 -c 'import json,sys; d=json.load(sys.stdin)
[d:=d[k] for k in sys.argv[1].split(".")]; print(d)' "$1"; }

is_enabled() { # is_enabled auth|secrets <mount>  -> prints yes/no
  local kind="$1" mount="$2"
  kbao "$kind" list -format=json 2>/dev/null \
    | python3 -c "import sys,json; print('yes' if '${mount}/' in json.load(sys.stdin) else 'no')" 2>/dev/null || echo "no"
}

# ─────────────────────────────────────────────────────────────────────────────────────────────
# Preflight (fail-closed)
# ─────────────────────────────────────────────────────────────────────────────────────────────
for t in kubectl base64 python3; do command -v "$t" >/dev/null || fail "required tool not on PATH: $t"; done

if [ "$MODE" = "check" ]; then
  if [ -z "${KUBECONFIG:-}" ] || ! kubectl cluster-info >/dev/null 2>&1; then
    note "--check-only: no reachable cluster — validated tools + script only, stopping."
    log "preflight OK (offline)."; exit 0
  fi
fi

[ -n "${KUBECONFIG:-}" ] || fail "KUBECONFIG is unset — export the cluster-admin kubeconfig."
kubectl cluster-info >/dev/null 2>&1 || fail "kubectl cluster-info is not reachable — is KUBECONFIG the live cluster?"

OPENBAO_POD="$(kubectl -n "$NAMESPACE" get pod -l app.kubernetes.io/name=openbao \
  -o jsonpath='{.items[0].metadata.name}' 2>/dev/null || true)"
[ -n "$OPENBAO_POD" ] || fail "no openbao pod found in $NAMESPACE — deploy the overlay first."
kubectl -n "$NAMESPACE" get pod "$OPENBAO_POD" \
  -o jsonpath='{.status.phase}' 2>/dev/null | grep -q Running || fail "openbao pod $OPENBAO_POD is not Running."

kubectl -n "$NAMESPACE" get secret "$APP_SECRET" >/dev/null 2>&1 \
  || fail "$NAMESPACE/$APP_SECRET not found — provision it first (staging-ops.md §1 step 5)."

kubectl get clusterrolebinding openbao-auth-delegator >/dev/null 2>&1 \
  || fail "ClusterRoleBinding openbao-auth-delegator missing — apply the overlay with bd babelstone-zla1.12.31 first (the k8s-auth TokenReview is forbidden without it)."

ENGINE_SA="${ENGINE_SA:-$(kubectl -n "$NAMESPACE" get deploy engine \
  -o jsonpath='{.spec.template.spec.serviceAccountName}' 2>/dev/null || true)}"
[ -n "$ENGINE_SA" ] || ENGINE_SA="default"

log "preflight OK — namespace=$NAMESPACE openbao_pod=$OPENBAO_POD engine_sa=$ENGINE_SA"
if [ "$MODE" = "check" ]; then
  log "--check-only: preflight ran, cluster UNTOUCHED. Stopping before any mutation."; exit 0
fi

# ─────────────────────────────────────────────────────────────────────────────────────────────
# 1 · Initialise (once) + unseal
# ─────────────────────────────────────────────────────────────────────────────────────────────
STATUS="$(kbao status -format=json 2>/dev/null || true)"
INITIALIZED="$(printf '%s' "$STATUS" | python3 -c 'import sys,json; print(json.load(sys.stdin).get("initialized"))' 2>/dev/null || echo unknown)"
SEALED="$(printf '%s' "$STATUS" | python3 -c 'import sys,json; print(json.load(sys.stdin).get("sealed"))' 2>/dev/null || echo unknown)"

if [ "$MODE" = "unseal" ] || [ "$INITIALIZED" = "True" ]; then
  [ "$INITIALIZED" = "True" ] || fail "OpenBao is not initialised yet — run without --unseal for a first init."
  log "OpenBao already initialised."
else
  log "1. bao operator init (shares=$KEY_SHARES threshold=$KEY_THRESHOLD) — writing secret-zero to $INIT_SECRETS_FILE"
  ( umask 077; kbao operator init -key-shares="$KEY_SHARES" -key-threshold="$KEY_THRESHOLD" -format=json > "$INIT_SECRETS_FILE" )
  chmod 600 "$INIT_SECRETS_FILE"
  SEALED="True"
  note "SECRET-ZERO written to $INIT_SECRETS_FILE (mode 0600). MOVE IT OFFLINE and shred the local copy."
fi

[ -f "$INIT_SECRETS_FILE" ] || fail "secret-zero file $INIT_SECRETS_FILE not found — set OPENBAO_INIT_SECRETS to the offline copy."
ROOT_TOKEN="$(python3 -c 'import json,sys; print(json.load(open(sys.argv[1]))["root_token"])' "$INIT_SECRETS_FILE")"
[ -n "$ROOT_TOKEN" ] || fail "could not read root_token from $INIT_SECRETS_FILE."

if [ "$SEALED" != "False" ]; then
  log "2. unseal (threshold=$KEY_THRESHOLD)"
  python3 -c 'import json,sys; [print(k) for k in json.load(open(sys.argv[1]))["unseal_keys_b64"][:int(sys.argv[2])]]' \
    "$INIT_SECRETS_FILE" "$KEY_THRESHOLD" | while IFS= read -r key; do
      printf '%s' "$key" | kbao_i operator unseal - >/dev/null
    done
  kbao status -format=json | python3 -c 'import sys,json; assert json.load(sys.stdin)["sealed"] is False' \
    || fail "OpenBao is still sealed after unseal."
  log "   unsealed."
fi

# Authenticate for the rest of the session (token read from stdin, not argv; persists in-pod).
printf '%s' "$ROOT_TOKEN" | kbao_i login - >/dev/null

if [ "$MODE" = "unseal" ]; then
  log "3. ordered restart: rollout restart deploy/engine"
  kubectl -n "$NAMESPACE" rollout restart deploy/engine
  log "--unseal complete. The engine will go Ready once its CSI mount + AppRole login resolve."
  exit 0
fi

# ─────────────────────────────────────────────────────────────────────────────────────────────
# 3 · Kubernetes auth method + babelstone-app role/policy
# ─────────────────────────────────────────────────────────────────────────────────────────────
log "3. Kubernetes auth method (reviewer = openbao SA, which now has auth-delegator)"
[ "$(is_enabled auth kubernetes)" = "yes" ] || kbao auth enable kubernetes
ksh_i 'bao write auth/kubernetes/config \
  token_reviewer_jwt=@/var/run/secrets/kubernetes.io/serviceaccount/token \
  kubernetes_host="https://$KUBERNETES_SERVICE_HOST:$KUBERNETES_SERVICE_PORT" \
  kubernetes_ca_cert=@/var/run/secrets/kubernetes.io/serviceaccount/ca.crt' >/dev/null

log "4. read-only policy + role $APP_ROLE_NAME over $KV_MOUNT/data/babelstone/* (SA=$ENGINE_SA, audience=$AUDIENCE)"
kbao_i policy write "$APP_ROLE_NAME" - >/dev/null <<POLICY
path "$KV_MOUNT/data/babelstone/*"     { capabilities = ["read"] }
path "$KV_MOUNT/metadata/babelstone/*" { capabilities = ["read", "list"] }
POLICY
kbao write "auth/kubernetes/role/$APP_ROLE_NAME" \
  bound_service_account_names="$ENGINE_SA" \
  bound_service_account_namespaces="$NAMESPACE" \
  audience="$AUDIENCE" \
  token_policies="$APP_ROLE_NAME" \
  token_ttl="1h" >/dev/null

# ─────────────────────────────────────────────────────────────────────────────────────────────
# 5 · KV v2 + populate the app-tier paths (values from babelstone-dev-secrets, over stdin)
# ─────────────────────────────────────────────────────────────────────────────────────────────
log "5. KV v2 at $KV_MOUNT/ + populate app-tier paths (values sourced from $APP_SECRET)"
[ "$(is_enabled secrets "$KV_MOUNT")" = "yes" ] || kbao secrets enable -version=2 -path="$KV_MOUNT" kv

PG="$(get_app_secret POSTGRES_PASSWORD)";  [ -n "$PG" ]  || fail "$APP_SECRET is missing POSTGRES_PASSWORD."
KEK="$(get_app_secret SECRET_VAULT_KEK)";  [ -n "$KEK" ] || fail "$APP_SECRET is missing SECRET_VAULT_KEK."
OIDC="$(get_app_secret OIDC_PRIVATE_KEYS)";[ -n "$OIDC" ]|| fail "$APP_SECRET is missing OIDC_PRIVATE_KEYS."

V="$PG"  json_obj password V             | kbao_i kv put "$KV_MOUNT/babelstone/postgres" - >/dev/null
K="$KEK" O="$OIDC" json_obj secret_vault_kek K oidc_private_keys O \
                                          | kbao_i kv put "$KV_MOUNT/babelstone/logto" - >/dev/null
# The engine reads this DIRECTLY at runtime (OpenBaoKvSecretProvider); the field name MUST be "Engine".
# Password is the SAME PG value Postgres was seeded with — cross-store consistency by construction.
CONN="Host=postgres;Port=5432;Database=babelstone;Username=babelstone;Password=$PG"
V="$CONN" json_obj Engine V              | kbao_i kv put "$KV_MOUNT/Engine" - >/dev/null

# ─────────────────────────────────────────────────────────────────────────────────────────────
# 6 · Transit engine + the engine's transit token
# ─────────────────────────────────────────────────────────────────────────────────────────────
log "6. transit engine + scoped token for the per-subject PII crypto-shred boundary"
[ "$(is_enabled secrets transit)" = "yes" ] || kbao secrets enable transit
kbao_i policy write engine-transit - >/dev/null <<'POLICY'
path "transit/keys/pii-*"        { capabilities = ["create", "read", "update", "delete"] }
path "transit/encrypt/pii-*"     { capabilities = ["update"] }
path "transit/decrypt/pii-*"     { capabilities = ["update"] }
path "transit/keys/pii-*/config" { capabilities = ["update"] }
POLICY
TRANSIT_TOKEN="$(kbao token create -policy=engine-transit -period=24h -format=json | json_get auth.client_token)"
V="$TRANSIT_TOKEN" json_obj token V | kbao_i kv put "$KV_MOUNT/babelstone/engine-transit" - >/dev/null

# ─────────────────────────────────────────────────────────────────────────────────────────────
# 7 · AppRole anchor for the engine's own KV read (secret/data/Engine)
# ─────────────────────────────────────────────────────────────────────────────────────────────
log "7. AppRole engine-kv (role_id + secret_id) → $KV_MOUNT/babelstone/engine-approle"
[ "$(is_enabled auth approle)" = "yes" ] || kbao auth enable approle
kbao_i policy write engine-kv - >/dev/null <<POLICY
path "$KV_MOUNT/data/Engine" { capabilities = ["read"] }
POLICY
kbao write auth/approle/role/engine-kv token_policies="engine-kv" token_ttl="1h" token_max_ttl="4h" >/dev/null
ROLE_ID="$(kbao read -format=json auth/approle/role/engine-kv/role-id | json_get data.role_id)"
SECRET_ID="$(kbao write -f -format=json auth/approle/role/engine-kv/secret-id | json_get data.secret_id)"
R="$ROLE_ID" S="$SECRET_ID" json_obj role_id R secret_id S \
  | kbao_i kv put "$KV_MOUNT/babelstone/engine-approle" - >/dev/null

# ─────────────────────────────────────────────────────────────────────────────────────────────
# 8 · Verify + ordered restart
# ─────────────────────────────────────────────────────────────────────────────────────────────
log "8. verify"
kbao kv get -field=Engine "$KV_MOUNT/Engine" >/dev/null || fail "readback of $KV_MOUNT/Engine failed."
kbao kv get -field=role_id "$KV_MOUNT/babelstone/engine-approle" >/dev/null || fail "readback of engine-approle failed."
log "   KV paths populated and readable."

log "9. rollout restart deploy/engine (so its CSI mount + AppRole login resolve)"
kubectl -n "$NAMESPACE" rollout restart deploy/engine

echo
log "OpenBao init: OK."
note "Engine goes Ready once the CSI volume syncs babelstone-app-secrets and secret/data/Engine resolves:"
note "  kubectl -n $NAMESPACE get pods -l app.kubernetes.io/name=engine"
note "SECRET-ZERO is in $INIT_SECRETS_FILE — move it OFFLINE and shred the local copy now."
note "After any node reboot OpenBao comes up SEALED: re-run with --unseal (needs OPENBAO_INIT_SECRETS)."
