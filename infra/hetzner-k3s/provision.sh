#!/usr/bin/env bash
# infra/hetzner-k3s/provision.sh — fail-closed wrapper around `hetzner-k3s create`
# (bd babelstone-zla1.12.6; ADR-IC-006 posture — minimise public surface; CIS host hardening).
#
# In plain English: cluster.yaml ships with the SSH allow-list as the literal sentinel
# REPLACE_ME/32. The danger is what a hurried operator does when `create` chokes on that
# invalid value — the quickest "fix" is typing 0.0.0.0/0, which opens the SSH port to the
# whole internet on the one machine holding etcd, the cluster-admin credential, and the
# cloud token. This wrapper makes the safe path the only path: the SSH source comes from
# a REQUIRED environment variable (like HCLOUD_TOKEN already does), it is validated as an
# explicit /32 (or a short comma-separated list of them), the sentinel is substituted
# into a rendered, gitignored copy, and provisioning REFUSES to run when the value is
# unset, still REPLACE_ME, world-open (0.0.0.0/0), or not a /32.
#
# The committed cluster.yaml stays the single template (the sentinel line is load-bearing:
# this script replaces it and fails if it is missing — hand-editing the committed file is
# the drift this guard exists to prevent). The k8s API list's deliberate 0.0.0.0/0 is a
# DOCUMENTED trade-off (see cluster.yaml) and is out of this guard's scope — only the SSH
# list is gated.
#
# Required env:
#   HCLOUD_TOKEN       read/write Hetzner Cloud API token (consumed by hetzner-k3s itself to
#                      CREATE the cluster). hetzner-k3s plants this token as the kube-system
#                      `hcloud` Secret on EVERY create — regardless of the addon toggles
#                      (verified live on v2.6.0: it lands even with the CCM + CSI addons off in
#                      cluster.yaml, bd babelstone-zla1.12.20). With those addons off nothing
#                      consumes it, so it sits orphaned — and THIS SCRIPT scrubs it post-create
#                      (step 5) so no Hetzner API credential persists in-cluster.
#   SSH_ALLOWED_CIDR   the operator / jump-host IPv4 /32 allowed to SSH — e.g. 203.0.113.7/32
#                      (comma-separate a small list: "203.0.113.7/32,198.51.100.2/32")
#
# Usage:
#   cd infra/hetzner-k3s
#   export HCLOUD_TOKEN=...
#   export SSH_ALLOWED_CIDR=203.0.113.7/32
#   ./provision.sh               # preflight + render + `hetzner-k3s create`
#   ./provision.sh --check-only  # preflight + render only (no create; e.g. CI or a dry run)
set -euo pipefail

cd "$(dirname "$0")"

TEMPLATE="cluster.yaml"
RENDERED="cluster.rendered.yaml"   # gitignored — never commit the rendered config
SENTINEL="REPLACE_ME/32"
CHECK_ONLY=false

case "${1:-}" in
  "") ;;
  --check-only) CHECK_ONLY=true ;;
  -h|--help) sed -n '2,31p' "$0" | sed 's/^# \{0,1\}//'; exit 0 ;;
  *) echo "ERROR: unknown argument: $1 (only --check-only is supported)" >&2; exit 2 ;;
esac

fail() { echo "PROVISION PREFLIGHT FAIL: $*" >&2; exit 1; }

# ── 1 · required env, fail closed ────────────────────────────────────────────────────
if ! $CHECK_ONLY; then
  [ -n "${HCLOUD_TOKEN:-}" ] || fail "HCLOUD_TOKEN is unset — export the read/write Hetzner Cloud API token (never commit it)"
fi
[ -n "${SSH_ALLOWED_CIDR:-}" ] \
  || fail "SSH_ALLOWED_CIDR is unset — export your operator/jump-host /32 (e.g. 203.0.113.7/32). Refusing to guess; NEVER use 0.0.0.0/0."

