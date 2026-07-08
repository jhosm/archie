#!/usr/bin/env python3
"""Tests for the Mission Control BFF auth surface (bd babelstone-zla1.10.8.1 / .2).

In plain English: these tests pin the two new safety behaviours of the demo's little proxy server.
First, that turning auth OFF (the laptop-dev default) leaves the server working exactly as before,
but REFUSES to boot if you expose it to the network without saying so out loud. Second, that turning
auth ON (oidc mode) really does put a login in front of every page: an unauthenticated visitor is
bounced into the standard OpenID-Connect + PKCE login, the /callback hands back a signed session
only for a valid, non-tampered login, and if the identity provider can't be reached the server
refuses to start rather than quietly serving everything open.

They are stdlib-only (a tiny in-process fake IdP stands in for Logto) and drive the real
serve.Handler over a loopback socket, so nothing here needs a network or a third-party package.
"""

from __future__ import annotations

import http.client
import http.server
import json
import os
import socket
import socketserver
import sys
import threading
import time
import urllib.parse
from contextlib import contextmanager

import pytest

# serve.py is a single-file script (no package); make it importable by path.
HERE = os.path.dirname(os.path.abspath(__file__))
if HERE not in sys.path:
    sys.path.insert(0, HERE)

import serve  # noqa: E402


# ── global-state fixture ─────────────────────────────────────────────────────────────────────
# serve.py reads its config into module globals at import. Each test tweaks a few of those, so we
# snapshot + restore the ones we touch to keep tests independent.
_TOUCHED = (
    "AUTH", "MC_AUTH_MODE", "MC_BIND", "MC_ALLOW_UNAUTHENTICATED",
    "OIDC_ISSUER", "OIDC_CLIENT_ID", "OIDC_CLIENT_SECRET", "OIDC_SCOPES",
    "OIDC_REDIRECT_URL", "MC_PUBLIC_BASE_URL", "MC_SESSION_SIGNING_KEY", "MC_SESSION_TTL",
    "ORCHESTRATOR_URL",
)


@pytest.fixture(autouse=True)
def _restore_serve_globals():
    saved = {name: getattr(serve, name) for name in _TOUCHED}
    # A clean default: ungated dev mode, loopback bind.
    serve.AUTH = None
    serve.MC_AUTH_MODE = "dev"
    serve.MC_BIND = "127.0.0.1"
    serve.MC_ALLOW_UNAUTHENTICATED = False
    yield
    for name, val in saved.items():
        setattr(serve, name, val)


# ── a tiny in-process fake OpenID-Connect provider ───────────────────────────────────────────
class _IdpHandler(http.server.BaseHTTPRequestHandler):
    def log_message(self, *a):  # keep test output quiet
        pass

    def _json(self, obj):
        body = json.dumps(obj).encode()
        self.send_response(200)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def _record_ua(self):
        uas = getattr(self.server, "user_agents", None)
        if uas is not None:
            uas.append((self.path, self.headers.get("User-Agent")))

    def do_GET(self):
        self._record_ua()
        if self.path.startswith("/.well-known/openid-configuration"):
            base = self.server.base
            doc = {
                "issuer": base,
                "authorization_endpoint": base + "/auth",
                "token_endpoint": base + "/token",
                "jwks_uri": base + "/jwks",
                "end_session_endpoint": base + "/session/end",
            }
            doc.update(getattr(self.server, "disc_overrides", {}) or {})  # tests can rewrite endpoints
            for k in getattr(self.server, "disc_drop", ()):               # …or omit a required field
                doc.pop(k, None)
            self._json(doc)
            return
        self.send_error(404)

    def do_POST(self):
        self._record_ua()
        if self.path == "/token":
            length = int(self.headers.get("Content-Length", 0) or 0)
            self.rfile.read(length)
            self._json({
                "id_token": self.server.id_token,
                "access_token": "opaque-access-token",
                "token_type": "Bearer",
                "expires_in": 3600,
            })
            return
        self.send_error(404)


