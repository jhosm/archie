#!/usr/bin/env python3
"""Register the product API as a Logto API resource + its scopes (bd babelstone-zla1.10.9.1).

Plain English: today the Mission Control demo UI logs an operator in but never asks Logto for a
token the *product API* would accept — it forwards a made-up caller id straight to the engine,
bypassing the Kong edge. bd babelstone-zla1.10.9 fixes that by routing Mission Control's
`/v1/*` + `/api/v1/*` through Kong with a real access token. For Logto to MINT such a token, the
product API must first exist as a Logto API resource (an RFC 8707 audience) with named scopes. This
script is that (re)create step — the automated, idempotent AS-side substrate the rest of the epic
builds on. It touches NO demo traffic: it only makes the resource + scopes exist in Logto.

ONE shared product-API resource (bd babelstone-zla1.10.9 decision): a single resource indicator
covers BOTH the engine `/v1/*` and the orchestrator `/api/v1/*` surfaces fronted by Kong at
`api.babelstone.dev`. It is DISTINCT from the MCP-server resource (`.../mcp`, register-mcp-resource.py)
— an agent token and an operator token are different audiences and must not be replayable across.

Why a script (not a manifest): Logto's application config (API resources, scopes, roles, clients) is
Management-API state, NOT captured in the Kubernetes manifests or the `logto db seed` Job — so a Logto
re-onboard wipes it. This script makes the product-API-resource substrate reproducible: run it after
Logto is onboarded to (re)create the resource + scopes idempotently. It mirrors
`scripts/iam/register-mcp-resource.py` line-for-line in shape; the CD `configure-logto` job invokes it
on every staging promote (and standalone with apply=false to re-heal a hand-re-onboarded Logto).

Granting the scopes is the curated hand-step, NOT done here. Operator access is role-scoped
(ADR-IC-021 C7): Mission Control operators are Logto *users*, so their `authorization_code` access
tokens carry a resource's scopes via their **role** (RBAC), not via an app grant. The maintainer step
is to bundle these scopes into an operator role and assign it to the operator user(s) — see
`infra/runbooks/mission-control-oidc-registration.md` §2a. Grant by hand rather than auto-granting —
the same curated, no-DCR discipline ADR-IC-021 §C6 applies to agent clients. serve.py sends `resource=` + these scopes at
login in bd babelstone-zla1.10.9.3; Kong validates the token at the edge in bd babelstone-zla1.10.9.2.

Secrets discipline: this script writes NOTHING secret. It reads a Management-API token from the
environment (never a literal). Obtain that token from the `babelstone-mgmt` M2M app
(client_credentials, resource=https://default.logto.app/api) and export it:

    export LOGTO_MGMT_TOKEN=$(curl -s -A babelstone-iam/1.0 -u "$APP_ID:$APP_SECRET" \
      -d grant_type=client_credentials \
      --data-urlencode resource=https://default.logto.app/api \
      -d scope=all https://auth.babelstone.dev/oidc/token | jq -r .access_token)
    python3 scripts/iam/register-product-api-resource.py

Idempotent: existing resource/scopes are detected and left untouched. Prints the resource id + scope
ids; exits non-zero on any create failure.

Config via env (defaults are the staging values):
    LOGTO_BASE_URL           default https://auth.babelstone.dev   (the default-tenant issuer host)
    PRODUCT_API_RESOURCE_URI default https://api.babelstone.dev/   (the ONE shared product-API resource
                             indicator; MUST equal byte-for-byte the `resource=` serve.py sends
                             (bd zla1.10.9.3) and any `aud` Kong enforces (bd zla1.10.9.2) — indicator
                             slash-sensitivity is infra/runbooks/iam-mcp-resource-registration.md
                             §1 step 2 + §4; on the interactive auth-code flow serve.py uses, a
                             mismatched/unsent resource fails SILENTLY to a default resource, not the
                             M2M flow's fail-closed invalid_target)
"""
import json
import os
import sys
import urllib.error
import urllib.request

BASE = os.environ.get("LOGTO_BASE_URL", "https://auth.babelstone.dev").rstrip("/")
# NB: no .rstrip("/") — the trailing slash is PART of the indicator and must be preserved byte-for-byte.
PRODUCT_API_URI = os.environ.get("PRODUCT_API_RESOURCE_URI", "https://api.babelstone.dev/")
TOKEN = os.environ.get("LOGTO_MGMT_TOKEN", "").strip()

# The product-API scopes. This is the initial scope vocabulary the epic's later slices align to
# (bd zla1.10.9.2/.9.3). NOTE: the human money-mover path gates on step-up SCA (acr/auth_time), NOT on
# a product scope, so these declare the operator's *entitlement surface* rather than the money-mover
# gate; the final gated set is confirmed with the Kong pre-function (bd zla1.10.9.2). No god-scope.
SCOPES = {
    "deposits:read": "read a deposit / poll saga status (GET /v1/deposits/...)",
    "deposits:write": "constitute (via the saga edge) / mature / pay-interest / terminate a deposit",
    "loans:write": "collect an installment / early-repay a loan",
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
            "User-Agent": "babelstone-iam-register-product-api/1.0",
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
    existing = next((r for r in resources if r.get("indicator") == PRODUCT_API_URI), None)
    if existing:
        rid = existing["id"]
        print(f"resource exists: id={rid} indicator={PRODUCT_API_URI}")
    else:
        status, res = api("POST", "/api/resources", {"name": "babelstone-product-api", "indicator": PRODUCT_API_URI})
        if status not in (200, 201):
            sys.exit(f"create resource failed: {status} {res}")
        rid = res["id"]
        print(f"resource created: id={rid} indicator={PRODUCT_API_URI}")

    # 2. the product-API scopes
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
    print(f"\nOK — product-API resource {PRODUCT_API_URI} has scopes: {sorted(final)}")
    print("Next (curated hand-step, ADR-IC-021 §C6): bundle these scopes into an operator role and "
          "assign it to the operator user(s) so their access token carries them — see "
          "infra/runbooks/mission-control-oidc-registration.md §2a. serve.py requests them via "
          "resource= at login (bd babelstone-zla1.10.9.3); Kong validates at the edge (bd babelstone-zla1.10.9.2).")


if __name__ == "__main__":
    main()
