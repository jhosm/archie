"""Contract tests for the MCP surface — tool registration + the constitute/read mappings.

Each tool now reads the gateway-attested ``X-Client-Id`` / ``X-OAuth-Scope`` headers off the request
context and enforces scope-per-tool (ADR-IC-010 §P4, Epic J). These tests inject a fake ``Context``
carrying those headers so the direct-call mapping assertions exercise the same authorised path the
secured transport would.
"""

from __future__ import annotations

from typing import Any

import httpx
import pytest
from mcp.server.elicitation import (
    AcceptedElicitation,
    AcceptedUrlElicitation,
    DeclinedElicitation,
)
from mcp.shared.exceptions import McpError
from starlette.requests import Request

from babelstone_mcp import server
from babelstone_mcp.elicitation import PeriodicInterestConfirmation
from babelstone_mcp.engine_client import EngineClient
from babelstone_mcp.orchestrator_client import OrchestratorClient


class _FakeContext:
    """A minimal stand-in for FastMCP's ``Context`` exposing ``request_context.request``.

    Optionally carries pre-seeded elicitation results so the §P8 human-in-the-loop paths
    (form-mode confirm on ``constitute_deposit``, URL-mode step-up on the money-movers) can be
    exercised by direct tool calls without a live MCP session. ``elicit`` / ``elicit_url`` record
    that they were called so tests can assert the elicitation fired (or did not).
    """

    def __init__(
        self,
        *,
        client_id: str,
        scope: str,
        elicit_result: object | None = None,
        elicit_url_result: object | None = None,
    ) -> None:
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
        self._elicit_result = elicit_result
        self._elicit_url_result = elicit_url_result
        self.elicit_called = False
        self.elicit_url_called = False
        self.elicit_url_args: tuple[str, str, str] | None = None

    async def elicit(self, message: str, schema: type) -> object:
        self.elicit_called = True
        return self._elicit_result

    async def elicit_url(self, message: str, url: str, elicitation_id: str) -> object:
        self.elicit_url_called = True
        self.elicit_url_args = (message, url, elicitation_id)
        return self._elicit_url_result


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
        # The gateway-attested caller each call forwarded to the engine (ADR-IC-010 §P3).
        self.client_id_forwarded: str | None = None

    async def constitute(
        self, request: dict[str, Any], client_id: str | None = None
    ) -> dict[str, Any]:
        self.constitute_request = request
        self.client_id_forwarded = client_id
        return {"deposit_id": "d-1", "status": "ACTIVE", "commit_sequence": 0}

    async def deposit_position(
        self, deposit_id: str, min_sequence: int | None = None, client_id: str | None = None
    ) -> dict[str, Any]:
        self.position_requested = deposit_id
        self.min_sequence_requested = min_sequence
        self.client_id_forwarded = client_id
        return {**_POSITION, "deposit_id": deposit_id}

    async def mature(self, deposit_id: str, client_id: str | None = None) -> dict[str, Any]:
        self.matured = deposit_id
        self.client_id_forwarded = client_id
        return {
            **_POSITION,
            "deposit_id": deposit_id,
            "accrued_gross_interest_cents": 30417,
            "withholding_to_date_cents": 8517,
            "net_interest_cents": 21900,
            "total_payout_cents": 1021900,
            "lifecycle": "Matured",
        }

    async def pay_interest(self, deposit_id: str, client_id: str | None = None) -> dict[str, Any]:
        self.interest_paid = deposit_id
        self.client_id_forwarded = client_id
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


