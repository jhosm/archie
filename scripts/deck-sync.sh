#!/usr/bin/env bash
# scripts/deck-sync.sh — the deploy-time declarative Kong `deck sync` for the P.6
# Kubernetes environments (Q.6 / bd babelstone-4c81), with real edge mTLS + IAM key
# material injected from the OpenBao secret boundary at sync time (bd babelstone-4c81.1).
#
# In plain English: Kong's whole edge config is one git-tracked file
# (infra/kong/kong.yml, DB-less declarative mode, ADR-IC-006 §P1). The cert/key and the
# IAM signing public key committed in that file are deliberately THROWAWAY POC
# placeholders (CN=babelstone-edge-poc) — present only so the config parses and CI can
# assert the boundary is wired. They must NEVER reach a real environment. This script is
# the deploy-time half: it pulls the real internal-CA-signed gateway client certs, the
# internal CA bundle, and the IAM's real RS256 signing public key from OpenBao (the M.2
# secret boundary, bd babelstone-puu3), renders them into a throwaway copy of kong.yml
# replacing the placeholders by entity id, flips `tls_verify` to true on the
# orchestrator/engine upstreams now that the CA bundle is mounted, and runs `deck sync`.
# The secret material lives only in a tmpfs render file that is shredded on exit — it is
# never written back to the repo and never committed (memory: PII/secrets off the bus;
# ADR-IC-006 §P5/§P7).
#
# It honours, mechanically:
#   • ADR-IC-006 §P1 — DB-less declarative; the ENTIRE config is rendered + `deck sync`ed.
#   • ADR-IC-006 §P5 — Boundary 2 upstream mTLS: real internal-CA client cert injected;
#     `tls_verify: true` flipped on once the internal CA bundle is mounted (the reverse
#     half — Kong VERIFIES the upstream server cert, not just presents its own).
#   • ADR-IC-006 §P7 — JWT key registration: the IAM's real RS256 signing public key
#     replaces the POC placeholder; rotation is the deliberate 3-step sync (see --rotate).
#
# Source of the secret material is the OpenBao KV boundary (ADR-PC-004 / M.2). Paths
# (overridable by env so a real deployment can re-map them without editing this script):
#
#   BAO_KONG_EDGE_CERT_PATH   default secret/data/babelstone/edge/kong-client-cert
#       → fields: cert (PEM chain), key (PEM private key)         [entity 7e9b6f1a-…]
#   BAO_KONG_MCP_CERT_PATH    default secret/data/babelstone/edge/kong-mcp-client-cert
#       → fields: cert, key                                       [entity a1b2c3d4-…]
#   BAO_INTERNAL_CA_PATH      default secret/data/babelstone/edge/internal-ca-bundle
#       → field:  cert (the internal CA bundle PEM)               [entity f0e1d2c3-…]
#   BAO_IAM_JWT_PUBKEY_PATH   default secret/data/babelstone/edge/iam-jwt-public-key
#       → field:  rsa_public_key (the IAM RS256 signing PUBLIC key PEM, + optional
#                 rsa_public_key_next for the §P7 rotation overlap window)
#
# OpenBao access (the M.2 seam — same env the bao CLI reads):
#   BAO_ADDR   the OpenBao API address (e.g. http://openbao:8200)
#   BAO_TOKEN  a token with read on the paths above (a deploy-scoped policy, never root)
#
# Usage:
#   scripts/deck-sync.sh --dry-run                # CI gate: prove the render PATH end-to-end
#                                                 #   WITHOUT a live OpenBao or real secrets.
#                                                 #   Synthesises throwaway PEM in-process,
#                                                 #   exercises the splice + tls_verify flip +
#                                                 #   placeholder-gone guards + deck validate.
#                                                 #   No network, no secrets, no live Kong.
#   scripts/deck-sync.sh --render-only            # render kong.yml from the REAL OpenBao,
#                                                 #   validate, print the destination; no sync.
#   scripts/deck-sync.sh --kong-addr http://kong:8001
#                                                 # render from OpenBao + `deck sync` to Kong.
#   scripts/deck-sync.sh --rotate add|remove      # the §P7 3-step JWT key rotation:
#                                                 #   add    → register the NEXT IAM key
#                                                 #            alongside the current one;
#                                                 #   remove → drop the OLD key once all
#                                                 #            tokens signed with it expired.
#
# Self-contained: pure bash + the pinned `deck` (mise) + the `bao` CLI (or curl fallback).
# NO real cert/key material is committed; this script wires the RETRIEVAL path only.
set -euo pipefail

