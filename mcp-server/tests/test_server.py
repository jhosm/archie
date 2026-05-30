"""Contract tests for the MCP surface — tool/resource registration + the constitute mapping."""

from __future__ import annotations

from typing import Any

from babelstone_mcp import server
from babelstone_mcp.engine_client import EngineClient


class _FakeEngine(EngineClient):
    """An engine client that records the constitute request and returns a fixed result."""

    def __init__(self) -> None:  # noqa: D401 — bypass the real httpx client
        self.constitute_request: dict[str, Any] | None = None

    async def constitute(self, request: dict[str, Any]) -> dict[str, Any]:
        self.constitute_request = request
        return {"deposit_id": "d-1", "status": "ACTIVE"}

    async def deposit_position(self, deposit_id: str) -> dict[str, Any]:
        return {"deposit_id": deposit_id, "lifecycle": "Active"}


async def test_constitute_deposit_tool_is_registered_with_output_schema() -> None:
    tools = await server.mcp.list_tools()
    by_name = {t.name: t for t in tools}

    assert "constitute_deposit" in by_name
    # ADR-IC-010 P6 — every tool declares a structured outputSchema.
    assert by_name["constitute_deposit"].outputSchema is not None


async def test_deposit_position_resource_template_is_registered() -> None:
    templates = await server.mcp.list_resource_templates()
    uris = {t.uriTemplate for t in templates}

    assert "bank://deposits/{deposit_id}" in uris


async def test_constitute_tool_maps_args_to_the_engine_request() -> None:
    fake = _FakeEngine()
    server.set_engine(fake)

    result = await server.constitute_deposit(
        product_id="dpz_pt_12m_juros_venc",
        role="standard",
        principal_cents=1_000_000,
        term_days=365,
        start_date="2026-01-15",
        funding_account="PT50-DDA-001",
    )

    assert result.deposit_id == "d-1"
    assert result.status == "ACTIVE"
    assert fake.constitute_request is not None
    assert fake.constitute_request["principal_cents"] == 1_000_000
    assert fake.constitute_request["product_id"] == "dpz_pt_12m_juros_venc"
    # Defaults applied for the AT_MATURITY walking skeleton.
    assert fake.constitute_request["interest_variant"] == "AT_MATURITY"
    assert fake.constitute_request["auto_renewal_policy"] == "NONE"
