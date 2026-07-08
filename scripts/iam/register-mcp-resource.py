#!/usr/bin/env python3
"""Register the MCP server as a Logto API resource + its three per-tool scopes (ADR-IC-021 C1/C5).

Plain English: this is the automated form of sections 1-2 of the
`infra/runbooks/iam-mcp-resource-registration.md` runbook. Logto's application config
(API resources, scopes, roles, clients) is Management-API state, NOT captured in the
Kubernetes manifests or the `logto db seed` job — so a Logto re-onboard wipes it. This
script makes the MCP-resource substrate reproducible: run it after Logto is onboarded to
(re)create the resource + scopes idempotently, so Boundary-9 aud-binding (C1) survives a
re-seed. Curated per-agent clients (runbook section 3) stay a deliberate hand step (C6).

Secrets discipline: this script writes NOTHING secret. It reads a Management-API token
from the environment (never a literal). Obtain that token from the `babelstone-mgmt`
M2M app (client_credentials, resource=https://default.logto.app/api) and export it:

    export LOGTO_MGMT_TOKEN=$(curl -s -u "$APP_ID:$APP_SECRET" \
      -d grant_type=client_credentials \
      --data-urlencode resource=https://default.logto.app/api \
      -d scope=all https://auth.babelstone.dev/oidc/token | jq -r .access_token)
    python3 scripts/iam/register-mcp-resource.py

Idempotent: existing resource/scopes are detected and left untouched. Prints the resource
id + scope ids; exits non-zero on any create failure.

Config via env (defaults are the staging values):
    LOGTO_BASE_URL   default https://auth.babelstone.dev   (the default-tenant issuer host)
    MCP_RESOURCE_URI default https://api.babelstone.dev/mcp (the Kong-fronted MCP URI;
                     MUST equal the mcp-server's BABELSTONE_MCP_SERVER_URI / what
                     /.well-known/oauth-protected-resource advertises)
"""
import json
import os
import sys
import urllib.error
import urllib.request

BASE = os.environ.get("LOGTO_BASE_URL", "https://auth.babelstone.dev").rstrip("/")
MCP_URI = os.environ.get("MCP_RESOURCE_URI", "https://api.babelstone.dev/mcp")
TOKEN = os.environ.get("LOGTO_MGMT_TOKEN", "").strip()

# The three narrow, per-tool scopes (ADR-IC-021 C5). Kept in lockstep with RESOURCE_SCOPES
# in mcp-server/src/babelstone_mcp/auth.py — no god-scope.
SCOPES = {
    "deposits:read": "read a deposit / poll saga status",
    "deposits:write": "constitute / mature / pay-interest (incl. the saga producer)",
    "transfers:write": "reserved for a future transfer tool (declared to keep the catalogue stable)",
}


def api(method, path, body=None):
    req = urllib.request.Request(
        BASE + path,
        data=json.dumps(body).encode() if body is not None else None,
        method=method,
        headers={
            "Authorization": f"Bearer {TOKEN}",
            "Content-Type": "application/json",
            "Accept": "application/json",
            # A descriptive UA: Cloudflare's managed bot rules 1010-ban the default
            # `Python-urllib/*` signature in front of auth.babelstone.dev.
            "User-Agent": "babelstone-iam-register/1.0",
        },
    )
    try:
        with urllib.request.urlopen(req, timeout=25) as r:
            return r.status, json.loads(r.read() or "null")
    except urllib.error.HTTPError as e:
        raw = e.read().decode(errors="replace")
        try:
            return e.code, json.loads(raw or "null")
        except json.JSONDecodeError:
            return e.code, {"_nonjson": raw[:300]}


def main():
    if not TOKEN:
        sys.exit("LOGTO_MGMT_TOKEN is unset — export a Management-API token (see the module docstring).")

    # 1. the API resource (RFC 8707 aud target)
    status, resources = api("GET", "/api/resources")
    if status != 200:
        sys.exit(f"GET /api/resources failed: {status} {resources}")
    existing = next((r for r in resources if r.get("indicator") == MCP_URI), None)
    if existing:
        rid = existing["id"]
        print(f"resource exists: id={rid} indicator={MCP_URI}")
    else:
        status, res = api("POST", "/api/resources", {"name": "babelstone-mcp-server", "indicator": MCP_URI})
        if status not in (200, 201):
            sys.exit(f"create resource failed: {status} {res}")
        rid = res["id"]
        print(f"resource created: id={rid} indicator={MCP_URI}")

    # 2. the three per-tool scopes (C5)
    have = {s["name"] for s in api("GET", f"/api/resources/{rid}/scopes")[1]}
    for name, desc in SCOPES.items():
        if name in have:
            print(f"scope exists: {name}")
            continue
        status, sc = api("POST", f"/api/resources/{rid}/scopes", {"name": name, "description": desc})
        if status not in (200, 201):
            sys.exit(f"create scope {name} failed: {status} {sc}")
        print(f"scope created: {name}")

    final = {s["name"]: s["id"] for s in api("GET", f"/api/resources/{rid}/scopes")[1]}
    print(f"\nOK — MCP resource {MCP_URI} has scopes: {sorted(final)}")
    print("Next (runbook section 3): hand-register each curated agent client and grant it the "
          "subset of these scopes it needs (least privilege).")


if __name__ == "__main__":
    main()