# ── resolve repo root (cwd-independent; agent worktrees reset cwd between calls) ──────
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
KONG_SRC="$REPO_ROOT/infra/kong/kong.yml"

# ── entity ids the placeholders carry in the committed kong.yml (stable, asserted) ───
EDGE_CERT_ID="7e9b6f1a-2c4d-4e8a-9b1f-0a2c4d6e8f10"   # shared orchestrator/engine client cert
MCP_CERT_ID="a1b2c3d4-e5f6-7890-abcd-ef1234567890"    # mcp-server-scoped client cert
CA_BUNDLE_ID="f0e1d2c3-b4a5-6789-cdef-012345678901"   # internal CA bundle (upstream verify)
IAM_CONSUMER="iam-issuer"                              # the JWT consumer carrying rsa_public_key

# ── OpenBao paths (overridable) ──────────────────────────────────────────────────────
BAO_KONG_EDGE_CERT_PATH="${BAO_KONG_EDGE_CERT_PATH:-secret/data/babelstone/edge/kong-client-cert}"
BAO_KONG_MCP_CERT_PATH="${BAO_KONG_MCP_CERT_PATH:-secret/data/babelstone/edge/kong-mcp-client-cert}"
BAO_INTERNAL_CA_PATH="${BAO_INTERNAL_CA_PATH:-secret/data/babelstone/edge/internal-ca-bundle}"
BAO_IAM_JWT_PUBKEY_PATH="${BAO_IAM_JWT_PUBKEY_PATH:-secret/data/babelstone/edge/iam-jwt-public-key}"

# ── IAM issuer repoint (ADR-IC-021 rollout step 3 / bd babelstone-zla1.10.3) ──────────
# The committed kong.yml carries the POC issuer `https://iam.babelstone.example/` as the
# `iam-issuer` consumer's JWT `iss` key — fine for the dev/CI stack + the offline contract
# harness, which mint tokens under that iss. At staging deploy the IAM is **Logto**
# (ADR-IC-021), whose `iss` is its OIDC base URL. Rewriting the committed placeholder to the
# real Logto issuer here is the SAME deploy-time-placeholder-swap as the §P7 signing key — so
# the staging gateway validates Logto-issued tokens with NO edge-contract change. Overridable
# so a different environment can repoint without editing this script.
IAM_ISSUER_POC="https://iam.babelstone.example/"          # the committed placeholder iss
BABELSTONE_IAM_ISSUER="${BABELSTONE_IAM_ISSUER:-https://auth.babelstone.dev/oidc}"  # Logto staging iss

# ── arg parse ────────────────────────────────────────────────────────────────────────
MODE="sync"          # sync | render-only | rotate | dry-run
ROTATE_OP=""         # add | remove
DRY_RUN=0            # 1 → synthesise throwaway PEM in-process, never touch OpenBao
KONG_ADMIN_ADDR="${KONG_ADMIN_ADDR:-}"
while [ $# -gt 0 ]; do
  case "$1" in
    --dry-run)     MODE="dry-run"; DRY_RUN=1; shift ;;
    --render-only) MODE="render-only"; shift ;;
    --kong-addr)   KONG_ADMIN_ADDR="$2"; shift 2 ;;
    --rotate)      MODE="rotate"; ROTATE_OP="${2:-}"; shift 2 ;;
    -h|--help)     sed -n '2,72p' "$0"; exit 0 ;;
    *) echo "deck-sync: unknown argument '$1'" >&2; exit 2 ;;
  esac
done

info() { printf '\033[36m• %s\033[0m\n' "$*" >&2; }
ok()   { printf '\033[32m✓ %s\033[0m\n' "$*" >&2; }
warn() { printf '\033[33m! %s\033[0m\n' "$*" >&2; }
die()  { printf '\033[31m✗ %s\033[0m\n' "$*" >&2; exit 1; }

DECK() { mise exec -- deck "$@"; }