@contextmanager
def fake_idp():
    httpd = http.server.ThreadingHTTPServer(("127.0.0.1", 0), _IdpHandler)
    httpd.base = "http://127.0.0.1:%d" % httpd.server_address[1]
    httpd.id_token = None
    httpd.disc_overrides = {}   # tests may inject a hostile/malformed discovery endpoint
    httpd.disc_drop = ()        # …or drop a required field (e.g. issuer)
    httpd.user_agents = []      # (path, User-Agent) seen on each backchannel request (bd zla1.10.12)
    t = threading.Thread(target=httpd.serve_forever, daemon=True)
    t.start()
    try:
        yield httpd
    finally:
        httpd.shutdown()
        httpd.server_close()


@contextmanager
def run_mc():
    """Start the real serve.Handler on an ephemeral loopback port (uses whatever serve.AUTH is)."""
    socketserver.ThreadingTCPServer.allow_reuse_address = True
    httpd = socketserver.ThreadingTCPServer(("127.0.0.1", 0), serve.Handler)
    port = httpd.server_address[1]
    t = threading.Thread(target=httpd.serve_forever, daemon=True)
    t.start()
    try:
        yield port
    finally:
        httpd.shutdown()
        httpd.server_close()


def http_req(port, path, method="GET", headers=None, follow=False):
    conn = http.client.HTTPConnection("127.0.0.1", port, timeout=5)
    conn.request(method, path, headers=headers or {})
    resp = conn.getresponse()
    body = resp.read()
    status = resp.status
    hdrs = resp.getheaders()
    conn.close()
    return status, hdrs, body


def set_cookies(hdrs):
    """All Set-Cookie header values from an http.client getheaders() list."""
    return [v for (k, v) in hdrs if k.lower() == "set-cookie"]


def header(hdrs, name):
    for (k, v) in hdrs:
        if k.lower() == name.lower():
            return v
    return None


def cookie_value(set_cookie_line):
    return set_cookie_line.split(";", 1)[0].split("=", 1)[1]


def make_jwt(**claims):
    """A minimal, UNSIGNED JWT (the sig segment is ignored — serve.py validates claims + trusts the
    TLS backchannel, OIDC Core §3.1.3.7 item 6, not the signature)."""
    header_seg = serve._b64u(json.dumps({"alg": "none", "typ": "JWT"}).encode())
    payload_seg = serve._b64u(json.dumps(claims).encode())
    return header_seg + "." + payload_seg + "." + serve._b64u(b"sig")


def find_free_port():
    s = socket.socket()
    s.bind(("127.0.0.1", 0))
    p = s.getsockname()[1]
    s.close()
    return p


# The two cookie families are signed with DISTINCT keys derived from MC_SESSION_SIGNING_KEY, and each
# carries a mandatory `typ` — so tests must mint each cookie the same way serve.py does.
def tx_key(base):
    return serve._derive_key(base, serve._TX_PURPOSE)


def sess_key(base):
    return serve._derive_key(base, serve._SESSION_PURPOSE)


def make_session_cookie(base, sub="user-1", exp=None, typ=None):
    exp = int(time.time()) + 300 if exp is None else exp
    payload = {"typ": serve._TYP_SESSION if typ is None else typ, "v": 1, "sub": sub, "exp": exp}
    return serve._sign_cookie(payload, sess_key(base))


# ── dev mode: byte-for-byte passthrough (no gate) ─────────────────────────────────────────────
def test_dev_mode_serves_ui_without_a_gate():
    # AUTH is None (dev). GET / must serve the static UI with no redirect and no auth cookie.
    with run_mc() as port:
        status, hdrs, body = http_req(port, "/", headers={"Accept": "text/html"})
    assert status == 200
    assert b"<html" in body.lower() or b"<!doctype" in body.lower()
    assert set_cookies(hdrs) == []  # no session/tx cookie is ever minted in dev mode


