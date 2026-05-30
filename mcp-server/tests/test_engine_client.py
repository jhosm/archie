"""Contract tests for the engine HTTP client — mocked transport, no live engine."""

from __future__ import annotations

import json

import httpx
import pytest

from babelstone_mcp.engine_client import EngineClient


def _client(handler) -> EngineClient:
    return EngineClient("http://engine", httpx.AsyncClient(transport=httpx.MockTransport(handler)))


async def test_constitute_posts_snake_case_cents_and_returns_result() -> None:
    captured: dict = {}

    def handler(request: httpx.Request) -> httpx.Response:
        captured["method"] = request.method
        captured["url"] = str(request.url)
        captured["body"] = json.loads(request.content)
        return httpx.Response(201, json={"deposit_id": "d-1", "status": "ACTIVE"})

    result = await _client(handler).constitute(
        {"principal_cents": 1_000_000, "product_id": "dpz_pt_12m_juros_venc"}
    )

    assert result == {"deposit_id": "d-1", "status": "ACTIVE"}
    assert captured["method"] == "POST"
    assert captured["url"] == "http://engine/v1/deposits"
    assert captured["body"]["principal_cents"] == 1_000_000  # integer cents, never a float


async def test_deposit_position_gets_by_id() -> None:
    captured: dict = {}

    def handler(request: httpx.Request) -> httpx.Response:
        captured["url"] = str(request.url)
        return httpx.Response(200, json={"deposit_id": "d-1", "total_payout_cents": 1_021_900})

    result = await _client(handler).deposit_position("d-1")

    assert captured["url"] == "http://engine/v1/deposits/d-1"
    assert result["total_payout_cents"] == 1_021_900


async def test_non_2xx_raises_fail_loud() -> None:
    def handler(request: httpx.Request) -> httpx.Response:
        return httpx.Response(422, json={"detail": "no rate sheet effective"})

    with pytest.raises(httpx.HTTPStatusError):
        await _client(handler).constitute({"principal_cents": 1})
