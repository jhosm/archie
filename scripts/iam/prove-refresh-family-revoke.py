#!/usr/bin/env python3
"""Empirically prove C3: refresh-token reuse revokes the WHOLE token family (ADR-IC-021 C3).

Plain English: if an attacker steals a refresh token and the real client later rotates it, replaying
the stolen (now-consumed) token must not just fail — it must kill the ENTIRE family of tokens for that
login, including the newest one the legitimate client is holding. That whole-family revoke is the
theft-response behaviour ADR-IC-021 §C3 (Test ID `IAM_REFRESH_REUSE_FAMILY_REVOKE`) depends on, and
which the ADR flagged as "must be proven before relied upon". This script proves it end-to-end against
the live staging Logto.

Why it is non-trivial: a refresh token only exists off an INTERACTIVE authorization-code grant
(client_credentials/M2M issues none), so the script drives a HEADLESS auth-code + PKCE(S256) login
through Logto's Experience API (`/api/experience`, a cookie-bound flow) — no browser. It uses a
throwaway PUBLIC (Native) client so rotation fires on the FIRST refresh (a confidential client only
rotates at >=70% TTL, which would make the reuse test vacuous), and a throwaway user, both created +
deleted here.

The proof (steps 3-5):
  RT0 = mint via the headless login (offline_access).
  rotate TWICE: RT0 -> RT1 -> RT2 (each a NEW token; the second rotation fully consumes RT0).
  wait past the ROTATION GRACE window (see below), then:
  replay RT0 -> expect `invalid_grant`          (reuse-detection fired).
  replay the newest RT2 -> expect `invalid_grant` TOO (the WHOLE family/grantId was revoked == C3 proof).

IMPORTANT nuance discovered empirically (Logto 1.41.0): a just-rotated refresh token stays valid for a
brief GRACE window (a few seconds — Logto tolerates concurrent refreshes / client retries), so a reuse
test that replays IMMEDIATELY false-negatives (the "reused" token is still legitimately accepted, minting
yet another token). The proof must wait past the grace (C3_GRACE_WAIT, default 5 s) before replaying.

Secrets discipline: reads the Management-API token from `$LOGTO_MGMT_TOKEN` (never a literal). The
throwaway user's password is generated locally per run and never persisted or echoed. Tokens are held
in memory only. The throwaway client + user are deleted in a `finally` (best-effort). This is a
WRITE/stateful flow (creates + deletes users, mints + revokes tokens) — it must be run by a maintainer
against staging, never in read-only research mode, and it self-cleans so it does not pollute audit state.

Env (defaults are the staging values):
    LOGTO_BASE_URL   default https://auth.babelstone.dev   (the default-tenant issuer host)
    LOGTO_MGMT_TOKEN required — a Management-API token (client_credentials, resource=.../api, scope=all)
    C3_KEEP_FIXTURES set to 1 to skip the user/client cleanup (for debugging a failed run)
"""
import base64
import hashlib
import http.cookiejar
import json
import os
import secrets
import sys
import time
import urllib.error
import urllib.parse
import urllib.request

BASE = os.environ.get("LOGTO_BASE_URL", "https://auth.babelstone.dev").rstrip("/")
MGMT_TOKEN = os.environ.get("LOGTO_MGMT_TOKEN", "").strip()
KEEP = os.environ.get("C3_KEEP_FIXTURES", "") == "1"
GRACE = int(os.environ.get("C3_GRACE_WAIT", "5"))  # seconds to wait past Logto's rotation grace window
UA = "babelstone-iam-c3-refresh/1.0"  # non-default UA: Cloudflare 1010-bans Python-urllib in front of auth
REDIRECT_URI = "http://localhost:8765/callback"  # never actually dialled — the code is read off the 302
AUTH_HOST = urllib.parse.urlparse(BASE).netloc


def _b64url(b: bytes) -> str:
    return base64.urlsafe_b64encode(b).rstrip(b"=").decode()