def test_dev_mode_does_not_gate_a_proxy_prefix():
    # An unauthenticated /v1/* call is NOT 302'd to a login in dev mode — it is relayed (and 502s
    # because no engine is running). The point: no auth gate stands in front of it.
    with run_mc() as port:
        status, _hdrs, _body = http_req(port, "/v1/deposits/none")
    assert status == 502  # relayed to a dead upstream, not gated


# ── the fail-safe matrix (bd babelstone-zla1.10.8.2) ──────────────────────────────────────────
def test_failsafe_loopback_dev_starts_silently():
    serve.MC_AUTH_MODE = "dev"
    serve.MC_BIND = "127.0.0.1"
    serve.MC_ALLOW_UNAUTHENTICATED = False
    serve._preflight()  # must NOT raise
    assert serve.AUTH is None


def test_failsafe_public_dev_no_override_refuses(capsys):
    serve.MC_AUTH_MODE = "dev"
    serve.MC_BIND = "0.0.0.0"
    serve.MC_ALLOW_UNAUTHENTICATED = False
    with pytest.raises(SystemExit) as ei:
        serve._preflight()
    assert ei.value.code == 2
    assert "REFUSING TO START" in capsys.readouterr().err


def test_failsafe_public_dev_with_override_starts_and_warns(capsys):
    serve.MC_AUTH_MODE = "dev"
    serve.MC_BIND = "0.0.0.0"
    serve.MC_ALLOW_UNAUTHENTICATED = True
    serve._preflight()  # must NOT raise
    assert serve.AUTH is None
    err = capsys.readouterr().err
    assert "WARNING" in err and "UNAUTHENTICATED" in err


def test_unknown_auth_mode_refuses():
    serve.MC_AUTH_MODE = "banana"
    with pytest.raises(SystemExit):
        serve._preflight()


def test_is_public_bind_classification():
    assert serve._is_public_bind("0.0.0.0") is True
    assert serve._is_public_bind("::") is True
    assert serve._is_public_bind("10.0.0.5") is True
    assert serve._is_public_bind("127.0.0.1") is False
    assert serve._is_public_bind("localhost") is False
    assert serve._is_public_bind("::1") is False
    assert serve._is_public_bind("") is False


# ── oidc mode: fail-closed on missing / unreachable config ────────────────────────────────────
def test_oidc_missing_config_fails_closed():
    serve.OIDC_ISSUER = ""
    serve.OIDC_CLIENT_ID = ""
    serve.MC_SESSION_SIGNING_KEY = ""
    with pytest.raises(serve.OidcConfigError):
        serve._build_oidc_gate()


def test_oidc_non_loopback_http_issuer_refused():
    serve.OIDC_ISSUER = "http://idp.example.test"  # http + non-loopback voids the TLS-backchannel trust
    serve.OIDC_CLIENT_ID = "mc"
    serve.MC_SESSION_SIGNING_KEY = "k"
    serve.OIDC_REDIRECT_URL = "https://mc.example/callback"
    with pytest.raises(serve.OidcConfigError):
        serve._build_oidc_gate()


def test_oidc_unreachable_discovery_fails_closed():
    dead = find_free_port()  # nothing is listening here
    serve.OIDC_ISSUER = "http://127.0.0.1:%d" % dead
    serve.OIDC_CLIENT_ID = "mc"
    serve.MC_SESSION_SIGNING_KEY = "k"
    serve.OIDC_REDIRECT_URL = "https://mc.example/callback"
    with pytest.raises(serve.OidcConfigError):
        serve._build_oidc_gate()


def test_oidc_preflight_unreachable_is_systemexit_not_ungated(capsys):
    dead = find_free_port()
    serve.MC_AUTH_MODE = "oidc"
    serve.OIDC_ISSUER = "http://127.0.0.1:%d" % dead
    serve.OIDC_CLIENT_ID = "mc"
    serve.MC_SESSION_SIGNING_KEY = "k"
    serve.OIDC_REDIRECT_URL = "https://mc.example/callback"
    with pytest.raises(SystemExit):
        serve._preflight()
    assert serve.AUTH is None  # NEVER falls back to an ungated server
    assert "fails CLOSED" in capsys.readouterr().err