class _FakeOrchestrator(OrchestratorClient):
    """An orchestrator client that records the poll and returns a fixed status (or raises a chosen
    HTTP status), bypassing the real httpx client."""

    def __init__(
        self, *, status: dict[str, Any] | None = None, raise_status: int | None = None
    ) -> None:  # noqa: D401 — bypass the real httpx client
        self.process_requested: str | None = None
        self.constitute_request: dict[str, Any] | None = None
        # The gateway-attested caller forwarded to the orchestrator for the ownership check (§P3).
        self.client_id_forwarded: str | None = None
        self._status = status or _STATUS
        self._raise_status = raise_status

    async def constitute(
        self, request: dict[str, Any], client_id: str | None = None
    ) -> dict[str, Any]:
        self.constitute_request = request
        self.client_id_forwarded = client_id
        if self._raise_status is not None:
            req = httpx.Request("POST", "http://orchestrator/api/v1/deposits/constitute")
            response = httpx.Response(self._raise_status, request=req)
            raise httpx.HTTPStatusError(f"{self._raise_status}", request=req, response=response)
        return {
            "deposit_id": "DEP-2026-00012345",
            "process_id": "PROC-2026-00098765",
            "status": "PROCESSING",
            "stream_url": "/api/v1/processes/PROC-2026-00098765/stream",
        }

    async def process_status(
        self, process_id: str, client_id: str | None = None
    ) -> dict[str, Any]:
        self.process_requested = process_id
        self.client_id_forwarded = client_id
        if self._raise_status is not None:
            request = httpx.Request(
                "GET", f"http://orchestrator/api/v1/processes/{process_id}/status"
            )
            response = httpx.Response(self._raise_status, request=request)
            raise httpx.HTTPStatusError(
                f"{self._raise_status}", request=request, response=response
            )
        return {**self._status, "process_id": process_id}


_STATUS = {
    "process_id": "PROC-2026-000123",
    "state": "AWAIT_WORKFLOW_APPROVAL",
    "status": "AWAITING_APPROVAL",
    "version": 7,
    "terminal": False,
}


async def test_every_tool_is_registered_with_output_schema() -> None:
    tools = await server.mcp.list_tools()
    by_name = {t.name: t for t in tools}

    # ADR-IC-010 P6 — every tool (read and write) declares a structured outputSchema.
    assert "constitute_deposit" in by_name
    assert by_name["constitute_deposit"].outputSchema is not None
    # The orchestrator-routed constitution PRODUCER (Document 11 Pattern 2; bd babelstone-ziu3.6).
    assert "constitute_deposit_saga" in by_name
    assert by_name["constitute_deposit_saga"].outputSchema is not None
    # Per the 2026-05-31 amendment the read surface is a tool, not a resource template.
    assert "get_deposit" in by_name
    assert by_name["get_deposit"].outputSchema is not None
    assert "mature_deposit" in by_name
    assert by_name["mature_deposit"].outputSchema is not None
    assert "pay_interest" in by_name
    assert by_name["pay_interest"].outputSchema is not None
    # The async-completion polling tool (Document 11 Pattern 2; bd babelstone-vjoi) — also a §P6 tool.
    assert "get_process_status" in by_name
    assert by_name["get_process_status"].outputSchema is not None


async def test_no_resources_or_templates_are_registered() -> None:
    # ADR-IC-010 §A2 (2026-05-31 amendment) replaced the `bank://deposits/{deposit_id}` resource
    # template with the `get_deposit` tool: the single, lag-sensitive deposit position is a
    # model-controlled on-demand read (a tool with a mandatory §P6 outputSchema), not host-attached
    # context. So neither the static resource list NOR the resource-template list carries a deposit
    # surface — the scoped MCP read surface is tools-only.
    resources = await server.mcp.list_resources()
    assert resources == []
    templates = await server.mcp.list_resource_templates()
    assert templates == []


async def test_both_prompts_are_registered() -> None:
    # Epic J.2: two vetted agent-workflow prompt templates (ADR-IC-010 §A1).
    prompts = await server.mcp.list_prompts()
    names = {p.name for p in prompts}
    assert "constitute_term_deposit" in names
    assert "review_upcoming_maturities" in names


