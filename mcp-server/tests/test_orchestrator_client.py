"""Contract tests for the orchestrator HTTP client — mocked transport, no live host.

The orchestrator client (bd babelstone-vjoi / ziu3.6 / Document 11 Pattern 2) is the mcp→orchestrator boundary
the saga channel reads/writes through — a separate boundary from mcp→engine because the orchestrator owns saga
state. These tests pin the request shape (URL, body, the forwarded gateway-attested X-Client-Id) for BOTH the
process-status READ and the constitute PRODUCER, and the fail-loud contract (a non-2xx raises, so the tool can
translate the expected 404/403/400 into a clean error).
"""

from __future__ import annotations

import httpx
import pytest

from babelstone_mcp.orchestrator_client import OrchestratorClient


def _client(handler) -> OrchestratorClient:
    return OrchestratorClient(
        "http://orchestrator", httpx.AsyncClient(transport=httpx.MockTransport(handler))
    )


async def test_process_status_gets_by_public_process_id() -> None:
    captured: dict = {}

    def handler(request: httpx.Request) -> httpx.Response:
        captured["method"] = request.method
        captured["url"] = str(request.url)
        return httpx.Response(
            200,
            json={
                "process_id": "PROC-2026-000123",
                "state": "AWAIT_WORKFLOW_APPROVAL",
                "status": "AWAITING_APPROVAL",
                "version": 7,
                "terminal": False,
            },
        )

    result = await _client(handler).process_status("PROC-2026-000123")

    assert captured["method"] == "GET"
    assert captured["url"] == "http://orchestrator/api/v1/processes/PROC-2026-000123/status"
    assert result["status"] == "AWAITING_APPROVAL"
    assert result["terminal"] is False


async def test_client_id_is_forwarded_as_x_client_id() -> None:
    # The gateway-attested caller (the OAuth sub Kong overwrote into X-Client-Id, ADR-IC-010 §P3) is
    # forwarded so the orchestrator can enforce per-process OWNERSHIP (ADR-IC-006 §P4).
    captured: dict[str, str | None] = {}

    def handler(request: httpx.Request) -> httpx.Response:
        captured["x_client_id"] = request.headers.get("X-Client-Id")
        return httpx.Response(200, json={"process_id": "PROC-1", "state": "STARTED",
                                         "status": "PROCESSING", "version": 0, "terminal": False})

    await _client(handler).process_status("PROC-1", client_id="CLI-7")
    assert captured["x_client_id"] == "CLI-7"


async def test_no_client_id_means_no_x_client_id_header() -> None:
    captured: dict[str, str | None] = {}

    def handler(request: httpx.Request) -> httpx.Response:
        captured["x_client_id"] = request.headers.get("X-Client-Id")
        return httpx.Response(200, json={"process_id": "PROC-1", "state": "STARTED",
                                         "status": "PROCESSING", "version": 0, "terminal": False})

    await _client(handler).process_status("PROC-1")
    assert captured["x_client_id"] is None


@pytest.mark.parametrize("status_code", [403, 404, 500])
async def test_non_2xx_raises_fail_loud(status_code: int) -> None:
    # Fail-loud like the engine client: the tool layer catches the EXPECTED 404/403 and translates them
    # into a clean McpError; a genuine 5xx propagates.
    def handler(request: httpx.Request) -> httpx.Response:
        return httpx.Response(status_code, json={"title": "nope"})

    with pytest.raises(httpx.HTTPStatusError):
        await _client(handler).process_status("PROC-NOPE")


# --- constitute: the Pattern 2 PRODUCER (bd babelstone-ziu3.6) -----------------------------------------


async def test_constitute_posts_to_the_saga_edge_and_returns_the_process_id() -> None:
    captured: dict = {}

    def handler(request: httpx.Request) -> httpx.Response:
        captured["method"] = request.method
        captured["url"] = str(request.url)
        captured["body"] = request.read().decode()
        return httpx.Response(
            202,
            json={
                "deposit_id": "DEP-2026-00012345",
                "process_id": "PROC-2026-00098765",
                "status": "PROCESSING",
                "stream_url": "/api/v1/processes/PROC-2026-00098765/stream",
            },
        )

    result = await _client(handler).constitute(
        {"product_code": "TD-TRAD-12M", "amount": 1_000_000, "source_account_ref": "acct-ref-1"}
    )

    assert captured["method"] == "POST"
    assert captured["url"] == "http://orchestrator/api/v1/deposits/constitute"
    # The PII-free structural body the saga pins — a product CODE + integer cents + an opaque account ref.
    assert '"product_code":"TD-TRAD-12M"' in captured["body"]
    assert '"amount":1000000' in captured["body"]
    assert result["process_id"] == "PROC-2026-00098765"
    assert result["status"] == "PROCESSING"


async def test_constitute_forwards_the_attested_caller_as_x_client_id() -> None:
    # The owning client is the gateway-attested X-Client-Id (OAuth sub) Kong overwrote — forwarded so the
    # orchestrator binds saga OWNERSHIP to it (ADR-IC-006 §P4), NEVER a body field (Document 11).
    captured: dict[str, str | None] = {}

    def handler(request: httpx.Request) -> httpx.Response:
        captured["x_client_id"] = request.headers.get("X-Client-Id")
        return httpx.Response(202, json={"deposit_id": "DEP-1", "process_id": "PROC-1",
                                         "status": "PROCESSING", "stream_url": "/s"})

    await _client(handler).constitute(
        {"product_code": "TD-TRAD-12M", "amount": 1, "source_account_ref": "a"}, client_id="CLI-OWNER"
    )
    assert captured["x_client_id"] == "CLI-OWNER"


@pytest.mark.parametrize("status_code", [400, 403, 500])
async def test_constitute_non_2xx_raises_fail_loud(status_code: int) -> None:
    # Fail-loud: a structurally-malformed request (400) or a missing attested caller (403) raises, so the
    # tool layer surfaces it rather than returning a partial result.
    def handler(request: httpx.Request) -> httpx.Response:
        return httpx.Response(status_code, json={"title": "nope"})

    with pytest.raises(httpx.HTTPStatusError):
        await _client(handler).constitute(
            {"product_code": "TD-TRAD-12M", "amount": 1, "source_account_ref": "a"}
        )