# ── 2 · validate every entry: an explicit IPv4 /32, never world-open ─────────────────
validate_cidr() {
  local cidr="$1"
  case "$cidr" in
    *REPLACE_ME*) fail "SSH_ALLOWED_CIDR is still the REPLACE_ME sentinel — set your real operator /32" ;;
    0.0.0.0/*)    fail "SSH_ALLOWED_CIDR '$cidr' is world-open (0.0.0.0/…) — the SSH port must never be open to the internet" ;;
  esac
  [[ "$cidr" =~ ^([0-9]{1,3})\.([0-9]{1,3})\.([0-9]{1,3})\.([0-9]{1,3})/32$ ]] \
    || fail "SSH_ALLOWED_CIDR '$cidr' is not an explicit IPv4 /32 (a.b.c.d/32) — broader masks are refused by design"
  local octet
  for octet in "${BASH_REMATCH[1]}" "${BASH_REMATCH[2]}" "${BASH_REMATCH[3]}" "${BASH_REMATCH[4]}"; do
    [ "$octet" -le 255 ] || fail "SSH_ALLOWED_CIDR '$cidr' has an out-of-range octet ($octet)"
  done
}

CIDRS=()
IFS=',' read -r -a RAW_CIDRS <<< "$SSH_ALLOWED_CIDR"
for raw in "${RAW_CIDRS[@]}"; do
  cidr="$(printf '%s' "$raw" | tr -d '[:space:]')"
  [ -n "$cidr" ] || continue
  validate_cidr "$cidr"
  CIDRS+=("$cidr")
done
[ "${#CIDRS[@]}" -gt 0 ] || fail "SSH_ALLOWED_CIDR parsed to an empty list"

# ── 3 · render: substitute the sentinel line with the validated entries ──────────────
grep -q "$SENTINEL" "$TEMPLATE" \
  || fail "$TEMPLATE no longer carries the $SENTINEL sentinel — the committed file must stay the template; supply the real CIDR via SSH_ALLOWED_CIDR, don't hand-edit"

ENTRIES=""
for cidr in "${CIDRS[@]}"; do
  ENTRIES="${ENTRIES}      - ${cidr}\n"
done
awk -v entries="$ENTRIES" -v sentinel="$SENTINEL" '
  index($0, sentinel) > 0 { printf "%s", entries; next }
  { print }
' "$TEMPLATE" > "$RENDERED"

# ── 4 · post-render assert on the rendered ssh allow-list (defence in depth) ─────────
# Extract exactly the networking.allowed_networks.ssh list entries (NOT the api list,
# whose 0.0.0.0/0 is the documented trade-off) and re-check each one.
SSH_LIST="$(awk '
  /^  allowed_networks:/         { in_an = 1; next }
  in_an && /^  [^ ]/             { in_an = 0 }
  in_an && /^    ssh:/           { in_ssh = 1; next }
  in_an && in_ssh && /^    [^ ]/ { in_ssh = 0 }
  in_an && in_ssh && /^      - / { sub(/^      - /, ""); sub(/[[:space:]]*#.*$/, ""); print }
' "$RENDERED")"
[ -n "$SSH_LIST" ] || fail "rendered $RENDERED has an EMPTY networking.allowed_networks.ssh list"
while IFS= read -r entry; do
  case "$entry" in
    *REPLACE_ME*)  fail "rendered ssh allow-list still contains the sentinel: $entry" ;;
    0.0.0.0/*)     fail "rendered ssh allow-list is world-open: $entry" ;;
    */32) ;;
    *)             fail "rendered ssh allow-list entry is not a /32: $entry" ;;
  esac
done <<< "$SSH_LIST"

echo "preflight OK: SSH allow-list = ${SSH_LIST//$'\n'/ } (explicit /32s, no sentinel, not world-open)"
echo "rendered config → $RENDERED (gitignored)"

if $CHECK_ONLY; then
  echo "--check-only: stopping before create."
  exit 0
fi

command -v hetzner-k3s >/dev/null \
  || fail "hetzner-k3s not found on PATH — install it first (see ./README.md prerequisites)"

echo "creating the cluster: hetzner-k3s create --config $RENDERED"
hetzner-k3s create --config "$RENDERED"

# ── 5 · scrub the unconditionally-planted Hetzner token Secret (bd babelstone-zla1.12.20) ──
# The box now EXISTS with the Hetzner token in a kube-system Secret. Fail-CLOSED throughout:
#   • trap (the "finally"): any early exit from here fails LOUD with the manual remediation;
#   • targeted delete of the kube-system/hcloud Secret hetzner-k3s v2.6 plants;
#   • then a NAME- AND VERSION-INDEPENDENT proof: the token VALUE must be absent from EVERY
#     kube-system Secret — so a renamed Secret on a future hetzner-k3s cannot produce a false
#     all-clear, and a kubectl/API error is a hard fail (never an all-clear), not a warning.
KUBECONFIG_PATH="./kubeconfig"; TOKEN_NS="kube-system"; TOKEN_SECRET="hcloud"
warn_unscrubbed() {
  echo "" >&2
  echo "!!! SECURITY: cluster CREATED but the Hetzner token was NOT confirmed removed from ${TOKEN_NS}." >&2
  echo "!!! The all-powerful read/write Hetzner token may still be in-cluster. Inspect + remove NOW:" >&2
  echo "!!!   kubectl --kubeconfig $PWD/$KUBECONFIG_PATH -n ${TOKEN_NS} get secret -o name" >&2
  echo "!!!   kubectl --kubeconfig $PWD/$KUBECONFIG_PATH -n ${TOKEN_NS} delete secret ${TOKEN_SECRET}" >&2
  echo "!!! Do not expose the box until confirmed clean." >&2
}
trap warn_unscrubbed EXIT
command -v kubectl >/dev/null || fail "kubectl not found on PATH — cannot scrub the Hetzner token Secret"
echo "scrubbing the kube-system Hetzner token Secret (CCM/CSI off -> nothing consumes it)"
kubectl --kubeconfig "$KUBECONFIG_PATH" -n "$TOKEN_NS" delete secret "$TOKEN_SECRET" --ignore-not-found
# name/version-independent proof: the token VALUE must be gone from EVERY kube-system Secret.
token_b64="$(printf '%s' "$HCLOUD_TOKEN" | base64 | tr -d '\n')"
secrets_json="$(kubectl --kubeconfig "$KUBECONFIG_PATH" -n "$TOKEN_NS" get secret -o json)" \
  || fail "could not enumerate ${TOKEN_NS} Secrets to verify the token is gone (kubectl/API error) -- do NOT expose the box"
if printf '%s' "$secrets_json" | grep -qF "$token_b64"; then
  fail "the Hetzner API token is STILL present in a ${TOKEN_NS} Secret (possibly under a name other than '${TOKEN_SECRET}') -- remove it before exposing the box"
fi
unset token_b64 secrets_json
trap - EXIT
echo "confirmed: the Hetzner API token is absent from all ${TOKEN_NS} Secrets"