def test_oidc_derives_redirect_from_public_base():
    with fake_idp() as idp:
        serve.OIDC_ISSUER = idp.base
        serve.OIDC_CLIENT_ID = "mc"
        serve.MC_SESSION_SIGNING_KEY = "k"
        serve.OIDC_REDIRECT_URL = ""
        serve.MC_PUBLIC_BASE_URL = "https://mc.example"
        gate = serve._build_oidc_gate()
    assert gate.redirect_url == "https://mc.example/callback"
    assert gate.token_endpoint == idp.base + "/token"  # discovered, not hardcoded


def test_oidc_backchannel_sends_descriptive_user_agent():
    # Python urllib's default "Python-urllib/<ver>" UA is 403'd by the CDN in front of the issuer
    # (Cloudflare on auth.babelstone.dev), which crash-looped the fail-closed gate in staging. The
    # backchannel (discovery here; the token exchange shares the same header) must send a descriptive,
    # non-bot User-Agent (bd babelstone-zla1.10.12).
    with fake_idp() as idp:
        serve.OIDC_ISSUER = idp.base
        serve.OIDC_CLIENT_ID = "mc"
        serve.MC_SESSION_SIGNING_KEY = "k"
        serve.OIDC_REDIRECT_URL = "https://mc.example/callback"
        serve._build_oidc_gate()  # performs discovery over the backchannel
        seen = [ua for (path, ua) in idp.user_agents if path.startswith("/.well-known")]
    assert seen, "discovery was not called"
    assert all(ua == serve._OIDC_USER_AGENT for ua in seen)
    assert all("python-urllib" not in (ua or "").lower() for ua in seen)


# ── oidc mode: the interactive redirect carries PKCE S256 ─────────────────────────────────────
def _gate_for(idp, signing_key="unit-test-signing-key", client_id="mc-client"):
    gate = serve._OidcGate(
        issuer=idp.base, client_id=client_id, client_secret="s3cr3t",
        scopes="openid profile email", redirect_url="https://mc.example/callback",
        signing_key=signing_key, session_ttl=3600)
    gate.discover()
    return gate


def test_oidc_unauthenticated_navigation_redirects_with_pkce_s256():
    with fake_idp() as idp:
        serve.AUTH = _gate_for(idp)
        with run_mc() as port:
            status, hdrs, _ = http_req(port, "/", headers={"Accept": "text/html"})
    assert status == 302
    loc = header(hdrs, "Location")
    q = urllib.parse.parse_qs(urllib.parse.urlsplit(loc).query)
    assert loc.startswith(idp.base + "/auth")
    assert q["response_type"] == ["code"]
    assert q["code_challenge_method"] == ["S256"]
    assert q["code_challenge"] and q["state"] and q["nonce"]

    # The challenge must be the real S256 of the verifier stashed in the (signed) tx cookie.
    tx_cookie = cookie_value(set_cookies(hdrs)[0])
    tx = serve._unsign_cookie(tx_cookie, tx_key("unit-test-signing-key"))
    assert tx is not None
    assert tx["typ"] == serve._TYP_TX
    expected = serve._b64u(serve.hashlib.sha256(tx["verifier"].encode()).digest())
    assert q["code_challenge"][0] == expected
    assert q["state"][0] == tx["state"]


def test_oidc_xhr_request_gets_401_not_redirect():
    with fake_idp() as idp:
        serve.AUTH = _gate_for(idp)
        with run_mc() as port:
            status, hdrs, _ = http_req(port, "/v1/deposits/x", headers={"Accept": "application/json"})
    assert status == 401  # a fetch/XHR is 401'd, not sent an opaque cross-origin 302
    assert set_cookies(hdrs) == []


# ── oidc mode: /callback establishes a session, and rejects tampering ─────────────────────────
def _begin_login(port, signing_key):
    """Drive the redirect leg; return (tx_cookie_value, state, nonce)."""
    status, hdrs, _ = http_req(port, "/deposits", headers={"Accept": "text/html"})
    assert status == 302
    tx_cookie = cookie_value(set_cookies(hdrs)[0])
    tx = serve._unsign_cookie(tx_cookie, tx_key(signing_key))
    return tx_cookie, tx["state"], tx["nonce"]


