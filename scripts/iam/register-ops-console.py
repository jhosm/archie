#!/usr/bin/env python3
"""Register the Mission Control ops console as a Logto Traditional Web app (ADR-IC-021 C7).

Plain English: Mission Control (app.babelstone.dev) is the operator console that reaches saga state —
the surface ADR-IC-021 §C7 says must sit behind a Logto login with step-up MFA. Its Deployment already
runs `MC_AUTH_MODE=oidc` pointing at a Logto client id `babelstone-mission-control`, but that client is
NOT present on the live Logto (a re-onboard this session wiped it — the same way it wiped the MCP
resource), so the login is currently broken. This is the automated, idempotent form of the manual
`infra/runbooks/mission-control-oidc-registration.md` §1–§2. (Mission Control is a FIRST-PARTY owned
Boundary-1/6 client, so hand-registering it is the normal path — distinct from the ADR-IC-021 §C6
agent-DCR gap, which is about arbitrary third-party *agents* self-onboarding.) It creates the
confidential Traditional-Web app with the exact redirect + sign-out URIs serve.py derives, so operator
login (and C7's ops-console subject) works again.

Secrets discipline: this script NEVER prints the client secret (it is popped from the API response
before anything is displayed). Seeding that secret + the session-signing key into the OpenBao-backed
`babelstone-dev-secrets` Secret is the deliberate manual step in
`infra/runbooks/mission-control-oidc-registration.md` §3 (secrets off the bus). Read the module env
token, never a literal:

    export LOGTO_MGMT_TOKEN=$(curl -s -A babelstone-iam/1.0 -u "$APP_ID:$APP_SECRET" \
      -d grant_type=client_credentials \
      --data-urlencode resource=https://default.logto.app/api \
      -d scope=all https://auth.babelstone.dev/oidc/token | jq -r .access_token)
    python3 scripts/iam/register-ops-console.py

Idempotent: keyed on the app NAME. If the app exists it is left untouched and its id is reported.

IMPORTANT — the App ID: Logto's Management API assigns a random App ID (e.g. `drvvp3sfzk4ssckg5d5si`);
it does NOT let you pin it to `babelstone-mission-control`. So after first creation you MUST set
`OIDC_CLIENT_ID` in `infra/k8s/overlays/staging/mission-control.yaml` to the id printed here and
redeploy — exactly the hedge `mission-control-oidc-registration.md` §1.3 anticipates. This script
reports the mismatch loudly so it is not missed (that manifest edit + redeploy is a maintainer step).

Config via env (defaults are the staging values):
    LOGTO_BASE_URL   default https://auth.babelstone.dev
    MC_PUBLIC_BASE   default https://app.babelstone.dev   (must equal MC_PUBLIC_BASE_URL in the manifest)
    OPS_APP_NAME     default babelstone-mission-control
"""
import json
import os
import sys
import urllib.error
import urllib.request

BASE = os.environ.get("LOGTO_BASE_URL", "https://auth.babelstone.dev").rstrip("/")
MC_BASE = os.environ.get("MC_PUBLIC_BASE", "https://app.babelstone.dev").rstrip("/")
APP_NAME = os.environ.get("OPS_APP_NAME", "babelstone-mission-control")
TOKEN = os.environ.get("LOGTO_MGMT_TOKEN", "").strip()

REDIRECT_URI = f"{MC_BASE}/callback"          # serve.py derives {MC_PUBLIC_BASE_URL}/callback
POST_LOGOUT_URI = f"{MC_BASE}/"               # where /logout bounces after Logto clears its session


def api(method, path, body=None):
    req = urllib.request.Request(
        BASE + path,
        data=json.dumps(body).encode() if body is not None else None,
        method=method,
        headers={
            "Authorization": f"Bearer {TOKEN}",
            "Content-Type": "application/json",
            "Accept": "application/json",
            "User-Agent": "babelstone-iam-register-ops/1.0",  # non-default UA (Cloudflare 1010 guard)
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


def _redact(app):
    """Never surface the confidential client secret (create-time only)."""
    if isinstance(app, dict):
        app.pop("secret", None)
        app.pop("secrets", None)
    return app


def main():
    if not TOKEN:
        sys.exit("LOGTO_MGMT_TOKEN is unset — export a Management-API token (see the module docstring).")

    status, apps = api("GET", "/applications")
    if status != 200:
        sys.exit(f"GET /applications failed: {status} {apps}")
    existing = next((a for a in apps if a.get("name") == APP_NAME), None)

    if existing:
        app_id = existing["id"]
        print(f"ops-console app exists: id={app_id} name={APP_NAME} (left untouched)")
        meta = existing.get("oidcClientMetadata") or {}
        print(f"  redirectUris={meta.get('redirectUris')} postLogoutRedirectUris={meta.get('postLogoutRedirectUris')}")
    else:
        body = {
            "name": APP_NAME,
            "type": "Traditional",  # confidential Traditional Web app (client_secret_post + PKCE S256)
            "description": "Mission Control ops console (Boundary 1 owned-channel login, ADR-IC-021 C7).",
            "oidcClientMetadata": {
                "redirectUris": [REDIRECT_URI],
                "postLogoutRedirectUris": [POST_LOGOUT_URI],
            },
        }
        status, app = api("POST", "/applications", body)
        if status not in (200, 201):
            sys.exit(f"create ops-console app failed: {status} {_redact(app)}")
        app_id = app["id"]
        print(f"ops-console app created: id={app_id} name={APP_NAME} (secret NOT printed — retrieve per runbook §3)")
        print(f"  redirectUris=[{REDIRECT_URI}] postLogoutRedirectUris=[{POST_LOGOUT_URI}]")

    print()
    if app_id != "babelstone-mission-control":
        print("⚠️  App ID is Logto-generated, NOT 'babelstone-mission-control'. Maintainer follow-up:")
        print(f"    1. Set OIDC_CLIENT_ID in infra/k8s/overlays/staging/mission-control.yaml to: {app_id}")
        print("    2. Seed LOGTO_MISSION_CONTROL_CLIENT_SECRET into babelstone-dev-secrets "
              "(infra/runbooks/mission-control-oidc-registration.md §3) — NEVER commit it.")
        print("    3. Redeploy Mission Control so app.babelstone.dev login works again.")
    print("\nDone. This closes the live 'ops-console client missing' gap C7 depends on "
          "(also advances bd babelstone-zla1.10.8).")


if __name__ == "__main__":
    main()