async def test_get_deposit_tool_maps_id_to_the_engine_read() -> None:
    fake = _FakeEngine()
    server.set_engine(fake)

    result = await server.get_deposit(
        deposit_id="d-42", ctx=_read_ctx(client_id="CLI-ATTESTED-99"), min_sequence=7
    )

    assert fake.position_requested == "d-42"
    assert fake.min_sequence_requested == 7   # the read-your-writes token is threaded to the engine
    # The gateway-attested caller (X-Client-Id, the OAuth sub) is forwarded to the engine (§P3).
    assert fake.client_id_forwarded == "CLI-ATTESTED-99"
    assert result.deposit_id == "d-42"
    assert result.tan_basis_points == 300
    assert result.lifecycle == "Active"
    assert result.sor == "engine"
    assert result.product_code == "dpz_pt_12m_juros_venc"
    assert result.last_sequence == 0


# --- get_process_status: the async-completion polling tool (Document 11 Pattern 2; bd vjoi) ----------


async def test_get_process_status_maps_to_the_orchestrator_read() -> None:
    fake = _FakeOrchestrator()
    server.set_orchestrator(fake)

    result = await server.get_process_status(
        process_id="PROC-2026-000123", ctx=_read_ctx(client_id="CLI-POLL-1")
    )

    assert fake.process_requested == "PROC-2026-000123"
    # The gateway-attested caller (X-Client-Id, the OAuth sub) is forwarded so the orchestrator can
    # enforce per-process OWNERSHIP (§P3 / ADR-IC-006 §P4) — never a tool argument.
    assert fake.client_id_forwarded == "CLI-POLL-1"
    assert result.process_id == "PROC-2026-000123"
    assert result.state == "AWAIT_WORKFLOW_APPROVAL"
    assert result.status == "AWAITING_APPROVAL"   # the coarse agent-facing projection
    assert result.version == 7
    assert result.terminal is False


async def test_get_process_status_requires_the_read_scope() -> None:
    # get_process_status is a READ (deposits:read) — a write-only token cannot reach it, and the
    # rejection happens BEFORE the orchestrator is touched (ADR-IC-010 §P4).
    fake = _FakeOrchestrator()
    server.set_orchestrator(fake)

    with pytest.raises(McpError):
        await server.get_process_status(process_id="PROC-1", ctx=_write_ctx())

    assert fake.process_requested is None


async def test_get_process_status_unknown_process_raises_clean_mcp_error() -> None:
    # The orchestrator 404 (no such process) is an EXPECTED outcome — surfaced as a clean McpError, not
    # a raw transport error.
    fake = _FakeOrchestrator(raise_status=404)
    server.set_orchestrator(fake)

    with pytest.raises(McpError) as exc:
        await server.get_process_status(process_id="PROC-NOPE", ctx=_read_ctx())

    assert "no process found" in exc.value.error.message.lower()


async def test_get_process_status_other_owner_raises_forbidden_mcp_error() -> None:
    # The orchestrator 403 (the process is owned by another client) is surfaced as a clean McpError;
    # process_id is not a capability token (ADR-IC-006 §P4). No caller-id leak in the message.
    fake = _FakeOrchestrator(raise_status=403)
    server.set_orchestrator(fake)

    with pytest.raises(McpError) as exc:
        await server.get_process_status(
            process_id="PROC-OTHER", ctx=_read_ctx(client_id="CLI-NOT-OWNER")
        )

    message = exc.value.error.message
    assert "different client" in message.lower() or "your own" in message.lower()
    assert "CLI-NOT-OWNER" not in message


# --- constitute_deposit_saga: the Pattern 2 PRODUCER (Document 11; bd babelstone-ziu3.6) -------------