def test_oidc_callback_sets_session_for_valid_code():
    key = "unit-test-signing-key"
    with fake_idp() as idp:
        serve.AUTH = _gate_for(idp, signing_key=key)
        with run_mc() as port:
            tx_cookie, state, nonce = _begin_login(port, key)
            idp.id_token = make_jwt(iss=idp.base, aud="mc-client", sub="user-1",
                                    email="op@babelstone.dev", nonce=nonce,
                                    exp=int(time.time()) + 300, iat=int(time.time()))
            status, hdrs, _ = http_req(
                port, "/callback?code=good-code&state=%s" % state,
                headers={"Cookie": serve._SESSION_COOKIE + "=; " + serve._TX_COOKIE + "=" + tx_cookie})

    assert status == 302
    assert header(hdrs, "Location") == "/deposits"  # bounced back to where we were headed
    session_lines = [c for c in set_cookies(hdrs) if c.startswith(serve._SESSION_COOKIE + "=")]
    assert session_lines, "a session cookie must be set"
    sess = serve._unsign_cookie(cookie_value(session_lines[0]), sess_key(key))
    assert sess["typ"] == serve._TYP_SESSION
    assert sess["sub"] == "user-1"
    assert sess["email"] == "op@babelstone.dev"
    assert sess["exp"] > int(time.time())


def test_oidc_callback_rejects_bad_state():
    key = "unit-test-signing-key"
    with fake_idp() as idp:
        serve.AUTH = _gate_for(idp, signing_key=key)
        with run_mc() as port:
            tx_cookie, _state, nonce = _begin_login(port, key)
            idp.id_token = make_jwt(iss=idp.base, aud="mc-client", sub="user-1",
                                    nonce=nonce, exp=int(time.time()) + 300)
            status, hdrs, _ = http_req(
                port, "/callback?code=good-code&state=WRONG-STATE",
                headers={"Cookie": serve._TX_COOKIE + "=" + tx_cookie})
    assert status == 400
    assert not [c for c in set_cookies(hdrs) if c.startswith(serve._SESSION_COOKIE + "=")]


def test_oidc_callback_rejects_nonce_mismatch():
    key = "unit-test-signing-key"
    with fake_idp() as idp:
        serve.AUTH = _gate_for(idp, signing_key=key)
        with run_mc() as port:
            tx_cookie, state, _nonce = _begin_login(port, key)
            idp.id_token = make_jwt(iss=idp.base, aud="mc-client", sub="user-1",
                                    nonce="not-the-real-nonce", exp=int(time.time()) + 300)
            status, hdrs, _ = http_req(
                port, "/callback?code=good-code&state=%s" % state,
                headers={"Cookie": serve._TX_COOKIE + "=" + tx_cookie})
    assert status == 400  # replay/forgery guard: the id_token nonce must match the login tx


def test_oidc_expired_session_is_not_accepted():
    key = "unit-test-signing-key"
    expired = make_session_cookie(key, exp=int(time.time()) - 10)
    with fake_idp() as idp:
        serve.AUTH = _gate_for(idp, signing_key=key)
        with run_mc() as port:
            status, hdrs, _ = http_req(
                port, "/", headers={"Accept": "text/html",
                                    "Cookie": serve._SESSION_COOKIE + "=" + expired})
    assert status == 302  # re-login, NOT 200 — an expired session is worthless


def test_oidc_valid_session_is_admitted():
    key = "unit-test-signing-key"
    good = make_session_cookie(key)
    with fake_idp() as idp:
        serve.AUTH = _gate_for(idp, signing_key=key)
        with run_mc() as port:
            status, _hdrs, body = http_req(
                port, "/", headers={"Accept": "text/html",
                                    "Cookie": serve._SESSION_COOKIE + "=" + good})
    assert status == 200  # a valid session is served the UI
    assert b"<html" in body.lower() or b"<!doctype" in body.lower()


