"""Contract tests for the engine HTTP client — mocked transport, no live engine."""

from __future__ import annotations

import json
import uuid

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
        captured["idempotency_key"] = request.headers.get("Idempotency-Key")
        return httpx.Response(201, json={"deposit_id": "d-1", "status": "ACTIVE", "commit_sequence": 0})

    result = await _client(handler).constitute(
        {"principal_cents": 1_000_000, "product_id": "dpz_pt_12m_juros_venc"}
    )

    assert result == {"deposit_id": "d-1", "status": "ACTIVE", "commit_sequence": 0}
    assert captured["method"] == "POST"
    assert captured["url"] == "http://engine/v1/deposits"
    assert captured["body"]["principal_cents"] == 1_000_000  # integer cents, never a float
    # The engine now MANDATES a UUID Idempotency-Key (ADR-PC-029 slot 1) — 400 without it. The agent
    # channel has no saga_outbox row id, so the client mints a fresh per-call UUID (ADR-IC-010).
    assert captured["idempotency_key"] is not None
    assert uuid.UUID(captured["idempotency_key"])  # parses as a UUID — raises otherwise


async def test_constitute_mints_a_fresh_idempotency_key_per_call() -> None:
    # Each constitute() call is its own command (the agent is not the saga), so two calls carry two
    # distinct keys — the per-call UUID is generated client-side, not reused.
    keys: list[str | None] = []

    def handler(request: httpx.Request) -> httpx.Response:
        keys.append(request.headers.get("Idempotency-Key"))
        return httpx.Response(201, json={"deposit_id": "d-1", "status": "ACTIVE", "commit_sequence": 0})

    client = _client(handler)
    await client.constitute({"principal_cents": 1})
    await client.constitute({"principal_cents": 2})

    assert keys[0] is not None and keys[1] is not None
    assert keys[0] != keys[1]


async def test_deposit_position_gets_by_id() -> None:
    captured: dict = {}

    def handler(request: httpx.Request) -> httpx.Response:
        captured["url"] = str(request.url)
        captured["if_min_sequence"] = request.headers.get("If-Min-Sequence")
        return httpx.Response(200, json={"deposit_id": "d-1", "total_payout_cents": 1_021_900})

    result = await _client(handler).deposit_position("d-1")

    assert captured["url"] == "http://engine/v1/deposits/d-1"
    assert captured["if_min_sequence"] is None  # no token → no header, serve the read model
    assert result["total_payout_cents"] == 1_021_900


async def test_deposit_position_sends_if_min_sequence_header_when_given() -> None:
    # Read-your-writes (ADR-IC-005 §P3): a min_sequence token rides as If-Min-Sequence, so the engine
    # folds the stream if the projector lags behind the caller's just-committed write.
    captured: dict = {}

    def handler(request: httpx.Request) -> httpx.Response:
        captured["if_min_sequence"] = request.headers.get("If-Min-Sequence")
        return httpx.Response(200, json={"deposit_id": "d-1", "last_sequence": 3})

    await _client(handler).deposit_position("d-1", min_sequence=3)

    assert captured["if_min_sequence"] == "3"


async def test_non_2xx_raises_fail_loud() -> None:
    def handler(request: httpx.Request) -> httpx.Response:
        return httpx.Response(422, json={"detail": "no rate sheet effective"})

    with pytest.raises(httpx.HTTPStatusError):
        await _client(handler).constitute({"principal_cents": 1})


async def test_client_id_is_forwarded_as_x_client_id_on_every_surface() -> None:
    # The gateway-attested caller (the OAuth sub Kong overwrote into X-Client-Id, ADR-IC-010 §P3)
    # is forwarded to the engine on each surface so the engine sees who acted, for audit/ownership.
    seen: dict[str, str | None] = {}

    def handler(request: httpx.Request) -> httpx.Response:
        seen[request.method + " " + request.url.path] = request.headers.get("X-Client-Id")
        if request.method == "GET":
            return httpx.Response(200, json={"deposit_id": "d-1"})
        if request.url.path == "/v1/deposits":
            return httpx.Response(201, json={"deposit_id": "d-1", "status": "ACTIVE", "commit_sequence": 0})
        return httpx.Response(200, json={"deposit_id": "d-1"})

    client = _client(handler)
    await client.constitute({"principal_cents": 1}, client_id="CLI-7")
    await client.deposit_position("d-1", client_id="CLI-7")
    await client.mature("d-1", client_id="CLI-7")
    await client.pay_interest("d-1", client_id="CLI-7")

    assert seen["POST /v1/deposits"] == "CLI-7"
    assert seen["GET /v1/deposits/d-1"] == "CLI-7"
    assert seen["POST /v1/deposits/d-1/maturity"] == "CLI-7"
    assert seen["POST /v1/deposits/d-1/interest"] == "CLI-7"


async def test_no_client_id_means_no_x_client_id_header() -> None:
    # When no caller is attested (e.g. a dev-direct call), no X-Client-Id is invented.
    captured: dict[str, str | None] = {}

    def handler(request: httpx.Request) -> httpx.Response:
        captured["x_client_id"] = request.headers.get("X-Client-Id")
        return httpx.Response(200, json={"deposit_id": "d-1"})

    await _client(handler).deposit_position("d-1")
    assert captured["x_client_id"] is None
