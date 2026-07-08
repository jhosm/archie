#!/usr/bin/env python3
"""Offer WebAuthn + TOTP as MFA factors on the DEFAULT-tenant sign-in experience (ADR-IC-021 C4/C7).

Plain English: this turns on multi-factor sign-in (passkey / authenticator app) for the tenant
your operators, agents, and Grafana SSO authenticate through — the surface ADR-IC-021 §C7 ("the ops
console requires a step-up MFA session before reaching saga state") and §C4 (SCA strength) target.
It does NOT touch the Logto ADMIN console (that lives in the separate `admin` tenant and keeps its own
passkey). The policy is deliberately **UserControlled** ("offer, non-forcing", the maintainer's slice-2
decision): the factors become available and can be demanded on a step-up, but no existing login is
forced to enroll on its next sign-in.

Why this is the C4/C7 *prerequisite*, not the whole of it: the deployed Logto emits no native OIDC
`acr` (verified 2026-07-08; live discovery has `acr_values_supported` absent). Per ADR-IC-010 Amendment
2026-07-08 (§A16/§A17), step-up *freshness* rides Logto's native `auth_time` (already gated by Kong +
the engine `ScaPrecondition`), and step-up *strength* is a SYNTHESISED non-`acr` claim produced by a
`getCustomJwtClaims` script reading this sign-in's `verificationRecords` (Totp/WebAuthn). Enabling the
factors here is what lets a re-auth actually PERFORM the SCA whose result that script reads.

Logto config (sign-in-experience MFA) is live Management-API DB state, NOT captured in the k8s manifests
or `logto db seed` — a re-onboard wipes it. This script is the idempotent reproduce path (mirrors
`scripts/iam/register-mcp-resource.py`).

Secrets discipline: writes NOTHING secret. Reads a Management-API token from the environment. Obtain it
from the `babelstone-mgmt` M2M app and export it (never a literal on the command line):

    export LOGTO_MGMT_TOKEN=$(curl -s -A babelstone-iam/1.0 -u "$APP_ID:$APP_SECRET" \
      -d grant_type=client_credentials \
      --data-urlencode resource=https://default.logto.app/api \
      -d scope=all https://auth.babelstone.dev/oidc/token | jq -r .access_token)
    python3 scripts/iam/enable-default-mfa.py

Idempotent: if the desired factors + policy are already set, it makes no change. Prints the resulting
MFA config; exits non-zero on any API failure.

Config via env (defaults are the staging values):
    LOGTO_BASE_URL  default https://auth.babelstone.dev  (the default-tenant issuer host)
    MFA_POLICY      default UserControlled                (per the "offer, non-forcing" decision;
                    set Mandatory only to force enrollment on the next default-tenant/Grafana login)
"""
import json
import os
import sys
import urllib.error
import urllib.request

BASE = os.environ.get("LOGTO_BASE_URL", "https://auth.babelstone.dev").rstrip("/")
TOKEN = os.environ.get("LOGTO_MGMT_TOKEN", "").strip()
POLICY = os.environ.get("MFA_POLICY", "UserControlled").strip()

# The two SCA-strong factors we offer. WebAuthn (passkey) mirrors what the admin console already uses;
# TOTP is the portable authenticator-app factor. Logto's MfaFactor enum values are exactly these
# capitalisations ('Totp' / 'WebAuthn'; 'BackupCode' needs a primary factor and is intentionally
# omitted). These are the verificationRecords the getCustomJwtClaims strength-synthesis reads.
FACTORS = ["Totp", "WebAuthn"]


def api(method, path, body=None):
    req = urllib.request.Request(
        BASE + path,
        data=json.dumps(body).encode() if body is not None else None,
        method=method,
        headers={
            "Authorization": f"Bearer {TOKEN}",
            "Content-Type": "application/json",
            "Accept": "application/json",
            # Cloudflare's managed bot rules 1010-ban the default `Python-urllib/*` UA in front of
            # auth.babelstone.dev — a descriptive UA is mandatory (same as register-mcp-resource.py).
            "User-Agent": "babelstone-iam-enable-mfa/1.0",
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

    status, sie = api("GET", "/api/sign-in-exp")
    if status != 200:
        sys.exit(f"GET /api/sign-in-exp failed: {status} {sie}")

    current = sie.get("mfa") or {}
    cur_factors = sorted(current.get("factors") or [])
    cur_policy = current.get("policy")
    want_factors = sorted(FACTORS)

    if cur_factors == want_factors and cur_policy == POLICY:
        print(f"MFA already set: factors={cur_factors} policy={cur_policy} — no change.")
        return

    print(f"MFA before: factors={cur_factors or '[]'} policy={cur_policy}")
    status, res = api("PATCH", "/api/sign-in-exp", {"mfa": {"factors": FACTORS, "policy": POLICY}})
    if status != 200:
        sys.exit(f"PATCH /api/sign-in-exp mfa failed: {status} {res}")

    new = res.get("mfa") or {}
    print(f"MFA after:  factors={sorted(new.get('factors') or [])} policy={new.get('policy')}")
    print("\nOK — default-tenant MFA offers WebAuthn + TOTP (UserControlled). "
          "Step-up freshness rides native auth_time; strength is the synthesised non-acr claim "
          "(ADR-IC-010 §A16). Nothing forces enrollment until a factor is demanded on a step-up.")


if __name__ == "__main__":
    main()
