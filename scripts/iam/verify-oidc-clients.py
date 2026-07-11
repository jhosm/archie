#!/usr/bin/env python3
"""Fail-loud verify that the hand-registered Boundary-6 OIDC login surfaces (Grafana, Backstage)
exist in Logto with the deployed client_id AND the right redirect URI (bd babelstone-zla1.10.19).

Plain English: after a staging re-onboard Logto mints fresh OAuth app IDs, and each surface's
committed client_id has to be re-pinned to match. Mission Control already has a CD safety net —
register-ops-console.py + OPS_EXPECT_CLIENT_ID — that fails the promote if its client_id is not a
registered Logto app. Grafana and Backstage did NOT: they are hand-registered, so a re-onboard could
leave them dialing an App ID Logto no longer knows, and nothing caught it until a human hit
`oidc.invalid_client` at the login screen (the 2026-07-11 Grafana outage). This script is that
missing net — for each surface it GETs /api/applications/{client_id} and fails loud if the app is
missing (404) or its redirect URI is wrong.

This VERIFIES; it does NOT register. Auto-creating these apps would contradict the ADR-IC-021 §C6
curated hand-registration posture (DCR deferred) and risk duplicate apps — Mission Control's
auto-create (register-ops-console.py) is the deliberate first-party exception. On a fresh re-onboard
the remediation is the runbook hand-registration, then re-pin the committed client_id; this script
tells the operator exactly which surface still needs it.

Idempotent + read-only: it performs only GETs, never a create/update, so it is safe to run on every
promote and standalone (apply=false) to re-check a hand-re-onboarded Logto.

Config via env (defaults are the staging values):
    LOGTO_BASE_URL             default https://auth.babelstone.dev
    LOGTO_MGMT_TOKEN           Management-API bearer (required; minted by the caller, never a literal)
    GRAFANA_EXPECT_CLIENT_ID   deployed Grafana client_id; when set → verified, when unset → skipped
    GRAFANA_BASE               default https://grafana.babelstone.dev
    BACKSTAGE_EXPECT_CLIENT_ID deployed Backstage client_id; when set → verified, when unset → skipped
    BACKSTAGE_BASE             default https://backstage.babelstone.dev

A surface whose *_EXPECT_CLIENT_ID is unset is skipped (matching register-ops-console's bare-hand-run
behaviour); the CD configure-logto job always sets both from the RENDERED overlay. Every configured
surface is checked before exiting, so BOTH problems surface in one run rather than dying on the first.
"""
import json
import os
import sys
import urllib.error
import urllib.request

BASE = os.environ.get("LOGTO_BASE_URL", "https://auth.babelstone.dev").rstrip("/")
TOKEN = os.environ.get("LOGTO_MGMT_TOKEN", "").strip()

GRAFANA_BASE = os.environ.get("GRAFANA_BASE", "https://grafana.babelstone.dev").rstrip("/")
BACKSTAGE_BASE = os.environ.get("BACKSTAGE_BASE", "https://backstage.babelstone.dev").rstrip("/")

# Each surface: (label, deployed client_id from env, the redirect URI Logto MUST have registered).
# The redirect URIs are deterministic from committed config:
#   Grafana   → {root_url}/login/generic_oauth   (infra/grafana/rbac/grafana.ini [server] root_url)
#   Backstage → {BACKSTAGE_BASE_URL}/api/auth/oidc/handler/frame  (infra/runbooks/backstage-oidc-registration.md §2)
SURFACES = [
    (
        "Grafana",
        os.environ.get("GRAFANA_EXPECT_CLIENT_ID", "").strip(),
        f"{GRAFANA_BASE}/login/generic_oauth",
        "infra/grafana/rbac/grafana.ini ([auth.generic_oauth] client_id)",
    ),
    (
        "Backstage",
        os.environ.get("BACKSTAGE_EXPECT_CLIENT_ID", "").strip(),
        f"{BACKSTAGE_BASE}/api/auth/oidc/handler/frame",
        "infra/k8s/overlays/staging/backstage-oidc.patch.yaml (BACKSTAGE_OIDC_CLIENT_ID)",
    ),
]


def api(method, path):
    req = urllib.request.Request(
        BASE + path,
        method=method,
        headers={
            "Authorization": f"Bearer {TOKEN}",
            "Accept": "application/json",
            "User-Agent": "babelstone-iam-verify-oidc/1.0",  # non-default UA (Cloudflare 1010 guard)
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


def verify(label, client_id, expect_redirect, pin_location):
    """Check one surface. Returns None on pass, or a human-readable problem string on failure."""
    status, app = api("GET", f"/api/applications/{client_id}")
    if status == 404:
        return (
            f"{label}: deployed client_id {client_id!r} is NOT a registered Logto application "
            f"(GET /api/applications/{client_id} → 404). Login is broken (oidc.invalid_client). "
            f"Fix: hand-register the {label} app in the Logto console, then re-pin its App ID into "
            f"{pin_location} and redeploy — or, if the app already exists under a different id, "
            f"re-pin to that id. The registered App ID and the deployed client_id MUST be equal."
        )
    if status != 200:
        return f"{label}: GET /api/applications/{client_id} returned {status} {app} (expected 200 or 404)."
    meta = app.get("oidcClientMetadata") or {}
    redirects = meta.get("redirectUris") or []
    if expect_redirect not in redirects:
        return (
            f"{label}: app {client_id!r} exists but its redirectUris {redirects} do not include the "
            f"required {expect_redirect!r}. Add that redirect URI on the {label} application in the "
            f"Logto console (Logto matches redirect_uri exactly), or the login round-trip fails."
        )
    print(f"OK — {label}: client_id {client_id} is registered with redirect {expect_redirect}.")
    return None


def main():
    if not TOKEN:
        sys.exit("LOGTO_MGMT_TOKEN is unset — export a Management-API token (see the module docstring).")

    problems = []
    checked = 0
    for label, client_id, expect_redirect, pin_location in SURFACES:
        if not client_id:
            print(f"skip — {label}: *_EXPECT_CLIENT_ID unset (nothing to verify).")
            continue
        checked += 1
        problem = verify(label, client_id, expect_redirect, pin_location)
        if problem:
            problems.append(problem)

    print()
    if problems:
        print(f"❌ {len(problems)} of {checked} verified OIDC login surface(s) FAILED:")
        for p in problems:
            print(f"  - {p}")
        print(
            "\nThese are hand-registered surfaces (ADR-IC-021 §C6 curated registration) — this gate "
            "verifies, it does not auto-register. Close each gap above, then re-run the configure-logto "
            "job (apply=false) to re-check without re-promoting."
        )
        sys.exit(1)

    if checked == 0:
        print("No OIDC login surfaces to verify (both *_EXPECT_CLIENT_ID unset).")
    else:
        print(f"Done — all {checked} verified OIDC login surface(s) are registered and correctly wired "
              "(closes the Grafana/Backstage enforcement gap bd babelstone-zla1.10.19).")


if __name__ == "__main__":
    main()