# ── render destination: a private tmpfile, shredded on exit (secrets never persist) ──
# DECK_SYNC_DEBUG_RENDER (a path) copies the render there before shredding — a debugging
# aid ONLY for the --dry-run path (throwaway PEM); NEVER point it at a real-secret render.
# mktemp portability: BSD mktemp (macOS) requires the X's at the END of the template
# (a trailing `.yml` suffix makes it fail), so mint the temp then rename to `.yml`.
RENDER="$(mktemp "${TMPDIR:-/tmp}/kong-rendered.XXXXXX")" || die "could not create the render tmpfile"
mv -f "$RENDER" "$RENDER.yml"
RENDER="$RENDER.yml"
cleanup() {
  if [ -n "${DECK_SYNC_DEBUG_RENDER:-}" ] && [ "$DRY_RUN" -eq 1 ]; then
    cp -f "$RENDER" "$DECK_SYNC_DEBUG_RENDER" 2>/dev/null || true
  fi
  rm -f "$RENDER"
}
trap cleanup EXIT
chmod 600 "$RENDER"

# ── dry-run material: throwaway, freshly-minted PEM, never the committed POC bytes ───
# Generated in-process with openssl so the render path can be exercised end-to-end in CI
# with NO live OpenBao and NO committed secret. The CN is deliberately NOT the POC CN, so
# the placeholder-gone guard (which fails on babelstone-*-poc) proves the swap really
# happened. This material is throwaway and lives only in the tmpfs render — never written
# back, never committed.
DRY_KEY_PEM=""; DRY_CERT_PEM=""; DRY_PUB_PEM=""
dry_mint() {
  [ -n "$DRY_CERT_PEM" ] && return 0
  command -v openssl >/dev/null 2>&1 || die "--dry-run needs openssl to mint throwaway PEM"
  local d; d="$(mktemp -d "${TMPDIR:-/tmp}/deck-dry.XXXXXX")"
  openssl req -x509 -newkey rsa:2048 -nodes -days 30 \
    -subj "/CN=babelstone-edge-dryrun" \
    -keyout "$d/k.pem" -out "$d/c.pem" >/dev/null 2>&1 \
    || die "--dry-run: openssl could not mint a throwaway keypair"
  openssl rsa -in "$d/k.pem" -pubout -out "$d/pub.pem" >/dev/null 2>&1 \
    || die "--dry-run: openssl could not derive the public key"
  DRY_KEY_PEM="$(cat "$d/k.pem")"; DRY_CERT_PEM="$(cat "$d/c.pem")"; DRY_PUB_PEM="$(cat "$d/pub.pem")"
  rm -rf "$d"
}

# ── OpenBao field read: prefer the bao CLI, fall back to the HTTP API via curl ───────
# Returns the raw value of one field at a KV-v2 data path. Fail-closed: an empty/missing
# field is a hard error (we must NEVER deploy with a placeholder silently left in).
bao_field() { # data_path field
  local path="$1" field="$2" val=""
  # Dry-run short-circuit: synthesise throwaway PEM, never touch OpenBao.
  if [ "$DRY_RUN" -eq 1 ]; then
    dry_mint
    case "$field" in
      cert)            printf '%s' "$DRY_CERT_PEM"; return 0 ;;
      key)             printf '%s' "$DRY_KEY_PEM";  return 0 ;;
      rsa_public_key|rsa_public_key_next) printf '%s' "$DRY_PUB_PEM"; return 0 ;;
    esac
  fi
  if command -v bao >/dev/null 2>&1; then
    # KV-v2 read; bao field paths drop the `data/` segment for `bao kv get`, so use the
    # raw `bao read` against the data path which mirrors the HTTP shape 1:1.
    val="$(bao read -field="$field" "$path" 2>/dev/null || true)"
  fi
  if [ -z "$val" ]; then
    : "${BAO_ADDR:?deck-sync: BAO_ADDR must be set to read OpenBao material}"
    : "${BAO_TOKEN:?deck-sync: BAO_TOKEN must be set to read OpenBao material}"
    val="$(curl -fsS -H "X-Vault-Token: $BAO_TOKEN" "$BAO_ADDR/v1/$path" \
            | mise exec -- python3 -c 'import sys,json;print(json.load(sys.stdin)["data"]["data"].get(sys.argv[1],""))' "$field" \
            2>/dev/null || true)"
  fi
  [ -n "$val" ] || die "OpenBao field '$field' at '$path' is empty/unreadable (M.2 boundary not provisioned, or token lacks read)"
  printf '%s' "$val"
}