async def test_constitute_deposit_saga_routes_to_the_orchestrator_and_returns_a_process_id() -> None:
    fake = _FakeOrchestrator()
    server.set_orchestrator(fake)

    result = await server.constitute_deposit_saga(
        product_code="TD-TRAD-12M",
        amount_cents=1_000_000,
        source_account_ref="acct-ref-1",
        ctx=_write_ctx(client_id="CLI-SAGA-1"),
        interest_account_ref="acct-ref-2",
    )

    # The body the saga edge pins — a product CODE + integer cents + opaque account refs (no product
    # shape, no raw IBAN; ADR-PC-004 §P2 / ADR-PC-009).
    assert fake.constitute_request == {
        "product_code": "TD-TRAD-12M",
        "amount": 1_000_000,
        "source_account_ref": "acct-ref-1",
        "interest_account_ref": "acct-ref-2",
    }
    # The gateway-attested caller (X-Client-Id, OAuth sub) is forwarded so the orchestrator binds saga
    # OWNERSHIP to it (§P3 / ADR-IC-006 §P4) — never a tool argument.
    assert fake.client_id_forwarded == "CLI-SAGA-1"
    # The PRODUCER returns the saga process_id (NOT a bare deposit_id) the agent polls (Document 11 Pattern 2).
    assert result.process_id == "PROC-2026-00098765"
    assert result.deposit_id == "DEP-2026-00012345"
    assert result.status == "PROCESSING"
    # The typed follow_up hint points the agent at the polling tool with the minted process_id.
    assert result.follow_up.kind == "poll_tool"
    assert result.follow_up.tool == "get_process_status"
    assert result.follow_up.arguments == {"process_id": "PROC-2026-00098765"}


async def test_constitute_deposit_saga_omits_interest_account_ref_when_not_given() -> None:
    # interest_account_ref is optional; when omitted it is left off the body rather than sent as null.
    fake = _FakeOrchestrator()
    server.set_orchestrator(fake)

    await server.constitute_deposit_saga(
        product_code="TD-TRAD-12M",
        amount_cents=500_000,
        source_account_ref="acct-ref-1",
        ctx=_write_ctx(),
    )

    assert fake.constitute_request is not None
    assert "interest_account_ref" not in fake.constitute_request


async def test_constitute_deposit_saga_requires_the_write_scope() -> None:
    # The producer STARTS a saga — a write (deposits:write). A read-only token cannot reach it, and the
    # rejection happens BEFORE the orchestrator is touched (ADR-IC-010 §P4).
    fake = _FakeOrchestrator()
    server.set_orchestrator(fake)

    with pytest.raises(McpError):
        await server.constitute_deposit_saga(
            product_code="TD-TRAD-12M",
            amount_cents=1_000_000,
            source_account_ref="acct-ref-1",
            ctx=_read_ctx(),
        )

    assert fake.constitute_request is None


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


async def test_every_write_tool_forwards_the_attested_caller_to_the_engine() -> None:
    # The gateway-attested X-Client-Id (OAuth sub) is forwarded on EVERY engine call so the engine
    # sees who acted (ADR-IC-010 §P3 / Document 11) — never a tool argument.
    fake = _FakeEngine()
    server.set_engine(fake)

    await server.mature_deposit(deposit_id="d-42", ctx=_write_ctx(client_id="CLI-MATURE-1"))
    assert fake.client_id_forwarded == "CLI-MATURE-1"

    await server.pay_interest(deposit_id="d-42", ctx=_write_ctx(client_id="CLI-COUPON-2"))
    assert fake.client_id_forwarded == "CLI-COUPON-2"


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
        ctx=_write_ctx(client_id="CLI-CONSTITUTE-3"),
    )

    assert fake.client_id_forwarded == "CLI-CONSTITUTE-3"
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


# ---------------------------------------------------------------------------------------------
# §P8 human-in-the-loop elicitation (Epic J.4, bd babelstone-ar1y)
#
# Form mode is the SHIPPABLE v1 path on the one non-irreversible clarification we have today:
# constituting a PERIODIC deposit asks the human to confirm the periodic-coupon choice. URL mode is
# the v1 MACHINERY for step-up SCA on the irreversible money-movers (mature / pay_interest), gated
# behind ELICITATION_URL_MODE_ENABLED (default off) until the maintainer resolves the SCA-trigger +
# token-re-entry fork. Both modes carry only generic text (no PII) in the prompt.
# ---------------------------------------------------------------------------------------------


