"""Tests for the secured MCP edge (Epic J, babelstone-e50n) — ADR-IC-010 §P2/§P3/§P4.

What these prove:
- §P2 — ``/.well-known/oauth-protected-resource`` serves the RFC 9728 metadata, unauthenticated.
- §P3 — a ``/mcp`` request whose bearer token's ``aud`` is not this server's canonical URI is
  rejected ``401`` with ``code: AUDIENCE_MISMATCH`` and a ``WWW-Authenticate`` header carrying
  ``resource_metadata``; a correct ``aud`` passes the middleware. ``aud`` string AND list both work.
- §P4 — scope-per-tool: ``deposits:read`` calling ``constitute_deposit`` raises ``McpError``; the
  matching scope passes. A missing gateway-attested ``X-Client-Id`` is rejected.
- §A8 — the RFC 8705 mTLS-bound sender-constraint (``cnf.x5t#S256``) Kong validates and attests as
  ``X-SCA-Cnf-X5t`` surfaces on the ``AuthContext`` (``sender_bound`` / ``is_sender_constrained``);
  its absence is an unbound plain Bearer (not a fail-closed rejection).

The middleware and well-known route are exercised over a real ASGI round-trip via ``httpx``'s
``ASGITransport`` (no live socket); the scope checks are exercised on the ``auth`` module directly.
"""

from __future__ import annotations

import jwt
import pytest
from httpx import ASGITransport, AsyncClient
from mcp.shared.exceptions import McpError
from starlette.requests import Request
from starlette.responses import PlainTextResponse

from babelstone_mcp import app as app_module
from babelstone_mcp.auth import (
    DEPOSITS_READ,
    DEPOSITS_WRITE,
    RESOURCE_SCOPES,
    TOOL_SCOPES,
    TRANSFERS_WRITE,
    AuthContext,
    audience_binds_resource,
    check_tool_scope,
    mcp_resource_indicator,
)

MCP_URI = "http://localhost:8000/mcp"
IAM_URL = "https://iam.babelstone.example/"


