"""HTTP client for the engine command/query boundary (Babelstone.Engine.Api, ADR-PC-021 §D5).

A thin async wrapper over the two surfaces the MCP server maps: constitute (POST) and read a
deposit position (GET). Money crosses the wire as integer cents (ADR-PC-010 §P1), snake_case.
The client is fail-loud: a non-2xx engine response raises (``raise_for_status``) rather than
returning a partial/empty result — the MCP layer surfaces that to the agent.
"""

from __future__ import annotations

from typing import Any

import httpx


class EngineClient:
    """Calls the engine's deposits HTTP API. Inject an ``httpx.AsyncClient`` in tests."""

    def __init__(self, base_url: str, client: httpx.AsyncClient | None = None) -> None:
        self._base_url = base_url.rstrip("/")
        self._client = client or httpx.AsyncClient(timeout=30.0)

    async def constitute(self, request: dict[str, Any]) -> dict[str, Any]:
        """POST /v1/deposits — returns {deposit_id, status}. Raises on a non-2xx engine response."""
        response = await self._client.post(f"{self._base_url}/v1/deposits", json=request)
        response.raise_for_status()
        return response.json()

    async def deposit_position(self, deposit_id: str) -> dict[str, Any]:
        """GET /v1/deposits/{id} — the folded position. Raises on 404/other non-2xx."""
        response = await self._client.get(f"{self._base_url}/v1/deposits/{deposit_id}")
        response.raise_for_status()
        return response.json()

    async def aclose(self) -> None:
        await self._client.aclose()
