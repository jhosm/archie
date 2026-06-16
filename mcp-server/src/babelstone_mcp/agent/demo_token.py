"""The gateway stand-in: mint the demo credential the agent presents to the secured MCP edge.

In plain English: the real MCP server only trusts requests that arrive the way they would through the
production gateway (Kong) — a bearer token whose audience is this server, plus the gateway-attested
``X-Client-Id`` / ``X-OAuth-Scope`` headers that say who is calling and what they may do. In
production Kong sets those. For the demo there is no Kong in front of the agent, so the agent host
plays that role: it mints the same shape here. This is exactly what the Mission Control proxy already
does for the orchestrator (it injects ``X-Client-Id``); we do the equivalent for the MCP channel.

Formally (ADR-IC-010):
- §P3 — the MCP app re-checks the bearer's ``aud`` against ``BABELSTONE_MCP_SERVER_URI`` and rejects
  a mismatch with 401, BEFORE any tool code runs. Kong's jwt plugin verifies the SIGNATURE upstream;
  the app decodes with ``verify_signature=False`` and only asserts ``aud``. So for the demo the
  signing key is irrelevant to acceptance — what matters is that ``aud`` equals the server's URI.
- §P3/§P4 — identity and scope come from the gateway-attested ``X-Client-Id`` (the token ``sub``)
  and ``X-OAuth-Scope`` headers, NEVER from a tool argument. The deposit tools need
  ``deposits:read`` (get) and ``deposits:write`` (constitute/mature/pay).

The client id is an OPAQUE business reference (e.g. ``CLI-DEMO-0001``), never PII — matching the
demo's existing posture (the Mission Control proxy injects the same kind of opaque id).
"""

from __future__ import annotations

import time

import jwt

# The deposit tool scopes (ADR-IC-010 §P4 scope-per-tool). The demo agent carries both so Claude can
# read and write; a tighter demo could split them, but the investor flow constitutes and matures.
DEPOSITS_READ = "deposits:read"
DEPOSITS_WRITE = "deposits:write"
DEFAULT_SCOPES = (DEPOSITS_READ, DEPOSITS_WRITE)

# An opaque demo caller id — a business reference, never PII (mirrors the Mission Control proxy's
# DEMO_CLIENT_ID stand-in).
DEFAULT_CLIENT_ID = "CLI-DEMO-0001"

# A throwaway HS256 key. The MCP edge decodes with verify_signature=False (Kong verifies signatures
# upstream in production), so this value never gates acceptance — it only keeps PyJWT from complaining
# about an empty key. It is NOT a secret and grants nothing on its own.
_DEMO_SIGNING_KEY = "babelstone-demo-mcp-agent-not-a-secret"

CLIENT_ID_HEADER = "X-Client-Id"
OAUTH_SCOPE_HEADER = "X-OAuth-Scope"


def mint_demo_bearer(
    *,
    audience: str,
    client_id: str = DEFAULT_CLIENT_ID,
    scopes: tuple[str, ...] = DEFAULT_SCOPES,
    ttl_seconds: int = 3600,
    now: float | None = None,
) -> str:
    """Mint an audience-bound demo bearer JWT for the MCP edge (ADR-IC-010 §P3).

    The ``aud`` claim MUST equal the server's ``BABELSTONE_MCP_SERVER_URI`` or the AudienceMiddleware
    rejects it 401. ``sub`` carries the opaque client id and ``scope`` the space-delimited scopes —
    informational here (the gateway-attested headers are authoritative), included so the token mirrors
    a real one.
    """
    issued = time.time() if now is None else now
    claims = {
        "aud": audience,
        "sub": client_id,
        "scope": " ".join(scopes),
        "iat": int(issued),
        "exp": int(issued) + ttl_seconds,
    }
    return jwt.encode(claims, _DEMO_SIGNING_KEY, algorithm="HS256")


def demo_headers(
    *,
    audience: str,
    client_id: str = DEFAULT_CLIENT_ID,
    scopes: tuple[str, ...] = DEFAULT_SCOPES,
    ttl_seconds: int = 3600,
    now: float | None = None,
) -> dict[str, str]:
    """Build the full header set the agent presents to ``/mcp`` — the Kong stand-in.

    Returns the ``Authorization: Bearer …`` (audience-bound, §P3) plus the gateway-attested
    ``X-Client-Id`` (the §P3 caller identity) and ``X-OAuth-Scope`` (the §P4 granted scopes). These
    are exactly the headers the MCP server reads via ``AuthContext.from_headers`` and
    ``check_tool_scope``.
    """
    bearer = mint_demo_bearer(
        audience=audience,
        client_id=client_id,
        scopes=scopes,
        ttl_seconds=ttl_seconds,
        now=now,
    )
    return {
        "Authorization": f"Bearer {bearer}",
        CLIENT_ID_HEADER: client_id,
        OAUTH_SCOPE_HEADER: " ".join(scopes),
    }
