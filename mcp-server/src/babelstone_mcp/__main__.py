"""Entrypoint: run the dev MCP server over Streamable HTTP (ADR-IC-010 §P5).

``BABELSTONE_ENGINE_URL`` points at the engine command/query host (Babelstone.Engine.Api);
defaults to http://localhost:8080. Auth is deferred (the secured edge is Epic J).
"""

from __future__ import annotations

from .server import mcp


def main() -> None:
    mcp.run(transport="streamable-http")


if __name__ == "__main__":
    main()
