"""HTTP client for the engine command/query boundary (Babelstone.Engine.Api, ADR-PC-021 §D5).

A thin async wrapper over the three surfaces the MCP server maps: constitute (POST), read a
deposit position (GET), and mature (POST). Money crosses the wire as integer cents (ADR-PC-010 §P1),
snake_case.
The client is fail-loud: a non-2xx engine response raises (``raise_for_status``) rather than
returning a partial/empty result — the MCP layer surfaces that to the agent.
"""

from __future__ import annotations

import uuid
from typing import Any

import httpx


class EngineClient:
    """Calls the engine's deposits HTTP API. Inject an ``httpx.AsyncClient`` in tests."""

    def __init__(self, base_url: str, client: httpx.AsyncClient | None = None) -> None:
        self._base_url = base_url.rstrip("/")
        self._client = client or httpx.AsyncClient(timeout=30.0)

    async def constitute(self, request: dict[str, Any]) -> dict[str, Any]:
        """POST /v1/deposits — returns {deposit_id, status, commit_sequence}. Raises on a non-2xx
        engine response. ``commit_sequence`` is the read-your-writes token (ADR-IC-005 §P3): pass it
        back as ``min_sequence`` on the follow-up read to see the just-written deposit.

        The engine MANDATES a UUID ``Idempotency-Key`` header (ADR-PC-029 slot 1) and 400s without it.
        On the saga channel that key is the ``saga_outbox`` row id; on this agent channel there is no
        such row (the agent is not the saga), so the client mints a fresh per-call UUID. Each tool
        invocation is its own command, so a per-call key is the correct contract here — the MCP server
        is a co-consumer of the engine command surface (ADR-IC-010 / ADR-PC-029 slot 6).
        """
        response = await self._client.post(
            f"{self._base_url}/v1/deposits",
            json=request,
            headers={"Idempotency-Key": str(uuid.uuid4())},
        )
        response.raise_for_status()
        return response.json()

    async def deposit_position(
        self, deposit_id: str, min_sequence: int | None = None
    ) -> dict[str, Any]:
        """GET /v1/deposits/{id} — the ONE canonical deposit resource (ADR-IC-005). Served from the
        denormalized read model by default; when ``min_sequence`` is given (a commit_sequence token),
        sends ``If-Min-Sequence`` so the engine folds the stream for read-your-writes if the projector
        is still behind. Raises on 404/other non-2xx.
        """
        headers = {"If-Min-Sequence": str(min_sequence)} if min_sequence is not None else None
        response = await self._client.get(
            f"{self._base_url}/v1/deposits/{deposit_id}", headers=headers
        )
        response.raise_for_status()
        return response.json()

    async def mature(self, deposit_id: str) -> dict[str, Any]:
        """POST /v1/deposits/{id}/maturity — settles the deposit, returns the matured position.

        Same position shape as ``deposit_position`` with ``lifecycle`` = ``Matured``. Raises on a
        non-2xx engine response (e.g. 422 if the deposit cannot mature).
        """
        response = await self._client.post(
            f"{self._base_url}/v1/deposits/{deposit_id}/maturity", json={}
        )
        response.raise_for_status()
        return response.json()

    async def pay_interest(self, deposit_id: str) -> dict[str, Any]:
        """POST /v1/deposits/{id}/interest — pays one PERIODIC coupon, returns the updated position.

        Same position shape as ``deposit_position`` with the coupon's gross/withholding/net folded
        in and ``coupons_paid`` incremented. The coupon window is derived by the engine from the
        deposit's schedule — not supplied here. Raises on a non-2xx engine response (e.g. 422 if the
        deposit is not Active, not PERIODIC, or has no intermediate coupon left).
        """
        response = await self._client.post(
            f"{self._base_url}/v1/deposits/{deposit_id}/interest", json={}
        )
        response.raise_for_status()
        return response.json()

    async def aclose(self) -> None:
        await self._client.aclose()