def test_oidc_tampered_session_cookie_rejected():
    key = "unit-test-signing-key"
    good = make_session_cookie(key)
    body_seg, _sig = good.split(".", 1)
    forged = body_seg + "." + serve._b64u(b"forged-signature")
    with fake_idp() as idp:
        serve.AUTH = _gate_for(idp, signing_key=key)
        with run_mc() as port:
            status, _hdrs, _ = http_req(
                port, "/", headers={"Accept": "text/html",
                                    "Cookie": serve._SESSION_COOKIE + "=" + forged})
    assert status == 302  # a bad HMAC → treated as no session


# ── X-Client-Id attestation on the /api/v1/* arm (bd babelstone-zla1.10.8.4) ──────────────────
# The demo BFF stands in for Kong on the orchestrator edge: it must ATTEST X-Client-Id from the
# validated identity, not forge a static one. In oidc mode that identity is the session `sub`; in
# dev mode it stays the static DEMO_CLIENT_ID. A tiny fake orchestrator echoes back the header it
# received so we can assert exactly what the BFF attested.
class _OrchHandler(http.server.BaseHTTPRequestHandler):
    def log_message(self, *a):
        pass

    def _echo(self):
        body = json.dumps({"x_client_id": self.headers.get("X-Client-Id")}).encode()
        self.send_response(200)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        if self.command != "HEAD":
            self.wfile.write(body)

    def do_GET(self):
        self._echo()

    def do_POST(self):
        length = int(self.headers.get("Content-Length", 0) or 0)
        self.rfile.read(length)
        self._echo()


@contextmanager
def fake_orchestrator():
    httpd = http.server.ThreadingHTTPServer(("127.0.0.1", 0), _OrchHandler)
    base = "http://127.0.0.1:%d" % httpd.server_address[1]
    t = threading.Thread(target=httpd.serve_forever, daemon=True)
    t.start()
    try:
        yield base
    finally:
        httpd.shutdown()
        httpd.server_close()


def test_oidc_api_arm_attests_x_client_id_from_session_sub():
    # oidc mode: a /api/v1/* call bearing a valid session cookie must reach the orchestrator with
    # X-Client-Id == that session's `sub` (mirroring Kong's attest-from-sub) — NOT the static demo id.
    key = "unit-test-signing-key"
    good = make_session_cookie(key, sub="op-42")
    with fake_idp() as idp, fake_orchestrator() as orch:
        serve.AUTH = _gate_for(idp, signing_key=key)
        serve.ORCHESTRATOR_URL = orch
        with run_mc() as port:
            status, _hdrs, body = http_req(
                port, "/api/v1/deposits/x", method="POST",
                headers={"Cookie": serve._SESSION_COOKIE + "=" + good,
                         "Content-Length": "0"})
    assert status == 200
    echoed = json.loads(body)["x_client_id"]
    assert echoed == "op-42"                 # attested from the session sub
    assert echoed != serve.DEMO_CLIENT_ID    # NOT the forged static CLI-DEMO-0001


def test_dev_mode_api_arm_still_carries_static_demo_client_id():
    # dev mode (AUTH is None): the /api/v1/* arm keeps the static DEMO_CLIENT_ID, byte-for-byte the
    # pre-auth behaviour — no session exists to attest from.
    with fake_orchestrator() as orch:
        serve.ORCHESTRATOR_URL = orch  # AUTH stays None from the autouse fixture
        with run_mc() as port:
            status, _hdrs, body = http_req(
                port, "/api/v1/deposits/x", method="POST",
                headers={"Content-Length": "0"})
    assert status == 200
    assert json.loads(body)["x_client_id"] == serve.DEMO_CLIENT_ID  # CLI-DEMO-0001