@pytest.fixture(autouse=True)
def _env(monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.setenv("BABELSTONE_MCP_SERVER_URI", MCP_URI)
    monkeypatch.setenv("BABELSTONE_IAM_URL", IAM_URL)


def _token(aud: object) -> str:
    # Kong verifies the SIGNATURE upstream; the app decodes with verify_signature=False and only
    # re-checks `aud`. The signing key here is irrelevant to what the app asserts (a 32-byte HS256
    # key, just to keep PyJWT quiet about key length).
    return jwt.encode(
        {"aud": aud, "sub": "CLI-2026-007842"}, "x" * 32, algorithm="HS256"
    )


def _client() -> AsyncClient:
    transport = ASGITransport(app=app_module.build_app())
    return AsyncClient(transport=transport, base_url="http://localhost:8000")


def _mcp_request(authorization: str | None) -> Request:
    """A minimal Starlette ``Request`` for ``POST /mcp`` carrying (or omitting) an Authorization."""
    headers: list[tuple[bytes, bytes]] = []
    if authorization is not None:
        headers.append((b"authorization", authorization.encode()))
    scope = {
        "type": "http",
        "method": "POST",
        "path": "/mcp",
        "scheme": "http",
        "server": ("localhost", 8000),
        "query_string": b"",
        "headers": headers,
    }
    return Request(scope)


async def _passes_middleware(authorization: str | None) -> bool:
    """True iff ``AudienceMiddleware`` lets a ``/mcp`` request through (does NOT 401 it).

    Drives the middleware in isolation with a sentinel ``call_next``, so the assertion is purely
    "did the audience gate admit this request" — independent of the downstream transport.
    """
    mw = app_module.AudienceMiddleware(app=None)  # app is unused; dispatch is called directly
    passed = {"v": False}

    async def call_next(_request: Request):
        passed["v"] = True
        return PlainTextResponse("ok")

    await mw.dispatch(_mcp_request(authorization), call_next)
    return passed["v"]


# ── §P2 — RFC 9728 Protected Resource Metadata, public ─────────────────────────────


async def test_well_known_returns_rfc9728_metadata_unauthenticated() -> None:
    async with _client() as client:
        # NO Authorization header — discovery must be reachable before the agent has a token.
        resp = await client.get("/.well-known/oauth-protected-resource")

    assert resp.status_code == 200
    body = resp.json()
    assert body["resource"] == MCP_URI
    assert body["authorization_servers"] == [IAM_URL]
    assert body["bearer_methods_supported"] == ["header"]
    assert body["resource_signing_alg_values_supported"] == ["RS256"]


# ── §P3 — audience re-check at the app layer ───────────────────────────────────────


async def test_MCP_WRONG_RESOURCE_TOKEN_REJECTED_wrong_aud_is_rejected_401_audience_mismatch() -> None:
    # Realises catalogue Test ID MCP_WRONG_RESOURCE_TOKEN_REJECTED (ADR-IC-010 §P3): a token whose
    # `aud` is not this server's canonical URI gets 401 + code AUDIENCE_MISMATCH before any app code
    # runs. The Test ID is embedded in the method name so the ADR-PC-020 §P6 coverage checker
    # (.github/scripts/spec-coverage-check.sh) resolves the Live row to this test by literal grep.
    async with _client() as client:
        resp = await client.post(
            "/mcp",
            headers={"Authorization": f"Bearer {_token('https://some-other-resource.example/')}"},
            json={"jsonrpc": "2.0", "id": 1, "method": "tools/list"},
        )

    assert resp.status_code == 401
    assert resp.json()["code"] == "AUDIENCE_MISMATCH"
    www = resp.headers["WWW-Authenticate"]
    # The WWW-Authenticate header points the client at the Protected Resource Metadata (§P3).
    assert "resource_metadata=" in www
    assert "/.well-known/oauth-protected-resource" in www
    # No PII leaks in the refusal (ADR-PC-004 §P2) — a stable code + a generic message only.
    assert "CLI-2026-007842" not in resp.text


async def test_missing_bearer_token_is_rejected_401() -> None:
    async with _client() as client:
        resp = await client.post(
            "/mcp",
            json={"jsonrpc": "2.0", "id": 1, "method": "tools/list"},
        )
    assert resp.status_code == 401
    assert resp.json()["code"] == "AUDIENCE_MISMATCH"


async def test_malformed_token_is_rejected_401() -> None:
    async with _client() as client:
        resp = await client.post(
            "/mcp",
            headers={"Authorization": "Bearer not-a-jwt"},
            json={"jsonrpc": "2.0", "id": 1, "method": "tools/list"},
        )
    assert resp.status_code == 401
    assert resp.json()["code"] == "AUDIENCE_MISMATCH"


async def test_correct_aud_passes_the_audience_middleware() -> None:
    # A token whose `aud` matches is admitted by the middleware (it does NOT 401).
    assert await _passes_middleware(f"Bearer {_token(MCP_URI)}") is True


async def test_aud_as_list_is_accepted_when_uri_present() -> None:
    # RFC 7519 §4.1.3 — `aud` may be a JSON array; a match on any element passes.
    assert await _passes_middleware(f"Bearer {_token(['https://other.example/', MCP_URI])}") is True


async def test_aud_as_list_without_uri_is_rejected() -> None:
    async with _client() as client:
        resp = await client.post(
            "/mcp",
            headers={"Authorization": f"Bearer {_token(['https://a.example/', 'https://b.example/'])}"},
            json={"jsonrpc": "2.0", "id": 1, "method": "tools/list"},
        )
    assert resp.status_code == 401
    assert resp.json()["code"] == "AUDIENCE_MISMATCH"


# ── §P4 — scope-per-tool + gateway-attested identity ───────────────────────────────


def test_insufficient_scope_read_calling_write_tool_raises() -> None:
    auth = AuthContext(client_id="CLI-7", scopes=frozenset({"deposits:read"}))
    with pytest.raises(McpError) as exc:
        check_tool_scope(auth, "constitute_deposit")
    assert "Insufficient scope" in exc.value.error.message


def test_correct_scope_passes() -> None:
    read = AuthContext(client_id="CLI-7", scopes=frozenset({"deposits:read"}))
    write = AuthContext(client_id="CLI-7", scopes=frozenset({"deposits:write"}))
    # No raise == pass.
    check_tool_scope(read, "get_deposit")
    check_tool_scope(write, "constitute_deposit")
    check_tool_scope(write, "mature_deposit")
    check_tool_scope(write, "pay_interest")


def test_write_scope_cannot_be_used_to_read_only_if_mapping_requires_read() -> None:
    # A write-only token cannot reach the read tool (one tool -> exactly one scope; no god scope).
    write = AuthContext(client_id="CLI-7", scopes=frozenset({"deposits:write"}))
    with pytest.raises(McpError):
        check_tool_scope(write, "get_deposit")


def test_missing_x_client_id_is_rejected() -> None:
    # The gateway attests identity via X-Client-Id (OAuth sub); absent it, the request is refused —
    # identity is NEVER taken from a tool argument (Document 11).
    from starlette.datastructures import Headers

    with pytest.raises(McpError) as exc:
        AuthContext.from_headers(Headers({"X-OAuth-Scope": "deposits:read"}))
    assert "X-Client-Id" in exc.value.error.message


def test_client_id_is_read_from_gateway_header() -> None:
    from starlette.datastructures import Headers

    auth = AuthContext.from_headers(
        Headers({"X-Client-Id": "CLI-2026-007842", "X-OAuth-Scope": "deposits:read deposits:write"})
    )
    assert auth.client_id == "CLI-2026-007842"
    assert auth.scopes == frozenset({"deposits:read", "deposits:write"})


# ── §A8 — RFC 8705 mTLS-bound sender constraint attestation (bd babelstone-26rb) ───


def test_sender_binding_thumbprint_is_read_from_gateway_header() -> None:
    # A sender-constrained step-up token's cnf.x5t#S256, validated by Kong against the presented
    # client cert and attested as X-SCA-Cnf-X5t (§A8), surfaces on the AuthContext for the chain.
    from starlette.datastructures import Headers

    auth = AuthContext.from_headers(
        Headers(
            {
                "X-Client-Id": "CLI-2026-007842",
                "X-OAuth-Scope": "deposits:write",
                "X-SCA-Cnf-X5t": "oOwf84uA98xfl7q9U2t6ZEUtJF3FkNKxhWCXGhsrtP4",
            }
        )
    )
    assert auth.sender_bound == "oOwf84uA98xfl7q9U2t6ZEUtJF3FkNKxhWCXGhsrtP4"
    assert auth.is_sender_constrained is True


def test_absent_sender_binding_is_unbound_plain_bearer() -> None:
    # No X-SCA-Cnf-X5t (a plain, POC-legacy Bearer) => an empty, non-sender-constrained binding.
    # The header is NOT required: §A8 sender-constraining is production hardening, not a fail-closed
    # identity check, so its absence does not reject the request (only X-Client-Id is fail-closed).
    from starlette.datastructures import Headers

    auth = AuthContext.from_headers(
        Headers({"X-Client-Id": "CLI-2026-007842", "X-OAuth-Scope": "deposits:write"})
    )
    assert auth.sender_bound == ""
    assert auth.is_sender_constrained is False


def test_unknown_tool_has_no_scope_grant() -> None:
    auth = AuthContext(client_id="CLI-7", scopes=frozenset({"deposits:read", "deposits:write"}))
    with pytest.raises(McpError):
        check_tool_scope(auth, "transfer_funds")


# ── RFC 8707 resource-server surface (ADR-IC-021 step 4 / C1+C5, bd babelstone-zla1.10.4) ──────────


def test_resource_indicator_returns_the_registered_uri() -> None:
    # The canonical URI is the RFC 8707 resource indicator AND the Logto API-resource identifier; the
    # app advertises it (RFC 9728 `resource`) and tokens must carry it as `aud`. Returned verbatim
    # from the env (no trailing-slash mutation — the MCP-Auth SDK is slash-significant).
    assert mcp_resource_indicator() == MCP_URI


def test_resource_indicator_is_what_the_well_known_advertises() -> None:
    # The single-sourcing guard: the RFC 9728 metadata `resource` IS the resource indicator, so a
    # token's `aud` (checked against the same value) can never drift from what discovery advertises.
    assert app_module._mcp_server_uri() == mcp_resource_indicator()


def test_resource_scopes_are_the_three_adr_ic_021_scopes() -> None:
    # ADR-IC-021 C5: the Logto API resource declares exactly deposits:read / deposits:write /
    # transfers:write — narrow, per-tool, no god scope. This is the registered scope catalogue.
    assert RESOURCE_SCOPES == frozenset({DEPOSITS_READ, DEPOSITS_WRITE, TRANSFERS_WRITE})
    assert TRANSFERS_WRITE == "transfers:write"


def test_transfers_write_is_declared_but_maps_to_no_current_tool() -> None:
    # transfers:write is reserved in the resource catalogue but has no tool yet (no transfer tool on
    # the deposit surface), so it is deliberately absent from the ENFORCED per-tool projection.
    assert TRANSFERS_WRITE in RESOURCE_SCOPES
    assert TRANSFERS_WRITE not in TOOL_SCOPES.values()
    # Every scope that IS enforced per-tool must be one the resource is registered with (no tool can
    # require a scope Logto would never mint for this resource).
    assert set(TOOL_SCOPES.values()).issubset(RESOURCE_SCOPES)


def test_audience_binds_resource_string_form() -> None:
    assert audience_binds_resource(MCP_URI, MCP_URI) is True
    assert audience_binds_resource("https://other-resource.example/", MCP_URI) is False


def test_audience_binds_resource_list_form() -> None:
    # RFC 7519 §4.1.3 — `aud` may be an array; a match on any element binds.
    assert audience_binds_resource(["https://other.example/", MCP_URI], MCP_URI) is True
    assert audience_binds_resource(["https://a.example/", "https://b.example/"], MCP_URI) is False


def test_audience_binds_resource_fail_closed_on_absent_or_odd_claim() -> None:
    # A missing `aud` (None) or a non-string/non-list value is NOT bound — fail-closed, so a
    # cross-resource or malformed token is rejected (RFC 8707 / ADR-IC-021 C1).
    assert audience_binds_resource(None, MCP_URI) is False
    assert audience_binds_resource(12345, MCP_URI) is False