# ── splice a PEM block into kong.yml under a yaml key, preserving 6-space indentation ─
# kong.yml carries PEM under block scalars indented 6 spaces under `cert:`/`key:`/
# `rsa_public_key:`. We replace the block belonging to a specific entity id by id-anchored
# range edit using python (yaml-structure-aware, never a blind global sed that could hit
# the wrong entity). The render is validated by `deck file validate` afterwards, so a
# botched splice fails the gate rather than reaching Kong.
splice_pem() { # entity_kind entity_id yaml_key pem_value
  local kind="$1" id="$2" key="$3" pem="$4"
  PEM="$pem" mise exec -- python3 - "$RENDER" "$kind" "$id" "$key" <<'PY'
import os, re, sys
render, kind, ident, key = sys.argv[1], sys.argv[2], sys.argv[3], sys.argv[4]
pem = os.environ["PEM"].rstrip("\n")
with open(render) as f:
    lines = f.readlines()

# Locate the entity block: a list item bearing `id: <ident>` (cert/ca entities) or
# `username: <ident>` (the jwt consumer). We scan within the matching top-level section.
def find_entity(lines, ident):
    for i, ln in enumerate(lines):
        if re.match(rf'^\s*-?\s*(id|username):\s*{re.escape(ident)}\s*$', ln):
            return i
    return -1

start = find_entity(lines, ident)
if start < 0:
    sys.exit(f"splice: entity '{ident}' not found in render")

# Within that entity, find `<key>: |` and replace the indented block scalar beneath it.
# The entity ends at the next TOP-LEVEL entity boundary — a top-level list item
# (`  - id:` / `  - username:`) or a new column-0 section key (`certificates:`,
# `consumers:`, `services:` …). Nested list items inside the entity (e.g. the
# consumer's `jwt_secrets:` items) must NOT end the scan, so the stop is anchored to
# the entity's own indent, not "any `- `".
entity_indent = len(re.match(r'^(\s*)', lines[start]).group(1))
ki = -1
for i in range(start + 1, len(lines)):
    ln = lines[i]
    # new top-level section key at column 0 → past this entity
    if re.match(r'^[A-Za-z_]', ln):
        break
    # next sibling entity (a list item at or below the entity's own indent) → past it
    m = re.match(r'^(\s*)-\s', ln)
    if m and len(m.group(1)) <= entity_indent:
        break
    if re.match(rf'^(\s+){re.escape(key)}:\s*\|\s*$', ln):
        ki = i
        break
if ki < 0:
    sys.exit(f"splice: key '{key}: |' not found under entity '{ident}'")

indent = re.match(r'^(\s+)', lines[ki]).group(1)
# the block scalar body is more-indented than the key line; collect + drop it
body_indent = indent + "  "
j = ki + 1
while j < len(lines) and (lines[j].strip() == "" or lines[j].startswith(body_indent)):
    j += 1
new_body = [body_indent + l + "\n" for l in pem.splitlines()]
out = lines[:ki+1] + new_body + lines[j:]
with open(render, "w") as f:
    f.writelines(out)
PY
}

# ── repoint the iam-issuer `iss` at Logto (ADR-IC-021 step 3, bd babelstone-zla1.10.3) ─
# Rewrite the `iam-issuer` consumer's JWT `iss` key from the committed POC placeholder to the
# real Logto staging issuer. The committed file carries exactly one `- key: <POC iss>` line on
# that consumer; we replace its VALUE only (id/value-anchored, never a blind global edit). The
# render is `deck file validate`d afterwards, so a botched rewrite fails the gate, not Kong.
rewrite_iam_issuer() {
  POC="$IAM_ISSUER_POC" NEW="$BABELSTONE_IAM_ISSUER" mise exec -- python3 - "$RENDER" <<'PY'
import os, re, sys
render = sys.argv[1]
poc, new = os.environ["POC"], os.environ["NEW"]
with open(render) as f:
    lines = f.readlines()
# Replace the iam-issuer consumer's `- key: <poc>` value (the JWT iss). Match a list item whose
# value is exactly the POC issuer so a comment that merely names the iss is never rewritten.
pat = re.compile(r'^(\s*-\s*key:\s*)' + re.escape(poc) + r'\s*$')
hits = 0
for i, ln in enumerate(lines):
    m = pat.match(ln)
    if m:
        lines[i] = f"{m.group(1)}{new}\n"
        hits += 1
if hits != 1:
    sys.exit(f"iam-issuer iss rewrite: expected exactly 1 `- key: {poc}` line, found {hits}")
with open(render, "w") as f:
    f.writelines(lines)
PY
}

