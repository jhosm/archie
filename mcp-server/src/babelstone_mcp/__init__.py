"""Babelstone dev MCP server (E.5, ADR-IC-010).

Exposes ``constitute_deposit`` / ``mature_deposit`` (writes) and ``get_deposit`` (on-demand read)
tools over the official Python MCP SDK (Streamable HTTP), translating agent calls into HTTP requests
to the engine command/query boundary (``Babelstone.Engine.Api``, ADR-PC-021 §D5).

This is the auth-deferred *dev* slice: no OAuth/Kong (that is the secured edge, Epic J).
"""

__all__ = ["__version__"]

__version__ = "0.1.0"
