"""The secured ASGI app: Streamable-HTTP MCP behind an audience-checking middleware (Epic J).

In plain English: this wraps the MCP server so that every agent request to ``/mcp`` must carry a
bearer token whose audience is *this* server, and publishes a small public document at
``/.well-known/oauth-protected-resource`` telling agents where to get such a token. The gateway
(Kong) already checks the same things, but we re-check the audience here ourselves so the security
does not rest on the gateway alone (defense-in-depth).

Formally (ADR-IC-010):
- §P2 — a public RFC 9728 Protected Resource Metadata document at
  ``/.well-known/oauth-protected-resource`` advertises the IAM as the authorization server.
- §P3 — every ``/mcp`` request's bearer token is decoded (signature already verified by Kong's jwt
  plugin upstream; we decode with ``verify_signature=False`` and re-check ``aud`` AUTHORITATIVELY at
  the app layer, because Kong CE's jwt plugin cannot check audience against a per-route value). A
  token whose ``aud`` does not equal ``BABELSTONE_MCP_SERVER_URI`` is rejected with ``401`` and a
  ``WWW-Authenticate`` header carrying ``resource_metadata`` pointing at the well-known document.
- §P5 — the route is Streamable HTTP (single ``/mcp`` endpoint, POST + GET).

The ``aud`` claim may be a STRING or a LIST of strings per RFC 7519 §4.1.3 — both are handled. On
success the gateway-attested ``X-Client-Id`` and ``X-OAuth-Scope`` headers are passed through to the
tools unchanged (the tools read them via ``auth.AuthContext``); the middleware never derives identity
from the token itself (Document 11 — identity comes from the gateway-attested ``sub``).

**Trust precondition (enforced).** The app trusts those gateway-attested headers because Kong is
the sole ingress (this service exposes no host port), Kong OVERWRITES ``X-Client-Id``/
``X-OAuth-Scope`` from the validated ``sub``/``scope`` — the same EdgeAuth trust model the
orchestrator uses — AND the Kong→MCP hop is now MUTUAL TLS. The uvicorn upstream requires a client
certificate (``ssl_cert_reqs=CERT_REQUIRED`` when ``MCP_TLS_CA_CERTS`` is set; see
``__main__.build_tls_kwargs``) and Kong presents one with ``tls_verify: true`` on the mcp-server
service (ADR-IC-006 §P5 Boundary 2 / ADR-IC-010 §P5). So a Kong-bypassing actor on the upstream
network — even with a forged token and a spoofed ``X-Client-Id`` — is rejected at the TLS handshake,
not merely shielded by network topology (completed bd ``babelstone-29ic``; the end-to-end gateway
runtime contract test is bd ``babelstone-5ot0`` / ``make mcp-contract-test``).
"""

from __future__ import annotations

import json
import os

import jwt
from starlette.applications import Starlette
from starlette.middleware.base import BaseHTTPMiddleware
from starlette.requests import Request
from starlette.responses import JSONResponse, Response
from starlette.types import ASGIApp

from .server import mcp
from .telemetry import instrument_asgi_app

# RFC 9728 well-known path. Public (unauthenticated) so clients can discover the authorization
# server before they have a token — the matching Kong route disables the jwt plugin on it.
WELL_KNOWN_PATH = "/.well-known/oauth-protected-resource"


def _mcp_server_uri() -> str:
    """This server's canonical URI — the value tokens must carry as ``aud`` (RFC 8707 / §P3)."""
    return os.environ.get("BABELSTONE_MCP_SERVER_URI", "http://localhost:8000/mcp")


def _iam_url() -> str:
    """The IAM authorization server URL advertised in the RFC 9728 metadata (§P2)."""
    return os.environ.get("BABELSTONE_IAM_URL", "https://iam.babelstone.example/")


def _well_known_url(request: Request) -> str:
    """Absolute URL of the Protected Resource Metadata document, for the WWW-Authenticate header."""
    return str(request.url.replace(path=WELL_KNOWN_PATH, query=""))


def _audience_claim(token: str) -> object:
    """Decode the bearer JWT payload and return its ``aud`` claim (or ``None``).

    Kong's jwt plugin already verified the SIGNATURE upstream (ADR-IC-006 §P7), so we decode with
    ``verify_signature=False`` purely to read the audience for the authoritative §P3 re-check. A
    malformed/undecodable token returns ``None`` => the caller treats it as an audience mismatch
    (fail-closed). ``aud`` may be a string or a list (RFC 7519 §4.1.3).
    """
    try:
        claims = jwt.decode(
            token,
            options={"verify_signature": False, "verify_aud": False, "verify_exp": False},
        )
    except jwt.PyJWTError:
        return None
    return claims.get("aud")