# ── pure helpers ──────────────────────────────────────────────────────────────────────────────
def test_safe_return_to_blocks_open_redirect():
    assert serve._safe_return_to("/deposits") == "/deposits"
    assert serve._safe_return_to("/deposits?x=1#f") == "/deposits?x=1#f"
    assert serve._safe_return_to("//evil.example/x") == "/"
    assert serve._safe_return_to("https://evil.example") == "/"
    assert serve._safe_return_to("") == "/"
    assert serve._safe_return_to(None) == "/"
    # backslash variants — browsers normalise '\' to '/', so these are open-redirect vectors too.
    assert serve._safe_return_to("/\\evil.example") == "/"
    assert serve._safe_return_to("\\\\evil.example") == "/"
    assert serve._safe_return_to("/path\\to") == "/"


def test_sign_unsign_roundtrip_and_tamper():
    key = "k"
    tok = serve._sign_cookie({"a": 1, "b": "two"}, key)
    assert serve._unsign_cookie(tok, key) == {"a": 1, "b": "two"}
    assert serve._unsign_cookie(tok, "other-key") is None  # wrong key → rejected
    body_seg = tok.split(".", 1)[0]
    assert serve._unsign_cookie(body_seg + ".AAAA", key) is None  # tampered sig → rejected


def test_derived_keys_are_distinct():
    # The two cookie families MUST NOT share a key — that is what makes a tx blob unusable as a
    # session and vice-versa, independent of the `typ` belt.
    assert tx_key("base") != sess_key("base")
    assert tx_key("base") != "base"


# ── SECURITY REGRESSION: cross-cookie type confusion (auth-bypass finding #1) ──────────────────
def test_tx_cookie_cannot_be_used_as_session():
    # The 302 hands EVERY anonymous visitor a validly-signed tx cookie. Replaying that blob under the
    # session cookie name must NOT authenticate them (distinct key + mandatory typ).
    key = "unit-test-signing-key"
    with fake_idp() as idp:
        serve.AUTH = _gate_for(idp, signing_key=key)
        with run_mc() as port:
            tx_cookie, _state, _nonce = _begin_login(port, key)
            nav_status, _h1, _ = http_req(
                port, "/", headers={"Accept": "text/html",
                                    "Cookie": serve._SESSION_COOKIE + "=" + tx_cookie})
            xhr_status, _h2, _ = http_req(
                port, "/v1/x", headers={"Accept": "application/json",
                                        "Cookie": serve._SESSION_COOKIE + "=" + tx_cookie})
    assert nav_status == 302   # re-login, NOT 200 — the tx blob is not a session
    assert xhr_status == 401   # and an API call with it is unauthenticated, not relayed


def test_session_blob_under_tx_name_rejected_cleanly():
    # The reverse confusion: a real session blob presented under the tx cookie name at /callback must
    # be rejected as a CLEAN 400 (login failed), never a 500 — read_tx returns None (wrong key/typ),
    # so the state check fails on tx-is-None before any KeyError can happen.
    key = "unit-test-signing-key"
    session_blob = make_session_cookie(key)
    with fake_idp() as idp:
        serve.AUTH = _gate_for(idp, signing_key=key)
        with run_mc() as port:
            status, hdrs, _ = http_req(
                port, "/callback?code=x&state=whatever",
                headers={"Cookie": serve._TX_COOKIE + "=" + session_blob})
    assert status == 400
    assert not [c for c in set_cookies(hdrs) if c.startswith(serve._SESSION_COOKIE + "=")]


def test_session_with_empty_sub_rejected():
    # A session must carry a non-empty subject to be a valid identity.
    key = "unit-test-signing-key"
    no_sub = make_session_cookie(key, sub="")
    with fake_idp() as idp:
        serve.AUTH = _gate_for(idp, signing_key=key)
        with run_mc() as port:
            status, _hdrs, _ = http_req(
                port, "/", headers={"Accept": "text/html",
                                    "Cookie": serve._SESSION_COOKIE + "=" + no_sub})
    assert status == 302


# ── SECURITY REGRESSION: HEAD bypasses the gate (auth-bypass finding #2) ───────────────────────
def test_head_is_gated_in_oidc_mode():
    key = "unit-test-signing-key"
    with fake_idp() as idp:
        serve.AUTH = _gate_for(idp, signing_key=key)
        with run_mc() as port:
            status, _hdrs, _ = http_req(port, "/", method="HEAD", headers={"Accept": "text/html"})
    assert status in (302, 401)  # HEAD must not fall through to the static tree ungated