# ── flip `tls_verify: false` → true on the orchestrator/engine upstreams (§P5) ───────
# The MCP service already runs tls_verify: true; the orchestrator-edge + engine upstreams
# are POC `false` until the CA bundle is mounted. Now that the real internal CA bundle is
# injected (entity f0e1d2c3-…), flip them so Kong VERIFIES the upstream server cert.
flip_tls_verify() {
  mise exec -- python3 - "$RENDER" <<'PY'
import re, sys
render = sys.argv[1]
with open(render) as f:
    src = f.read()
# Only the POC-false upstreams carry `    tls_verify: false`; the MCP service is already
# true. A straight literal swap is safe (the MCP service has no `false` line to hit).
new = src.replace("    tls_verify: false", "    tls_verify: true")
with open(render, "w") as f:
    f.write(new)
PY
}

render_from_openbao() {
  info "rendering kong.yml from OpenBao material → $RENDER"
  cp -f "$KONG_SRC" "$RENDER"

  # 1) edge client cert + key (shared orchestrator/engine upstream mTLS, §P5)
  splice_pem certificate "$EDGE_CERT_ID" cert "$(bao_field "$BAO_KONG_EDGE_CERT_PATH" cert)"
  splice_pem certificate "$EDGE_CERT_ID" key  "$(bao_field "$BAO_KONG_EDGE_CERT_PATH" key)"

  # 2) mcp-server-scoped client cert + key (§P5, ADR-IC-010 §P5)
  splice_pem certificate "$MCP_CERT_ID" cert "$(bao_field "$BAO_KONG_MCP_CERT_PATH" cert)"
  splice_pem certificate "$MCP_CERT_ID" key  "$(bao_field "$BAO_KONG_MCP_CERT_PATH" key)"

  # 3) internal CA bundle (upstream-server-cert verification, §P5)
  splice_pem ca_certificate "$CA_BUNDLE_ID" cert "$(bao_field "$BAO_INTERNAL_CA_PATH" cert)"

  # 4) IAM RS256 signing public key (JWT validation, §P7) — Logto's JWKS signing key (ADR-IC-021).
  splice_pem consumer "$IAM_CONSUMER" rsa_public_key "$(bao_field "$BAO_IAM_JWT_PUBKEY_PATH" rsa_public_key)"

  # 4b) repoint the iam-issuer `iss` at Logto (ADR-IC-021 step 3): the committed POC issuer →
  #     the real Logto staging issuer, so the gateway trusts Logto-minted tokens.
  rewrite_iam_issuer

  # 5) now the CA bundle is real, flip upstream verify on (the reverse half of §P5 mTLS)
  flip_tls_verify

  # Guard (fail-closed — NEVER deploy placeholders): the committed POC PEM *bodies* must
  # be GONE from the render. We match the distinctive base64 sentinel of each committed
  # POC artifact (its first body line), not the CN comments — the comments legitimately
  # remain (they describe the entity + the POC→prod swap intent); only the secret MATERIAL
  # must have been replaced by the OpenBao-sourced bytes.
  local poc_sentinels=(
    "MIIDHTCCAgWgAwIBAgIUfaHM5ukIJp1MpqoI0c15PdeDE8I"   # edge POC cert  (entity 7e9b6f1a)
    "MIIDRDCCAiygAwIBAgIUTgyYJEkrDSCIaoGXYaVnIBrFvIs"   # mcp  POC cert  (entity a1b2c3d4)
    "MIIDEDCCAfigAwIBAgIUfYZUFJ8QHSp6YHfjTLOVypx+st0"   # CA bundle POC  (entity f0e1d2c3)
    "MIIBIjANBgkqhkiG9w0BAQEFAAOCAQ8AMIIBCgKCAQEAm28DnOOCLqSixANucnRv"  # IAM POC pubkey
  )
  local s
  for s in "${poc_sentinels[@]}"; do
    if grep -qF "$s" "$RENDER"; then
      die "render still contains a committed POC PEM body — OpenBao injection did not replace it"
    fi
  done
  if grep -q "tls_verify: false" "$RENDER"; then
    die "render still carries tls_verify: false — upstream mTLS not fully locked (§P5 reverse half)"
  fi
  ok "rendered + POC material replaced; upstream tls_verify flipped on (§P5)"
}