def _audience_matches(aud_claim: object, expected: str) -> bool:
    """True iff ``expected`` is the audience (string form) or appears in it (list form)."""
    if isinstance(aud_claim, str):
        return aud_claim == expected
    if isinstance(aud_claim, list):
        return expected in aud_claim
    return False


class AudienceMiddleware(BaseHTTPMiddleware):
    """Authoritative RFC 8707 audience re-check on every ``/mcp`` request (ADR-IC-010 §P3).

    The well-known metadata route (and any other ``/.well-known/*`` path) is left public; only the
    MCP transport path is guarded. On a missing/malformed token or an audience mismatch the request
    is rejected with ``401`` + ``WWW-Authenticate: Bearer ..., resource_metadata="<well-known>"`` and
    a body ``{"code": "AUDIENCE_MISMATCH", ...}``. No PII in the body or header (ADR-PC-004 §P2): a
    stable code + a generic message only.
    """

    async def dispatch(self, request: Request, call_next):  # type: ignore[override]
        path = request.url.path
        # The public discovery document — and any well-known path — is never audience-gated.
        if path.startswith("/.well-known/"):
            return await call_next(request)

        expected = _mcp_server_uri()
        auth = request.headers.get("authorization") or ""
        scheme, _, token = auth.partition(" ")
        token = token.strip()
        if scheme.lower() != "bearer" or not token:
            return self._reject(request)

        aud_claim = _audience_claim(token)
        if not _audience_matches(aud_claim, expected):
            return self._reject(request)

        # Audience re-confirmed. The gateway-attested X-Client-Id / X-OAuth-Scope headers flow
        # through unchanged for the tools to read (ADR-IC-010 §P3/§P4); we add nothing identity-
        # bearing of our own from the token.
        return await call_next(request)

    def _reject(self, request: Request) -> Response:
        resource_metadata = _well_known_url(request)
        www_authenticate = (
            f'Bearer error="invalid_token", '
            f'error_description="The access token audience does not match this MCP server", '
            f'resource_metadata="{resource_metadata}"'
        )
        return JSONResponse(
            {
                "code": "AUDIENCE_MISMATCH",
                "message": (
                    "The bearer token's audience does not match this MCP server's canonical URI "
                    "(RFC 8707 / ADR-IC-010 §P3)."
                ),
            },
            status_code=401,
            headers={"WWW-Authenticate": www_authenticate},
        )


@mcp.custom_route(WELL_KNOWN_PATH, methods=["GET"])
async def oauth_protected_resource(request: Request) -> Response:
    """RFC 9728 Protected Resource Metadata (ADR-IC-010 §P2) — public, unauthenticated.

    Advertises the IAM as the authorization server so an agent with no token can discover where to
    obtain one. Reachable unauthenticated: the Kong route disables the jwt plugin on this path, and
    ``AudienceMiddleware`` leaves ``/.well-known/*`` ungated.
    """
    body = {
        "resource": _mcp_server_uri(),
        "authorization_servers": [_iam_url()],
        "bearer_methods_supported": ["header"],
        "resource_signing_alg_values_supported": ["RS256"],
    }
    return Response(
        content=json.dumps(body),
        media_type="application/json",
        # Cache-friendly and CORS-open: discovery is public metadata.
        headers={"Cache-Control": "public, max-age=3600"},
    )


def build_app() -> ASGIApp:
    """The secured Streamable-HTTP ASGI app: the MCP transport wrapped in ``AudienceMiddleware``.

    ``mcp.streamable_http_app()`` yields the Starlette app carrying the ``/mcp`` route and the
    well-known route registered above via ``@mcp.custom_route``; we add the audience middleware in
    front of it (ADR-IC-010 §P3/§P5), then wrap the whole stack in the OpenTelemetry ASGI middleware
    (ADR-IC-007 Layer 1, bd babelstone-scd2.1) so every inbound MCP request becomes a SERVER span on
    the ``service.namespace=babelstone`` resource — the root of the MCP→engine distributed trace. The
    OTel wrap is OUTERMOST so the span also covers the audience check; it is a best-effort no-op when
    the OTel SDK is not installed (``telemetry.instrument_asgi_app``).
    """
    app: Starlette = mcp.streamable_http_app()
    app.add_middleware(AudienceMiddleware)
    return instrument_asgi_app(app)