def _constitute_periodic_ctx(
    elicit_result: object, client_id: str = "CLI-2026-007842"
) -> _FakeContext:
    return _FakeContext(
        client_id=client_id, scope="deposits:write", elicit_result=elicit_result
    )


async def test_constitute_deposit_with_periodic_elicitation_accepted() -> None:
    fake = _FakeEngine()
    server.set_engine(fake)
    ctx = _constitute_periodic_ctx(
        AcceptedElicitation(data=PeriodicInterestConfirmation(confirmed=True))
    )

    result = await server.constitute_deposit(
        product_id="dpz_pt_12m_juros_mensal",
        role="standard",
        principal_cents=1_000_000,
        term_days=365,
        start_date="2026-01-15",
        funding_account="PT50-DDA-001",
        ctx=ctx,
        interest_variant="PERIODIC",
        payment_period_months=1,
    )

    # The human confirmed → the engine command runs and the result comes back normally.
    assert ctx.elicit_called is True
    assert fake.constitute_request is not None
    assert fake.constitute_request["interest_variant"] == "PERIODIC"
    assert result.deposit_id == "d-1"


async def test_constitute_deposit_with_periodic_elicitation_declined_raises_mcp_error() -> None:
    fake = _FakeEngine()
    server.set_engine(fake)
    ctx = _constitute_periodic_ctx(DeclinedElicitation())

    with pytest.raises(McpError) as exc:
        await server.constitute_deposit(
            product_id="dpz_pt_12m_juros_mensal",
            role="standard",
            principal_cents=1_000_000,
            term_days=365,
            start_date="2026-01-15",
            funding_account="PT50-DDA-001",
            ctx=ctx,
            interest_variant="PERIODIC",
            payment_period_months=1,
        )

    # The clarification fired, and declining aborts BEFORE the engine command (no money moves on a
    # non-confirmation).
    assert ctx.elicit_called is True
    assert fake.constitute_request is None
    message = exc.value.error.message
    assert "did not confirm" in message.lower()
    # No PII in the surfaced error (no funding account / client id leak).
    assert "PT50" not in message
    assert "CLI-" not in message


async def test_constitute_deposit_with_periodic_explicit_no_raises_mcp_error() -> None:
    # The human accepted the form but answered "no" (confirmed=False) — a semantic non-confirmation.
    # The deposit must NOT be constituted, same as a decline.
    fake = _FakeEngine()
    server.set_engine(fake)
    ctx = _constitute_periodic_ctx(
        AcceptedElicitation(data=PeriodicInterestConfirmation(confirmed=False))
    )

    with pytest.raises(McpError) as exc:
        await server.constitute_deposit(
            product_id="dpz_pt_12m_juros_mensal",
            role="standard",
            principal_cents=1_000_000,
            term_days=365,
            start_date="2026-01-15",
            funding_account="PT50-DDA-001",
            ctx=ctx,
            interest_variant="PERIODIC",
            payment_period_months=1,
        )

    assert fake.constitute_request is None
    assert "did not confirm" in exc.value.error.message.lower()


async def test_constitute_deposit_at_maturity_skips_elicitation() -> None:
    fake = _FakeEngine()
    server.set_engine(fake)
    # AT_MATURITY is not a periodic choice → no clarification fires.
    ctx = _FakeContext(client_id="CLI-2026-007842", scope="deposits:write")

    result = await server.constitute_deposit(
        product_id="dpz_pt_12m_juros_venc",
        role="standard",
        principal_cents=1_000_000,
        term_days=365,
        start_date="2026-01-15",
        funding_account="PT50-DDA-001",
        ctx=ctx,
        interest_variant="AT_MATURITY",
    )

    assert ctx.elicit_called is False
    assert fake.constitute_request is not None
    assert result.deposit_id == "d-1"


# --- URL-mode step-up: mature_deposit --------------------------------------------------------


