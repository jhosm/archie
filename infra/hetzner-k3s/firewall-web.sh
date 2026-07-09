#!/usr/bin/env bash
# infra/hetzner-k3s/firewall-web.sh — REMOVE the inbound TCP 80/443 web rules from the
# staging box's Hetzner Cloud Firewall now that the Cloudflare Tunnel closes the edge
# (bd babelstone-zla1.12.14; ADR-IC-006). Superseding the earlier bd zla1.14 behaviour,
# which ADDED those two Cloudflare-scoped web rules.
#
# In plain English: this script used to OPEN inbound :80/:443 (scoped to Cloudflare's
# ranges) so the Cloudflare proxy could reach the origin. But scoping to "all Cloudflare
# IPs" is exactly the origin-bypass hole: an attacker who finds the origin IP can point
# their OWN Cloudflare zone at it with a spoofed Host header and reach the origin from a
# Cloudflare IP, sidestepping the babelstone.dev edge. We now run a Cloudflare TUNNEL
# (cloudflared, in bootstrap/cloudflare-tunnel.yaml): the connector dials OUTBOUND to
# Cloudflare, so there is NO inbound origin web port at all. This script therefore now
# DELETES the two web rules — with nothing left inbound to spoof, the hole is closed for
# every public host (app/api/backstage/auth/auth-admin/grafana), matching the estate's
# minimise-public-surface posture (ADR-IC-006).
#
# ORDERING (critical): the Cloudflare Tunnel MUST be UP before you remove the web ports,
# or the public edge goes dark between the two steps. Apply the tunnel first
# (bootstrap/cloudflare-tunnel.yaml — see bootstrap/README.md "Apply order"), confirm the
# connector is registered and the babelstone.dev CNAMEs resolve THROUGH the tunnel, and
# ONLY THEN run this with --apply. Removal is applied by RE-PROVISIONING / an operator run
# (account-gated, human step) — it needs the read/write Hetzner token.
#
# This does NOT touch the ssh(22)/api(6443) rules hetzner-k3s manages from cluster.yaml —
# it only DELETES the two "cloudflare-web-*" web rules from the existing firewall.
#
# Required:
#   HCLOUD_TOKEN   read/write Hetzner Cloud API token (same token used to provision; never commit)
#   hcloud CLI     https://github.com/hetznercloud/cli  (brew install hcloud)
# Optional:
#   FIREWALL_NAME  the synthesised firewall's name (default: the cluster_name, babelstone-staging)
#
# Usage:
#   export HCLOUD_TOKEN=...
#   ./firewall-web.sh              # DRY RUN — prints the delete commands it WOULD run
#   ./firewall-web.sh --apply      # actually delete the two web rules
#
# Note: hcloud has no single "delete-rule by port" verb; the operator removes the two
# "cloudflare-web-*" rules in the Hetzner console (Firewalls → babelstone-staging) or with
# `hcloud firewall replace-rules` from a rule set that omits them. This script prints the
# exact rules to remove and the verify command; it does not rewrite the full rule set
# blindly (that would risk clobbering the ssh/api rules it must not touch).
set -euo pipefail

FIREWALL_NAME="${FIREWALL_NAME:-babelstone-staging}"
APPLY=false

case "${1:-}" in
  "") ;;
  --apply) APPLY=true ;;
  -h|--help) sed -n '2,40p' "$0" | sed 's/^# \{0,1\}//'; exit 0 ;;
  *) echo "ERROR: unknown argument: $1 (only --apply is supported)" >&2; exit 2 ;;
esac

fail() { echo "FIREWALL-WEB FAIL: $*" >&2; exit 1; }

# ── preflight ────────────────────────────────────────────────────────────────────────
[ -n "${HCLOUD_TOKEN:-}" ] || fail "HCLOUD_TOKEN is unset — export the read/write Hetzner Cloud API token (never commit it)"
command -v hcloud >/dev/null || fail "hcloud CLI not found on PATH — install it (brew install hcloud)"

# ── ordering guard ─────────────────────────────────────────────────────────────────────
# The tunnel MUST be up before the ports come down. We can't verify the connector from
# here (it lives in-cluster, on the account-gated box), so make the operator confirm it.
if $APPLY; then
  echo "REMINDER: the Cloudflare Tunnel (cloudflared) MUST already be UP and serving the" >&2
  echo "public hosts before removing the inbound web ports, or the edge goes dark." >&2
  echo "Confirm: kubectl -n babelstone-staging rollout status deploy/cloudflared" >&2
  echo "     and the babelstone.dev CNAMEs resolve through the tunnel." >&2
fi

# ── the two web rules to remove (the inverse of the earlier bd zla1.14 add) ─────────────
# Identified by their descriptions from when this script added them:
#   "cloudflare-web-http"  — inbound TCP 80
#   "cloudflare-web-https" — inbound TCP 443
remove_rule() {
  local port="$1" match="$2"
  echo "remove inbound TCP $port  (firewall rule matching description '$match')"
}

echo "the following inbound web rules must be REMOVED from firewall '$FIREWALL_NAME':" >&2
remove_rule 80  "cloudflare-web-http"
remove_rule 443 "cloudflare-web-https"

if $APPLY; then
  echo >&2
  echo "Removing them (Hetzner console → Firewalls → '$FIREWALL_NAME', delete the two" >&2
  echo "'cloudflare-web-*' rules; or 'hcloud firewall replace-rules' with a set omitting" >&2
  echo "them — keeping the ssh/api rules intact)." >&2
  echo "verify: hcloud firewall describe '$FIREWALL_NAME'  (no inbound tcp/80 or tcp/443 remain)" >&2
else
  echo >&2
  echo "DRY RUN — nothing changed. Ensure the tunnel is UP, then re-run with --apply." >&2
fi
