"""Entrypoint: run the secured MCP server over Streamable HTTP (ADR-IC-010 §P3/§P5).

In plain English: this starts the MCP server with its audience-checking front door in place. Agents
reach it through Kong; this process serves the ``/mcp`` transport (wrapped in the audience middleware)
plus the public ``/.well-known/oauth-protected-resource`` discovery document. When the TLS env vars
below are set, the server speaks HTTPS and REQUIRES a client certificate — that is the upstream half
of the Kong→MCP mutual-TLS boundary (ADR-IC-006 §P5 / ADR-IC-010 §P5, bd babelstone-29ic). Without
them it serves plain HTTP, so a developer can still hit it directly (demo-mcp.sh).

Env:
- ``BABELSTONE_ENGINE_URL`` — the engine command/query host (default http://localhost:8080).
- ``BABELSTONE_ORCHESTRATOR_URL`` — the saga orchestrator edge host the ``get_process_status`` polling
  tool reads (default http://localhost:8090; Document 11 Pattern 2, bd babelstone-vjoi).
- ``BABELSTONE_MCP_SERVER_URI`` — this server's canonical URI; tokens must carry it as ``aud`` (§P3).
- ``BABELSTONE_IAM_URL`` — the authorization server advertised in the RFC 9728 metadata (§P2).
- ``MCP_BIND_HOST`` / ``MCP_BIND_PORT`` — the listen address (default ``0.0.0.0`` : ``8080``). In a
  container we MUST bind all interfaces so Kong can reach the upstream; FastMCP's own
  ``FASTMCP_*`` settings are bypassed (the SDK constructs ``Settings`` with explicit kwargs, so its
  env reading is unreliable for host/port), so we read a dedicated pair here.
- ``MCP_TLS_CERTFILE`` / ``MCP_TLS_KEYFILE`` — the server TLS cert + key. Present BOTH ⇒ uvicorn
  serves HTTPS (Kong dials ``https://mcp-server:8080``). Absent ⇒ plain HTTP (the dev-direct path).
- ``MCP_TLS_CA_CERTS`` — the CA that signed Kong's client cert. When set, uvicorn REQUIRES and
  verifies a client cert (mutual TLS): a Kong-bypassing actor with no client cert is rejected at the
  handshake (the 29ic fail-closed lock).
- ``MCP_TLS_CERT_REQS`` — override the client-cert mode as an ``ssl.VerifyMode`` int
  (0=NONE, 1=OPTIONAL, 2=REQUIRED). Defaults to ``2`` (REQUIRED) whenever ``MCP_TLS_CA_CERTS`` is set.

We serve the *wrapped* app (``app.build_app()``) via uvicorn rather than ``mcp.run(...)`` so the
``AudienceMiddleware`` is in front of the transport. The Starlette app's session-manager lifespan is
preserved because ``add_middleware`` mutates the same app in place.
"""

from __future__ import annotations

import os
import ssl
from typing import Any, Mapping

import uvicorn

from .app import build_app
from .telemetry import configure_tracing, instrument_httpx


def build_tls_kwargs(env: Mapping[str, str]) -> dict[str, Any]:
    """Translate the ``MCP_TLS_*`` env into uvicorn SSL kwargs (ADR-IC-006 §P5 / ADR-IC-010 §P5).

    Returns ``{}`` when no server cert/key is configured, so uvicorn serves plain HTTP (the
    dev-direct path — demo-mcp.sh). When BOTH ``MCP_TLS_CERTFILE`` and ``MCP_TLS_KEYFILE`` are
    present, uvicorn serves HTTPS. If ``MCP_TLS_CA_CERTS`` is also set, uvicorn verifies the peer's
    client cert against it and (by default) REQUIRES one — that is the upstream half of the
    Kong→MCP mutual TLS that closes the trust gap (a Kong-bypassing actor with no client cert is
    rejected at the handshake). ``MCP_TLS_CERT_REQS`` overrides the client-cert mode as an
    ``ssl.VerifyMode`` int (0/1/2); it has no effect without a CA (you cannot verify a client cert
    with no CA to verify it against).
    """
    certfile = env.get("MCP_TLS_CERTFILE")
    keyfile = env.get("MCP_TLS_KEYFILE")
    if not certfile or not keyfile:
        return {}
    kwargs: dict[str, Any] = {
        "ssl_certfile": certfile,
        "ssl_keyfile": keyfile,
    }
    ca_certs = env.get("MCP_TLS_CA_CERTS")
    if ca_certs:
        kwargs["ssl_ca_certs"] = ca_certs
        # Default to CERT_REQUIRED (mutual TLS) once a CA is configured — fail closed.
        cert_reqs_str = env.get("MCP_TLS_CERT_REQS", str(ssl.CERT_REQUIRED.value))
        kwargs["ssl_cert_reqs"] = _parse_cert_reqs(cert_reqs_str)
    return kwargs


def _parse_cert_reqs(raw: str) -> ssl.VerifyMode:
    """Parse ``MCP_TLS_CERT_REQS`` into an ``ssl.VerifyMode``, failing with a clear message.

    A bare ``ssl.VerifyMode(int(raw))`` raises a cryptic ``ValueError`` for either a non-numeric
    string (``int("foo")``) or an out-of-range int (``ssl.VerifyMode(99)`` → "99 is not a valid
    VerifyMode"). Neither tells an operator what the variable should be. This guard names the
    offending value AND the three legal settings so the misconfiguration is fixable from the log
    line alone (fail loud, ADR-IC-009).
    """
    legal = "0 (CERT_NONE), 1 (CERT_OPTIONAL), or 2 (CERT_REQUIRED)"
    try:
        value = int(raw)
    except ValueError:
        raise ValueError(
            f"MCP_TLS_CERT_REQS must be an integer {legal}; got {raw!r}."
        ) from None
    try:
        return ssl.VerifyMode(value)
    except ValueError:
        raise ValueError(
            f"MCP_TLS_CERT_REQS={value} is not a valid ssl.VerifyMode; expected {legal}."
        ) from None


def main() -> None:
    # Stand up tracing FIRST (ADR-IC-007 Layer 1, bd babelstone-scd2.1): register the
    # service.namespace=babelstone TracerProvider + OTLP/HTTP exporter to the Collector (§P1), and
    # instrument httpx so every engine/orchestrator call the tools make is a CLIENT span that
    # propagates the W3C traceparent. The ASGI SERVER span is added when build_app() wraps the app
    # below. Both are best-effort no-ops if the OTel SDK is absent, but configure_tracing fails fast
    # on an unresolved deployment.environment (§P1) — a deliberate refusal to mis-attribute traces.
    configure_tracing()
    instrument_httpx()

    tls = build_tls_kwargs(os.environ)
    uvicorn.run(
        build_app(),
        host=os.environ.get("MCP_BIND_HOST", "0.0.0.0"),
        port=int(os.environ.get("MCP_BIND_PORT", "8080")),
        log_level=os.environ.get("MCP_LOG_LEVEL", "info").lower(),
        **tls,
    )


if __name__ == "__main__":
    main()
