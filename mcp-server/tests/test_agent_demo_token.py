"""Unit tests for the gateway-stand-in credential (bd babelstone-f0ic.6.1) — ADR-IC-010 §P3/§P4.

These prove the demo credential the agent presents is exactly what the REAL secured MCP edge accepts:
- the minted bearer's ``aud`` satisfies the app's authoritative audience re-check (§P3);
- the headers carry the gateway-attested ``X-Client-Id`` / ``X-OAuth-Scope`` the tools read;
- those scopes pass ``check_tool_scope`` for the deposit tools (§P4).

So the test ties the Kong stand-in to the genuine edge checks, not a side door.
"""

from __future__ import annotations

import jwt

from babelstone_mcp.agent.demo_token import (
    DEFAULT_CLIENT_ID,
    DEPOSITS_READ,
    DEPOSITS_WRITE,
    demo_headers,
    mint_demo_bearer,
)
from babelstone_mcp.app import _audience_claim, _audience_matches
from babelstone_mcp.auth import AuthContext, check_tool_scope

MCP_URI = "http://localhost:8000/mcp"


def _decode(token: str) -> dict:
    return jwt.decode(token, options={"verify_signature": False, "verify_aud": False})


def test_bearer_carries_audience_subject_and_scopes() -> None:
    token = mint_demo_bearer(audience=MCP_URI, now=1_000_000.0)
    claims = _decode(token)
    assert claims["aud"] == MCP_URI
    assert claims["sub"] == DEFAULT_CLIENT_ID
    assert set(claims["scope"].split()) == {DEPOSITS_READ, DEPOSITS_WRITE}
    assert claims["iat"] == 1_000_000
    assert claims["exp"] == 1_000_000 + 3600


def test_minted_bearer_satisfies_the_real_audience_recheck() -> None:
    # The app's §P3 AudienceMiddleware logic must accept our minted token for the server's URI.
    token = mint_demo_bearer(audience=MCP_URI)
    assert _audience_matches(_audience_claim(token), MCP_URI) is True
    # And reject it for a different server URI (the audience binding actually binds).
    assert _audience_matches(_audience_claim(token), "http://other/mcp") is False


def test_headers_are_the_kong_stand_in() -> None:
    headers = demo_headers(audience=MCP_URI)
    assert headers["Authorization"].startswith("Bearer ")
    assert headers["X-Client-Id"] == DEFAULT_CLIENT_ID
    assert set(headers["X-OAuth-Scope"].split()) == {DEPOSITS_READ, DEPOSITS_WRITE}
    # The bearer embedded in the header is audience-bound to the same server.
    bearer = headers["Authorization"].removeprefix("Bearer ")
    assert _decode(bearer)["aud"] == MCP_URI


def test_attested_headers_pass_the_real_scope_checks() -> None:
    # AuthContext + check_tool_scope are the genuine §P4 edge — the demo headers must pass them.
    headers = demo_headers(audience=MCP_URI)
    auth = AuthContext.from_headers(headers)
    assert auth.client_id == DEFAULT_CLIENT_ID
    # No raise == authorized for both the write tools and the read tool.
    check_tool_scope(auth, "constitute_deposit")
    check_tool_scope(auth, "mature_deposit")
    check_tool_scope(auth, "get_deposit")
