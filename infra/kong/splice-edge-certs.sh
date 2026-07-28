#!/usr/bin/env bash
# Splice real mcp-mtls-ca client-cert material over kong.yml's committed POC edge
# certs, at Kong POD START (bd babelstone-fhw2.1; ADR-IC-006 §P1/§P5).
#
# In plain English: Kong's edge config (infra/kong/kong.yml, DB-less declarative,
# ADR-IC-006 §P1) ships THROWAWAY POC client certs on the engine/orchestrator and
# mcp-server upstreams. On a real deploy scripts/deck-sync.sh replaces them with
# internal-CA material pulled from OpenBao. But on STAGING deck-sync is skipped
# (the -dev OpenBao is empty; real edge provisioning is deferred to M.2/bd puu3),
# so Kong keeps the POC certs — which do NOT chain to the cluster-generated
# mcp-mtls-ca. Since bd babelstone-zla1.12.25 the engine + orchestrator REQUIRE a
# client cert that chains to mcp-mtls-ca, and mcp-server always has, so the
# Kong-fronted public path (api.babelstone.dev → engine/orchestrator/mcp-server) is
# rejected at the TLS handshake.
#
# This is the staging bridge that closes that gap WITHOUT OpenBao or deck-sync: an
# initContainer mounts cert-manager Secrets holding real mcp-mtls-ca material and
# runs this script to splice it over the POC placeholders BY ENTITY ID, writing the
# rendered config Kong then loads. It is the pod-start mirror of deck-sync's
# splice_pem — the SAME id-anchored, SURGICAL block-scalar replace: it rewrites ONLY
# the targeted `cert:`/`key:` block bodies and leaves every other byte (the Lua
# pre-functions, routes, plugins, comments) untouched, so a whole-file YAML
# reserialization can never mangle the embedded Lua. tls_verify is NOT flipped here
# (see the deployment patch header for why the engine/orchestrator reverse half is
# out of scope for this bridge).
#
# Entities spliced (ids are the stable placeholders asserted in kong.yml + deck-sync.sh):
#   7e9b6f1a-…  certificates   cert+key  ← Kong→engine/orchestrator client cert
#                                          (cert-manager kong-engine-client, /edge-certs/engine)
#   a1b2c3d4-…  certificates   cert+key  ← Kong→mcp-server client cert
#                                          (cert-manager mcp-kong-client,  /edge-certs/mcp)
#   f0e1d2c3-…  ca_certificates cert     ← mcp-mtls-ca bundle Kong verifies mcp-server against
#                                          (the mounted Secrets' ca.crt — cert-manager's issuing CA)
#
# Fail-closed: an empty source PEM, a missing entity/key, or a leftover POC body all
# abort the pod before Kong loads a half-spliced or still-POC config.
set -euo pipefail

SRC="${KONG_SRC:-/kong-src/kong.yml}"          # the committed POC kong.yml (ConfigMap, read-only)
OUT="${KONG_OUT:-/kong-rendered/kong.yml}"     # the rendered config Kong loads (shared emptyDir)
ENGINE_DIR="${ENGINE_CERT_DIR:-/edge-certs/engine}"  # kong-engine-client-tls Secret (tls.crt/tls.key/ca.crt)
MCP_DIR="${MCP_CERT_DIR:-/edge-certs/mcp}"           # mcp-kong-client-tls Secret   (tls.crt/tls.key/ca.crt)

# Entity ids the POC placeholders carry in kong.yml (stable, asserted by the edge
# fitness gate + deck-sync.sh — keep in lock-step with both).
EDGE_CERT_ID="7e9b6f1a-2c4d-4e8a-9b1f-0a2c4d6e8f10"   # engine/orchestrator client cert
MCP_CERT_ID="a1b2c3d4-e5f6-7890-abcd-ef1234567890"    # mcp-server client cert
CA_BUNDLE_ID="f0e1d2c3-b4a5-6789-cdef-012345678901"   # ca_certificates bundle (mcp-server verify)

# The first body line of each committed POC artifact — the exact sentinels deck-sync.sh
# guards on, EXTENDED to the two POC private keys so the guard covers cert AND key
# material (its stated invariant: NONE of the committed POC bodies may survive). If any
# survives the splice the injection did not take, so we abort rather than let Kong present
# a POC cert/key the engine will reject (fail-closed, §P5).
POC_SENTINELS=(
  "MIIDHTCCAgWgAwIBAgIUfaHM5ukIJp1MpqoI0c15PdeDE8I"          # edge POC cert   (7e9b6f1a)
  "MIIDRDCCAiygAwIBAgIUTgyYJEkrDSCIaoGXYaVnIBrFvIs"          # mcp  POC cert   (a1b2c3d4)
  "MIIDEDCCAfigAwIBAgIUfYZUFJ8QHSp6YHfjTLOVypx+st0"          # CA bundle POC   (f0e1d2c3)
  "MIIEvAIBADANBgkqhkiG9w0BAQEFAASCBKYwggSiAgEAAoIBAQCbbwOc44IupKLE"   # edge POC key (7e9b6f1a)
  "MIIEvgIBADANBgkqhkiG9w0BAQEFAASCBKgwggSkAgEAAoIBAQDjbbtJVolg9biy"   # mcp  POC key (a1b2c3d4)
)

