"""Contract tests for the MCP surface — tool registration + the constitute/read mappings.

Each tool now reads the gateway-attested ``X-Client-Id`` / ``X-OAuth-Scope`` headers off the request
context and enforces scope-per-tool (ADR-IC-010 §P4, Epic J). These tests inject a fake ``Context``
carrying those headers so the direct-call mapping assertions exercise the same authorised path the
secured transport would.
"""

from __future__ import annotations

from typing import Any

from starlette.requests import Request

from babelstone_mcp import server
from babelstone_mcp.engine_client import EngineClient


class _FakeContext:
    """A minimal stand-in for FastMCP's ``Context`` exposing ``request_context.request``."""

    def __init__(self, *, client_id: str, scope: str) -> None:
        scope_obj = {
            "type": "http",
            "method": "POST",
            "path": "/mcp",
            "query_string": b"",
            "headers": [
                (b"x-client-id", client_id.encode()),
                (b"x-oauth-scope", scope.encode()),
            ],
        }
        self.request_context = type(
            "_RC", (), {"request": Request(scope_obj)}
        )()


def _read_ctx(client_id: str = "CLI-2026-007842") -> _FakeContext:
    return _FakeContext(client_id=client_id, scope="deposits:read")


def _write_ctx(client_id: str = "CLI-2026-007842") -> _FakeContext:
    return _FakeContext(client_id=client_id, scope="deposits:write")

_POSITION = {
    "deposit_id": "d-1",
    "sor": "engine",
    "principal_cents": 1_000_000,
    "tan_basis_points": 300,
    "rate_sheet_version_id": "pt-deposits-2026.1",
    "product_code": "dpz_pt_12m_juros_venc",
    "term_days": 365,
    "start_date": "2026-01-15",
    "maturity_date": "2027-01-15",
    "interest_variant": "AT_MATURITY",
    "auto_renewal_policy": "NONE",
    "payment_period_months": 0,
    "accrued_gross_interest_cents": 0,
    "withholding_to_date_cents": 0,
    "net_interest_cents": 0,
    "total_payout_cents": 0,
    "coupons_paid": 0,
    "lifecycle": "Active",
    "last_sequence": 0,
    "last_updated": "2026-01-15T00:00:00+00:00",
}


class _FakeEngine(EngineClient):
    """An engine client that records the constitute request and returns fixed results."""

    def __init__(self) -> None:  # noqa: D401 — bypass the real httpx client
        self.constitute_request: dict[str, Any] | None = None
        self.position_requested: str | None = None
        self.min_sequence_requested: int | None = None
        self.matured: str | None = None
        self.interest_paid: str | None = None

    async def constitute(self, request: dict[str, Any]) -> dict[str, Any]:
        self.constitute_request = request
        return {"deposit_id": "d-1", "status": "ACTIVE", "commit_sequence": 0}

    async def deposit_position(
        self, deposit_id: str, min_sequence: int | None = None
    ) -> dict[str, Any]:
        self.position_requested = deposit_id
        self.min_sequence_requested = min_sequence
        return {**_POSITION, "deposit_id": deposit_id}

    async def mature(self, deposit_id: str) -> dict[str, Any]:
        self.matured = deposit_id
        return {
            **_POSITION,
            "deposit_id": deposit_id,
            "accrued_gross_interest_cents": 30417,
            "withholding_to_date_cents": 8517,
            "net_interest_cents": 21900,
            "total_payout_cents": 1021900,
            "lifecycle": "Matured",
        }

    async def pay_interest(self, deposit_id: str) -> dict[str, Any]:
        self.interest_paid = deposit_id
        return {
            **_POSITION,
            "deposit_id": deposit_id,
            "interest_variant": "PERIODIC",
            "payment_period_months": 1,
            "accrued_gross_interest_cents": 139602,
            "withholding_to_date_cents": 39089,
            "net_interest_cents": 100513,
            "coupons_paid": 1,
            "lifecycle": "Active",
        }


async def test_every_tool_is_registered_with_output_schema() -> None:
    tools = await server.mcp.list_tools()
    by_name = {t.name: t for t in tools}

    # ADR-IC-010 P6 — every tool (read and write) declares a structured outputSchema.
    assert "constitute_deposit" in by_name
    assert by_name["constitute_deposit"].outputSchema is not None
    # Per the 2026-05-31 amendment the read surface is a tool, not a resource template.
    assert "get_deposit" in by_name
    assert by_name["get_deposit"].outputSchema is not None
    assert "mature_deposit" in by_name
    assert by_name["mature_deposit"].outputSchema is not None
    assert "pay_interest" in by_name
    assert by_name["pay_interest"].outputSchema is not None


async def test_no_resource_templates_are_registered() -> None:
    # The deposit read model moved from a resource template to the get_deposit tool.
    templates = await server.mcp.list_resource_templates()
    assert [t.uriTemplate for t in templates] == []


async def test_get_deposit_tool_maps_id_to_the_engine_read() -> None:
    fake = _FakeEngine()
    server.set_engine(fake)

    result = await server.get_deposit(deposit_id="d-42", ctx=_read_ctx(), min_sequence=7)

    assert fake.position_requested == "d-42"
    assert fake.min_sequence_requested == 7   # the read-your-writes token is threaded to the engine
    assert result.deposit_id == "d-42"
    assert result.tan_basis_points == 300
    assert result.lifecycle == "Active"
    assert result.sor == "engine"
    assert result.product_code == "dpz_pt_12m_juros_venc"
    assert result.last_sequence == 0


async def test_mature_deposit_tool_maps_id_and_folds_interest() -> None:
    fake = _FakeEngine()
    server.set_engine(fake)

    result = await server.mature_deposit(deposit_id="d-42", ctx=_write_ctx())

    assert fake.matured == "d-42"
    assert result.deposit_id == "d-42"
    # The matured fold carries the canonical end-to-end numbers (lifecycle flips to Matured).
    assert result.lifecycle == "Matured"
    assert result.total_payout_cents == 1_021_900


async def test_pay_interest_tool_maps_id_and_folds_the_coupon() -> None:
    fake = _FakeEngine()
    server.set_engine(fake)

    result = await server.pay_interest(deposit_id="d-42", ctx=_write_ctx())

    assert fake.interest_paid == "d-42"
    assert result.deposit_id == "d-42"
    # The coupon folds in; the deposit stays Active (the final coupon is paid at maturity).
    assert result.lifecycle == "Active"
    assert result.interest_variant == "PERIODIC"
    assert result.payment_period_months == 1
    assert result.net_interest_cents == 100_513
    assert result.coupons_paid == 1


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
        ctx=_write_ctx(),
    )

    assert result.deposit_id == "d-1"
    assert result.status == "ACTIVE"
    assert result.commit_sequence == 0   # the read-your-writes token the agent threads to get_deposit
    assert fake.constitute_request is not None
    assert fake.constitute_request["principal_cents"] == 1_000_000
    assert fake.constitute_request["product_id"] == "dpz_pt_12m_juros_venc"
    # Defaults applied for the AT_MATURITY walking skeleton.
    assert fake.constitute_request["interest_variant"] == "AT_MATURITY"
    assert fake.constitute_request["auto_renewal_policy"] == "NONE"
    assert fake.constitute_request["payment_period_months"] == 0