async def test_mature_deposit_url_mode_enabled_accept_proceeds_to_engine(
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    monkeypatch.setattr(server, "ELICITATION_URL_MODE_ENABLED", True)
    fake = _FakeEngine()
    server.set_engine(fake)
    ctx = _FakeContext(
        client_id="CLI-MATURE-1",
        scope="deposits:write",
        elicit_url_result=AcceptedUrlElicitation(),
    )

    result = await server.mature_deposit(deposit_id="d-42", ctx=ctx)

    assert ctx.elicit_url_called is True
    assert fake.matured == "d-42"
    assert result.lifecycle == "Matured"
    # The URL carries only a stable op code + the elicitation UUID — never the deposit id.
    _msg, url, _eid = ctx.elicit_url_args  # type: ignore[misc]
    assert "operation=MATURE_DEPOSIT" in url
    assert "d-42" not in url


async def test_mature_deposit_url_mode_enabled_decline_raises_mcp_error(
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    monkeypatch.setattr(server, "ELICITATION_URL_MODE_ENABLED", True)
    fake = _FakeEngine()
    server.set_engine(fake)
    ctx = _FakeContext(
        client_id="CLI-MATURE-1",
        scope="deposits:write",
        elicit_url_result=DeclinedElicitation(),
    )

    with pytest.raises(McpError) as exc:
        await server.mature_deposit(deposit_id="d-42", ctx=ctx)

    # Declining the step-up aborts before settlement; the error is static (no PII).
    assert fake.matured is None
    assert "d-42" not in exc.value.error.message


async def test_mature_deposit_url_mode_disabled_skips_elicitation() -> None:
    # Default deployment: URL mode off → the affordance is dormant, the tool behaves as before.
    fake = _FakeEngine()
    server.set_engine(fake)
    ctx = _FakeContext(
        client_id="CLI-MATURE-1",
        scope="deposits:write",
        elicit_url_result=AcceptedUrlElicitation(),
    )

    result = await server.mature_deposit(deposit_id="d-42", ctx=ctx)

    assert ctx.elicit_url_called is False
    assert fake.matured == "d-42"
    assert result.lifecycle == "Matured"


# --- URL-mode step-up: pay_interest ----------------------------------------------------------


async def test_pay_interest_url_mode_enabled_accept_proceeds_to_engine(
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    monkeypatch.setattr(server, "ELICITATION_URL_MODE_ENABLED", True)
    fake = _FakeEngine()
    server.set_engine(fake)
    ctx = _FakeContext(
        client_id="CLI-COUPON-2",
        scope="deposits:write",
        elicit_url_result=AcceptedUrlElicitation(),
    )

    result = await server.pay_interest(deposit_id="d-42", ctx=ctx)

    assert ctx.elicit_url_called is True
    assert fake.interest_paid == "d-42"
    assert result.coupons_paid == 1
    _msg, url, _eid = ctx.elicit_url_args  # type: ignore[misc]
    assert "operation=PAY_INTEREST" in url
    assert "d-42" not in url


async def test_pay_interest_url_mode_enabled_decline_raises_mcp_error(
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    monkeypatch.setattr(server, "ELICITATION_URL_MODE_ENABLED", True)
    fake = _FakeEngine()
    server.set_engine(fake)
    ctx = _FakeContext(
        client_id="CLI-COUPON-2",
        scope="deposits:write",
        elicit_url_result=DeclinedElicitation(),
    )

    with pytest.raises(McpError) as exc:
        await server.pay_interest(deposit_id="d-42", ctx=ctx)

    assert fake.interest_paid is None
    assert "d-42" not in exc.value.error.message


async def test_pay_interest_url_mode_disabled_skips_elicitation() -> None:
    fake = _FakeEngine()
    server.set_engine(fake)
    ctx = _FakeContext(
        client_id="CLI-COUPON-2",
        scope="deposits:write",
        elicit_url_result=AcceptedUrlElicitation(),
    )

    result = await server.pay_interest(deposit_id="d-42", ctx=ctx)

    assert ctx.elicit_url_called is False
    assert fake.interest_paid == "d-42"
    assert result.coupons_paid == 1
