"""Entrypoint: run the secured MCP server over Streamable HTTP (ADR-IC-010 §P3/§P5).

In plain English: this starts the MCP server with its audience-checking front door in place. Agents
reach it through Kong; this process serves the ``/mcp`` transport (wrapped in the audience middleware)
plus the public ``/.well-known/oauth-protected-resource`` discovery document.

Env:
- ``BABELSTONE_ENGINE_URL`` — the engine command/query host (default http://localhost:8080).
- ``BABELSTONE_MCP_SERVER_URI`` — this server's canonical URI; tokens must carry it as ``aud`` (§P3).
- ``BABELSTONE_IAM_URL`` — the authorization server advertised in the RFC 9728 metadata (§P2).
- ``MCP_BIND_HOST`` / ``MCP_BIND_PORT`` — the listen address (default ``0.0.0.0`` : ``8080``). In a
  container we MUST bind all interfaces so Kong can reach the upstream; FastMCP's own
  ``FASTMCP_*`` settings are bypassed (the SDK constructs ``Settings`` with explicit kwargs, so its
  env reading is unreliable for host/port), so we read a dedicated pair here.

We serve the *wrapped* app (``app.build_app()``) via uvicorn rather than ``mcp.run(...)`` so the
``AudienceMiddleware`` is in front of the transport. The Starlette app's session-manager lifespan is
preserved because ``add_middleware`` mutates the same app in place.
"""

from __future__ import annotations

import os

import uvicorn

from .app import build_app


def main() -> None:
    uvicorn.run(
        build_app(),
        host=os.environ.get("MCP_BIND_HOST", "0.0.0.0"),
        port=int(os.environ.get("MCP_BIND_PORT", "8080")),
        log_level=os.environ.get("MCP_LOG_LEVEL", "info").lower(),
    )


if __name__ == "__main__":
    main()