def test_head_admitted_with_valid_session():
    key = "unit-test-signing-key"
    good = make_session_cookie(key)
    with fake_idp() as idp:
        serve.AUTH = _gate_for(idp, signing_key=key)
        with run_mc() as port:
            status, _hdrs, _ = http_req(port, "/", method="HEAD",
                                        headers={"Cookie": serve._SESSION_COOKIE + "=" + good})
    assert status == 200


def test_head_served_in_dev_mode():
    with run_mc() as port:  # AUTH is None (dev) — HEAD still works, byte-for-byte unchanged
        status, _hdrs, _ = http_req(port, "/", method="HEAD")
    assert status == 200


# ── HARDENING REGRESSION: discovered-endpoint TLS (finding #3) & issuer required (finding #6) ──
def test_oidc_non_loopback_http_token_endpoint_refused():
    # A discovery doc that advertises an http (non-loopback) token_endpoint voids the TLS-backchannel
    # premise, so discover() must fail closed.
    with fake_idp() as idp:
        idp.disc_overrides = {"token_endpoint": "http://idp.example.test/token"}
        with pytest.raises(serve.OidcConfigError):
            _gate_for(idp)


def test_oidc_https_endpoints_on_non_loopback_ok():
    # https endpoints on a non-loopback host are fine (the normal deployment shape).
    with fake_idp() as idp:
        idp.disc_overrides = {
            "authorization_endpoint": "https://idp.example.test/auth",
            "token_endpoint": "https://idp.example.test/token",
            "jwks_uri": "https://idp.example.test/jwks",
        }
        gate = _gate_for(idp)
    assert gate.token_endpoint == "https://idp.example.test/token"


def test_oidc_discovery_missing_issuer_refused():
    with fake_idp() as idp:
        idp.disc_drop = ("issuer",)  # RFC 8414 makes issuer REQUIRED
        with pytest.raises(serve.OidcConfigError):
            _gate_for(idp)


# ── HARDENING REGRESSION: azp / multi-audience id_token (finding #5) ───────────────────────────
def _callback_status(key, *, aud, azp=None, sub="user-1"):
    """Drive a full redirect→/callback flow with a crafted id_token; return (status, set-cookies)."""
    with fake_idp() as idp:
        serve.AUTH = _gate_for(idp, signing_key=key)
        with run_mc() as port:
            tx_cookie, state, nonce = _begin_login(port, key)
            claims = {"iss": idp.base, "aud": aud, "sub": sub,
                      "nonce": nonce, "exp": int(time.time()) + 300}
            if azp is not None:
                claims["azp"] = azp
            idp.id_token = make_jwt(**claims)
            status, hdrs, _ = http_req(
                port, "/callback?code=c&state=%s" % state,
                headers={"Cookie": serve._TX_COOKIE + "=" + tx_cookie})
    return status, set_cookies(hdrs)


def _has_session(cookies):
    return any(c.startswith(serve._SESSION_COOKIE + "=") for c in cookies)


def test_oidc_multi_audience_requires_azp():
    key = "unit-test-signing-key"
    cid = "mc-client"  # _gate_for's default client_id
    # single audience: no azp needed → accepted
    st, cookies = _callback_status(key, aud=cid)
    assert st == 302 and _has_session(cookies)
    # multi-audience, azp absent → rejected
    st, cookies = _callback_status(key, aud=[cid, "other-client"])
    assert st == 400 and not _has_session(cookies)
    # multi-audience, azp is some OTHER client → rejected
    st, cookies = _callback_status(key, aud=[cid, "other-client"], azp="other-client")
    assert st == 400 and not _has_session(cookies)
    # multi-audience, azp == our client_id → accepted
    st, cookies = _callback_status(key, aud=[cid, "other-client"], azp=cid)
    assert st == 302 and _has_session(cookies)