die() { rm -f "${OUT:-}.tmp" 2>/dev/null || true; echo "splice-edge-certs: $*" >&2; exit 1; }

[ -r "$SRC" ] || die "source kong.yml '$SRC' is not readable"

# Replace one id-anchored `<key>: |` block scalar with the PEM in $pemfile. The awk
# is a faithful port of deck-sync.sh's splice_pem: locate the entity by its id line,
# find `<key>: |` before the entity ends (a column-0 section key, or a sibling list
# item at or below the entity's own indent), then swap the more-indented block body —
# re-indenting the new PEM to the key's indent + 2, exactly as kong.yml carries it.
splice() { # entity_id  yaml_key  pem_file
  local id="$1" key="$2" pemfile="$3"
  [ -s "$pemfile" ] || die "PEM file '$pemfile' is empty/missing (Secret not mounted?)"
  awk -v id="$id" -v key="$key" -v pemfile="$pemfile" '
    BEGIN {
      n = 0
      while ((getline line < pemfile) > 0) pem[n++] = line
      while (n > 0 && pem[n-1] ~ /^[ \t]*$/) n--       # drop trailing blank lines
      if (n == 0) { print "splice: PEM " pemfile " has no content" > "/dev/stderr"; exit 3 }
      inEntity = 0; done = 0; skipping = 0
    }
    # Locate the entity: a list item / mapping bearing `id: <id>` (never yet in an entity).
    (!inEntity && !done && $0 ~ ("^[ \t]*-?[ \t]*id:[ \t]*" id "[ \t]*$")) {
      inEntity = 1
      match($0, /^[ \t]*/); entityIndent = RLENGTH
      print; next
    }
    # Inside the target entity, before the swap: watch for its end or its `<key>: |`.
    (inEntity && !done) {
      if ($0 ~ /^[A-Za-z_]/) { inEntity = 0; print; next }      # next top-level section
      if ($0 ~ /^[ \t]*-[ \t]/) {                               # a list item…
        match($0, /^[ \t]*/)
        if (RLENGTH <= entityIndent) { inEntity = 0; print; next }   # …a sibling entity → past it
      }
      if ($0 ~ ("^[ \t]+" key ":[ \t]*\\|[ \t]*$")) {           # the block scalar to replace
        print
        match($0, /^[ \t]*/); keyIndent = RLENGTH
        body = ""; for (i = 0; i < keyIndent + 2; i++) body = body " "
        for (i = 0; i < n; i++) print body pem[i]
        done = 1; skipping = 1; next
      }
      print; next
    }
    # Consume the old block-scalar body: blank lines + lines more-indented than the key.
    (skipping) {
      if ($0 ~ /^[ \t]*$/) next
      match($0, /^[ \t]*/)
      if (RLENGTH > keyIndent) next
      skipping = 0; print; next
    }
    { print }
    END {
      if (!done) { print "splice: entity " id " key " key " not found" > "/dev/stderr"; exit 4 }
    }
  ' "$OUT" > "$OUT.tmp" || die "awk splice of $id/$key failed"
  mv -f "$OUT.tmp" "$OUT"
}

# Render from a fresh copy of the committed config, then splice the real material by id.
mkdir -p "$(dirname "$OUT")"
cp -f "$SRC" "$OUT"

splice "$EDGE_CERT_ID" cert "$ENGINE_DIR/tls.crt"   # Kong→engine/orchestrator client cert
splice "$EDGE_CERT_ID" key  "$ENGINE_DIR/tls.key"
splice "$MCP_CERT_ID"  cert "$MCP_DIR/tls.crt"       # Kong→mcp-server client cert
splice "$MCP_CERT_ID"  key  "$MCP_DIR/tls.key"
splice "$CA_BUNDLE_ID" cert "$MCP_DIR/ca.crt"        # mcp-mtls-ca bundle (mcp-server upstream verify)

# Fail-closed guard: NONE of the committed POC bodies may survive (deck-sync.sh's discipline).
for s in "${POC_SENTINELS[@]}"; do
  if grep -qF "$s" "$OUT"; then
    die "rendered config still carries a POC PEM body ($s) — the splice did not replace it"
  fi
done

echo "splice-edge-certs: rendered $OUT with real mcp-mtls-ca edge material (POC placeholders replaced)" >&2
