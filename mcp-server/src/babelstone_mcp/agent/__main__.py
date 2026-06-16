"""Entrypoint: run the real-Claude agent host over HTTP (bd babelstone-f0ic.6.2).

In plain English: this starts the small web server that Mission Control talks to — ``POST
/agent/stream`` streams a Claude run as Server-Sent Events. Run it alongside the engine, the MCP
server, and the ``serve.py`` proxy for the demo's real-AI mode.

    python -m babelstone_mcp.agent          # serves on 127.0.0.1:8091 by default
    # (needs the agent extra + ANTHROPIC_API_KEY: pip install 'babelstone-mcp[agent]')

Env:
- ``AGENT_BIND_HOST`` / ``AGENT_BIND_PORT`` — listen address (default ``127.0.0.1`` : ``8091``).
  Defaults to localhost so the key-holding host is not exposed on the LAN for a laptop demo; set
  ``AGENT_BIND_HOST=0.0.0.0`` for a containerised deployment behind a gateway.
- ``ANTHROPIC_API_KEY`` — required at request time, server-side only (ADR-IC-014).
- ``BABELSTONE_AGENT_MCP_URL`` / ``BABELSTONE_MCP_SERVER_URI`` / ``BABELSTONE_AGENT_CLIENT_ID`` —
  where the agent connects to the MCP server and how it identifies itself (see ``host.AgentConfig``).
"""

from __future__ import annotations

import os

from .web import build_app


def main() -> None:  # pragma: no cover - uvicorn entrypoint, exercised by the f0ic.6.4 smoke
    import uvicorn

    # Bind localhost by default — the agent host holds the Anthropic key, so it should not be on the
    # LAN for a laptop demo (override AGENT_BIND_HOST=0.0.0.0 in a container behind a gateway).
    uvicorn.run(
        build_app(),
        host=os.environ.get("AGENT_BIND_HOST", "127.0.0.1"),
        port=int(os.environ.get("AGENT_BIND_PORT", "8091")),
        log_level=os.environ.get("AGENT_LOG_LEVEL", "info").lower(),
    )


if __name__ == "__main__":  # pragma: no cover
    main()