def mgmt(method, path, body=None):
    """Management API call (Bearer token). `path` MUST include the /api prefix."""
    req = urllib.request.Request(
        BASE + path,
        data=json.dumps(body).encode() if body is not None else None,
        method=method,
        headers={
            "Authorization": f"Bearer {MGMT_TOKEN}",
            "Content-Type": "application/json",
            "Accept": "application/json",
            "User-Agent": UA,
        },
    )
    try:
        with urllib.request.urlopen(req, timeout=25) as r:
            raw = r.read()
            return r.status, (json.loads(raw) if raw.strip() else None)
    except urllib.error.HTTPError as e:
        raw = e.read().decode(errors="replace")
        try:
            return e.code, json.loads(raw or "null")
        except json.JSONDecodeError:
            return e.code, {"_nonjson": raw[:300]}


class _StopAtRedirect(urllib.request.HTTPRedirectHandler):
    """Follow same-origin (auth host) redirects, but STOP at the cross-origin hop to REDIRECT_URI so we
    can read the authorization `code` off its Location header instead of dialling localhost."""

    def redirect_request(self, req, fp, code, msg, headers, newurl):
        if urllib.parse.urlparse(newurl).netloc != AUTH_HOST:
            return None  # stop — urlopen returns the 3xx response; caller reads Location
        return super().redirect_request(req, fp, code, msg, headers, newurl)


def make_browser():
    """A cookie-jar opener that behaves like a browser for the interaction flow."""
    jar = http.cookiejar.CookieJar()
    opener = urllib.request.build_opener(
        urllib.request.HTTPCookieProcessor(jar), _StopAtRedirect()
    )
    opener.addheaders = [("User-Agent", UA)]
    return opener


def browse(opener, method, url, body=None, json_body=None, headers=None):
    data, hdrs = None, dict(headers or {})
    if json_body is not None:
        data = json.dumps(json_body).encode()
        hdrs.setdefault("Content-Type", "application/json")
    elif body is not None:
        data = urllib.parse.urlencode(body).encode()
        hdrs.setdefault("Content-Type", "application/x-www-form-urlencoded")
    req = urllib.request.Request(url, data=data, method=method, headers=hdrs)
    try:
        with opener.open(req, timeout=25) as r:
            raw = r.read()
            return r.status, dict(r.headers), raw
    except urllib.error.HTTPError as e:
        return e.code, dict(e.headers), e.read()


