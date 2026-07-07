#!/usr/bin/env bash
# infra/hetzner-k3s/firewall-web.sh — open inbound TCP 80/443 on the staging box's
# Hetzner Cloud Firewall, scoped to Cloudflare's published edge ranges (bd babelstone-zla1.14).
#
# In plain English: the public URLs were dead partly because the Hetzner firewall blocked
# :80/:443. hetzner-k3s SYNTHESISES that firewall from cluster.yaml's `networking.allowed_networks`,
# which can only express the `ssh` and `api` lists — there is no knob for the web ports there.
# So the web rule has to be added out-of-band, and this script is the firewall-as-code that does
# it: it fetches Cloudflare's current IP ranges and adds two inbound rules (80 and 443) scoped to
# them, so ONLY the Cloudflare proxy can reach the origin — direct internet scans of the node IP
# are refused, matching the estate's minimise-public-surface posture (ADR-IC-006).
#
# Why scope to Cloudflare and not 0.0.0.0/0: the four A records sit behind the Cloudflare proxy
# (orange-cloud), so all legitimate :80/:443 traffic arrives FROM Cloudflare. Locking the origin
# to Cloudflare's ranges hides it from the open internet. The trade-off — the SAME one already
# documented for the GitHub Actions ranges on the k8s API in cluster.yaml — is that Cloudflare's
# list ROTATES: re-run this at each provision, and after Cloudflare publishes range changes, or a
# stale rule silently drops traffic. See "Refreshing" below.
#
# This does NOT touch the ssh/api rules hetzner-k3s manages from cluster.yaml — it only ADDS the
# two web rules to the existing firewall (non-destructive).
#
# Required:
#   HCLOUD_TOKEN   read/write Hetzner Cloud API token (same token used to provision; never commit)
#   hcloud CLI     https://github.com/hetznercloud/cli  (brew install hcloud)
# Optional:
#   FIREWALL_NAME  the synthesised firewall's name (default: the cluster_name, babelstone-staging)
#
# Usage:
#   export HCLOUD_TOKEN=...
#   ./firewall-web.sh              # DRY RUN — prints the hcloud commands it WOULD run
#   ./firewall-web.sh --apply      # actually add the two rules
#
# Refreshing rotated ranges: remove the two prior "cloudflare-web-*" rules first (Hetzner
# console → Firewalls, or `hcloud firewall delete-rule`), then re-run with --apply. Re-running
# without removing the old rules just appends duplicates.
set -euo pipefail

FIREWALL_NAME="${FIREWALL_NAME:-babelstone-staging}"
CF_V4_URL="https://www.cloudflare.com/ips-v4"
CF_V6_URL="https://www.cloudflare.com/ips-v6"
APPLY=false

case "${1:-}" in
  "") ;;
  --apply) APPLY=true ;;
  -h|--help) sed -n '2,38p' "$0" | sed 's/^# \{0,1\}//'; exit 0 ;;
  *) echo "ERROR: unknown argument: $1 (only --apply is supported)" >&2; exit 2 ;;
esac

fail() { echo "FIREWALL-WEB FAIL: $*" >&2; exit 1; }

# ── preflight ────────────────────────────────────────────────────────────────────────
[ -n "${HCLOUD_TOKEN:-}" ] || fail "HCLOUD_TOKEN is unset — export the read/write Hetzner Cloud API token (never commit it)"
command -v hcloud >/dev/null || fail "hcloud CLI not found on PATH — install it (brew install hcloud)"
command -v curl   >/dev/null || fail "curl not found on PATH"

# ── fetch Cloudflare's current edge ranges ─────────────────────────────────────────────
echo "fetching Cloudflare IP ranges ($CF_V4_URL, $CF_V6_URL) …" >&2
CF_V4="$(curl -fsS "$CF_V4_URL")" || fail "could not fetch $CF_V4_URL"
CF_V6="$(curl -fsS "$CF_V6_URL")" || fail "could not fetch $CF_V6_URL"

SOURCE_ARGS=()
while IFS= read -r cidr; do
  cidr="$(printf '%s' "$cidr" | tr -d '[:space:]')"
  [ -n "$cidr" ] || continue
  SOURCE_ARGS+=(--source-ips "$cidr")
done <<< "$CF_V4"$'\n'"$CF_V6"

[ "${#SOURCE_ARGS[@]}" -gt 0 ] || fail "parsed an EMPTY Cloudflare range list — refusing to add a rule with no sources"
echo "→ ${#SOURCE_ARGS[@]} source ranges (v4+v6) from Cloudflare" >&2

# ── add one inbound rule per web port, scoped to those ranges ───────────────────────────
add_rule() {
  local port="$1" desc="$2"
  local cmd=(hcloud firewall add-rule "$FIREWALL_NAME"
    --direction in --protocol tcp --port "$port"
    --description "$desc" "${SOURCE_ARGS[@]}")
  if $APPLY; then
    echo "+ ${cmd[*]}" >&2
    "${cmd[@]}"
  else
    echo "${cmd[*]}"
  fi
}

add_rule 80  "cloudflare-web-http (bd zla1.14 — HTTP→HTTPS redirect at Traefik)"
add_rule 443 "cloudflare-web-https (bd zla1.14 — public TLS edge)"

if $APPLY; then
  echo "done: added inbound TCP 80 + 443 (Cloudflare-scoped) to firewall '$FIREWALL_NAME'." >&2
  echo "verify: hcloud firewall describe '$FIREWALL_NAME'" >&2
else
  echo >&2
  echo "DRY RUN — nothing changed. Re-run with --apply to add the two rules above." >&2
fi