# ── the §P7 3-step JWT key rotation (add new → retain old → remove old) ──────────────
rotate_jwt_key() {
  case "$ROTATE_OP" in
    add)
      info "§P7 rotation step 1 — registering the NEXT IAM signing key alongside the current"
      render_from_openbao
      # add a SECOND jwt_secrets entry for the iam-issuer consumer carrying the next key,
      # keyed by a rotation-suffixed iss so both validate during the overlap window.
      local next; next="$(bao_field "$BAO_IAM_JWT_PUBKEY_PATH" rsa_public_key_next)"
      KEY_NEXT="$next" ISS_NEXT="${BABELSTONE_IAM_ISSUER}/next" mise exec -- python3 - "$RENDER" <<'PY'
import os, re, sys
render = sys.argv[1]
nxt = os.environ["KEY_NEXT"].rstrip("\n")
iss_next = os.environ["ISS_NEXT"]
with open(render) as f: lines = f.readlines()
# Append a second jwt_secrets entry after the existing one on the iam-issuer consumer.
# Find the existing `rsa_public_key: |` block end, then insert a sibling list item.
out, inserted = [], False
i = 0
while i < len(lines):
    out.append(lines[i])
    if not inserted and re.match(r'^\s+rsa_public_key:\s*\|\s*$', lines[i]):
        indent = re.match(r'^(\s+)', lines[i]).group(1)
        body = indent + "  "
        j = i + 1
        while j < len(lines) and (lines[j].strip()=="" or lines[j].startswith(body)):
            out.append(lines[j]); j += 1
        item_indent = indent[:-2] if len(indent) >= 2 else indent
        out.append(f"{item_indent}- key: {iss_next}\n")
        out.append(f"{item_indent}  algorithm: RS256\n")
        out.append(f"{item_indent}  secret: unused-for-rs256\n")
        out.append(f"{item_indent}  rsa_public_key: |\n")
        for l in nxt.splitlines():
            out.append(body + l + "\n")
        inserted = True
        i = j
        continue
    i += 1
if not inserted: sys.exit("rotate add: existing rsa_public_key block not found")
with open(render, "w") as f: f.writelines(out)
PY
      ok "next key registered (both keys valid during the overlap window)"
      ;;
    remove)
      info "§P7 rotation step 3 — removing the OLD key (run only AFTER all old tokens expired)"
      # render fresh: the current OpenBao key is now the (rotated-in) key; the old one is
      # simply absent from the source, so a clean render carries only the live key.
      render_from_openbao
      ok "old key removed; only the live IAM key remains registered"
      ;;
    *) die "deck-sync --rotate takes 'add' or 'remove' (the §P7 3-step rotation)";;
  esac
}

validate_render() {
  info "deck file validate on the rendered config (ADR-IC-006 §P1 structural gate)"
  DECK file validate "$RENDER" >/dev/null || die "rendered kong.yml failed deck file validate"
  ok "rendered config is structurally valid (deck file validate passed)"
}

do_sync() {
  [ -n "$KONG_ADMIN_ADDR" ] || die "deck sync needs --kong-addr (the Kong Admin API, e.g. http://kong:8001)"
  info "deck sync → $KONG_ADMIN_ADDR (declarative, DB-less; ADR-IC-006 §P1)"
  # deck diff first for an auditable preview, then sync (idempotent — converges Kong to
  # the rendered desired state; a no-op if already in sync).
  DECK gateway diff   "$RENDER" --kong-addr "$KONG_ADMIN_ADDR" || true
  DECK gateway sync   "$RENDER" --kong-addr "$KONG_ADMIN_ADDR" \
    || die "deck sync failed — Kong NOT converged to the rendered desired state"
  ok "Kong converged to the OpenBao-backed declarative config (real mTLS + IAM key live)"
}

# ── main ─────────────────────────────────────────────────────────────────────────────
case "$MODE" in
  dry-run)
    render_from_openbao
    validate_render
    ok "dry-run complete — render PATH proven with throwaway PEM (no OpenBao, no secrets, no sync)"
    ;;
  render-only)
    render_from_openbao
    validate_render
    ok "render-only complete — rendered config at $RENDER (NOT synced; secrets shredded on exit)"
    ;;
  rotate)
    rotate_jwt_key
    validate_render
    do_sync
    ;;
  sync)
    render_from_openbao
    validate_render
    do_sync
    ;;
esac