def token_call(form):
    """POST /oidc/token (no auth: public client, client_id in the body). Returns (status, json)."""
    req = urllib.request.Request(
        f"{BASE}/oidc/token",
        data=urllib.parse.urlencode(form).encode(),
        method="POST",
        headers={"Content-Type": "application/x-www-form-urlencoded", "Accept": "application/json", "User-Agent": UA},
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
    if not MGMT_TOKEN:
        sys.exit("LOGTO_MGMT_TOKEN is unset — export a Management-API token (see the module docstring).")

    app_id = user_id = None
    username = f"c3probe{secrets.token_hex(5)}"  # Logto username regex ^[A-Za-z_]\w*$ — no hyphens
    password = secrets.token_urlsafe(18)  # generated locally; never persisted or printed
    ok = False
    try:
        # 1. throwaway PUBLIC (Native) client with offline_access + our redirect, and a throwaway user.
        st, app = mgmt("POST", "/api/applications", {
            "name": f"c3-refresh-probe-{secrets.token_hex(3)}",
            "type": "Native",  # public client -> rotation fires on EVERY refresh (the >=70%-TTL trap avoided)
            "description": "Ephemeral C3 refresh-family-revoke probe (auto-deleted).",
            "oidcClientMetadata": {"redirectUris": [REDIRECT_URI], "postLogoutRedirectUris": []},
            # Explicitly enable refresh-token rotation (rotate-and-consume + reuse-detection). Logto's
            # Console defaults this on; set it explicitly so the probe does not depend on an API-created
            # app inheriting that default. (Reuse-detection still only fires AFTER the rotation grace
            # window — hence the wait before step 4.)
            "customClientMetadata": {"rotateRefreshToken": True},
        })
        if st not in (200, 201):
            sys.exit(f"create probe client failed: {st} {app}")
        app_id = app["id"]
        print(f"[1] probe client: {app_id} (Native/public, rotateRefreshToken)")

        st, usr = mgmt("POST", "/api/users", {"username": username, "password": password})
        if st not in (200, 201):
            sys.exit(f"create probe user failed: {st} {usr}")
        user_id = usr["id"]
        print(f"[1] probe user:   {user_id} ({username})")

        # 2. headless auth-code + PKCE(S256) login -> RT0.
        verifier = secrets.token_urlsafe(64)[:96]
        challenge = _b64url(hashlib.sha256(verifier.encode()).digest())
        state = secrets.token_urlsafe(16)
        nonce = secrets.token_urlsafe(16)
        opener = make_browser()

        authorize = f"{BASE}/oidc/auth?" + urllib.parse.urlencode({
            "client_id": app_id, "response_type": "code", "redirect_uri": REDIRECT_URI,
            "scope": "openid offline_access", "code_challenge": challenge,
            "code_challenge_method": "S256", "state": state, "nonce": nonce, "prompt": "consent",
        })
        st, _, _ = browse(opener, "GET", authorize)  # sets the interaction/session cookie
        print(f"[2] GET /oidc/auth -> {st} (interaction cookie established)")

        st, _, raw = browse(opener, "PUT", f"{BASE}/api/experience", json_body={"interactionEvent": "SignIn"})
        if st not in (200, 201, 204):
            sys.exit(f"[2] PUT /api/experience SignIn failed: {st} {raw[:300]}")

        st, _, raw = browse(opener, "POST", f"{BASE}/api/experience/verification/password",
                            json_body={"identifier": {"type": "username", "value": username}, "password": password})
        if st != 200:
            sys.exit(f"[2] password verification failed: {st} {raw[:300]}")
        verification_id = json.loads(raw).get("verificationId")
        print(f"[2] password verified (verificationId={verification_id})")

        st, _, raw = browse(opener, "POST", f"{BASE}/api/experience/identification",
                            json_body={"verificationId": verification_id})
        if st not in (200, 204):
            sys.exit(f"[2] identification failed: {st} {raw[:300]}")

        st, _, raw = browse(opener, "POST", f"{BASE}/api/experience/submit")
        if st == 422 and json.loads(raw or b"{}").get("code") == "user.suggest_mfa":
            # slice 2 enabled MFA factors (UserControlled); the throwaway user has none bound, so Logto
            # SUGGESTS binding. Skip the OPTIONAL binding (UserControlled => allowed) and re-submit.
            sk, _, skraw = browse(opener, "POST", f"{BASE}/api/experience/profile/mfa/mfa-skipped")
            if sk not in (200, 204):
                sys.exit(f"[2] mfa-skip failed: {sk} {skraw[:300]}")
            print("[2] MFA binding suggestion skipped (UserControlled)")
            st, _, raw = browse(opener, "POST", f"{BASE}/api/experience/submit")
        if st != 200:
            sys.exit(f"[2] submit failed: {st} {raw[:300]}")
        redirect_to = json.loads(raw).get("redirectTo")
        if not redirect_to:
            sys.exit(f"[2] submit returned no redirectTo: {raw[:300]}")

        # follow redirectTo; the opener STOPS at the cross-origin hop so we can read the code off Location.
        st, hdrs, _ = browse(opener, "GET", redirect_to)
        location = hdrs.get("Location", "")
        qs = urllib.parse.parse_qs(urllib.parse.urlparse(location).query)
        code = (qs.get("code") or [None])[0]
        if qs.get("state", [None])[0] != state or not code:
            sys.exit(f"[2] no code / state mismatch. status={st} location={location[:200]}")
        print(f"[2] authorization code captured (state verified)")

        st, tok = token_call({
            "grant_type": "authorization_code", "code": code, "redirect_uri": REDIRECT_URI,
            "code_verifier": verifier, "client_id": app_id,
        })
        if st != 200 or "refresh_token" not in (tok or {}):
            sys.exit(f"[2] code->token exchange failed or no refresh_token: {st} {tok}")
        rt0 = tok["refresh_token"]
        print(f"[2] RT0 minted (offline_access) ✓")

        def refresh(rt):
            return token_call({"grant_type": "refresh_token", "refresh_token": rt, "client_id": app_id})

        # 3. rotate TWICE (RT0 -> RT1 -> RT2). The second rotation USES RT1, which fully consumes RT0
        #    past node-oidc-provider's single-step rotation grace window (the prior token stays valid
        #    until the successor is first used). Each rotation must yield a NEW token.
        st, tok = refresh(rt0)
        if st != 200 or "refresh_token" not in (tok or {}):
            sys.exit(f"[3] rotation RT0->RT1 failed: {st} {tok}")
        rt1 = tok["refresh_token"]
        st, tok = refresh(rt1)
        if st != 200 or "refresh_token" not in (tok or {}):
            sys.exit(f"[3] rotation RT1->RT2 failed: {st} {tok}")
        rt2 = tok["refresh_token"]
        if len({rt0, rt1, rt2}) != 3:
            sys.exit(f"[3] tokens not all distinct (rotation not happening) — reuse test vacuous. ABORT.")
        print("[3] rotated twice: RT0 -> RT1 -> RT2 (all distinct) ✓")

        # Logto keeps a rotated refresh token valid for a brief GRACE window (to tolerate concurrent
        # refreshes / retries); reuse-detection only fires AFTER it. Replaying inside the grace
        # false-negatives (the reused token is still legitimately accepted). Wait past it.
        print(f"[3] waiting {GRACE}s past the rotation grace window before the reuse test...")
        time.sleep(GRACE)

        # 4. replay the ancient, fully-consumed RT0 -> expect invalid_grant (reuse-detection fired).
        st, err = refresh(rt0)
        reuse_rejected = (st == 400 and (err or {}).get("error") == "invalid_grant")
        print(f"[4] replay consumed RT0 -> {st} {(err or {}).get('error')}  "
              f"{'✓ invalid_grant (reuse detected)' if reuse_rejected else '✗ reuse NOT detected — RT0 still accepted'}")

        # 5. THE PROOF: the NEWEST live token RT2 must ALSO be dead now (whole-family/grantId revoke).
        st, err = refresh(rt2)
        family_revoked = (st == 400 and (err or {}).get("error") == "invalid_grant")
        print(f"[5] replay newest RT2 -> {st} {(err or {}).get('error')}  "
              f"{'✓ invalid_grant (WHOLE FAMILY revoked)' if family_revoked else '✗ RT2 STILL LIVE — family NOT revoked'}")

        ok = reuse_rejected and family_revoked
        print()
        if ok:
            print("RESULT: PASS — C3 proven. After the rotation grace window, replaying a rotated refresh "
                  "token revokes the whole family (both the ancient RT0 AND the newest RT2 -> invalid_grant). "
                  "IAM_REFRESH_REUSE_FAMILY_REVOKE holds on live Logto.")
        else:
            print("RESULT: FAIL — C3 NOT proven on this run. See steps [4]/[5] above. "
                  "(Distinguish invalid_grant from invalid_client/invalid_target before trusting a red result.)")
    finally:
        if not KEEP:
            deleted = []
            if user_id:
                mgmt("DELETE", f"/api/users/{user_id}"); deleted.append("user")
            if app_id:
                mgmt("DELETE", f"/api/applications/{app_id}"); deleted.append("client")
            print(f"[cleanup] deleted: {', '.join(deleted) or 'nothing'}.")
        else:
            print(f"[cleanup] SKIPPED (C3_KEEP_FIXTURES=1): user={user_id} app={app_id}")

    sys.exit(0 if ok else 1)


if __name__ == "__main__":
    main()
