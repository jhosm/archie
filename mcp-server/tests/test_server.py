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
from babelstone_mcp.engine_client import EngineClient, ScaRequiredError
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
    """An engine client that records the constitute request and returns fixed results.

    ``sca_required_calls`` makes the money-movers (``mature`` / ``pay_interest``) raise
    :class:`ScaRequiredError` that many times before they settle — modelling the engine's §P8 step-up
    gate (Q-BE Q1). 0 = the caller already holds fresh SCA (settles first try, no prompt); 1 = the
    common step-up-then-retry path (422, then the refreshed retry settles); 2 = the step-up never
    delivered a fresh token (both tries 422). ``mature_attempts`` / ``interest_attempts`` count the
    engine calls so a test can assert the retry actually happened."""

    def __init__(self, sca_required_calls: int = 0) -> None:  # noqa: D401 — bypass the real httpx client
        self.constitute_request: dict[str, Any] | None = None
        self.position_requested: str | None = None
        self.min_sequence_requested: int | None = None
        self.matured: str | None = None
        self.interest_paid: str | None = None
        # The gateway-attested caller each call forwarded to the engine (ADR-IC-010 §P3).
        self.client_id_forwarded: str | None = None
        # §P8 step-up-SCA gate simulation (bd babelstone-ziu3.5).
        self._sca_required_remaining = sca_required_calls
        self.mature_attempts = 0
        self.interest_attempts = 0

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

    def _maybe_require_sca(self) -> None:
        """Raise ScaRequiredError while the simulated gate has not been satisfied (bd babelstone-ziu3.5)."""
        if self._sca_required_remaining > 0:
            self._sca_required_remaining -= 1
            raise ScaRequiredError()

    async def mature(self, deposit_id: str, client_id: str | None = None) -> dict[str, Any]:
        self.mature_attempts += 1
        self._maybe_require_sca()
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
        self.interest_attempts += 1
        self._maybe_require_sca()
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


class _FakeLoanEngine(EngineClient):
    """A loan engine client modelling the SERVER-DERIVED, number-pinned installment idempotency
    (ADR-PC-036 §Decision 1+3 / LCD-1; bd babelstone-6cpq.1).

    It pays the next unpaid occurrence and records a ``command_dedup`` receipt under that occurrence's
    NUMBER; a re-fire of the SAME occurrence — the authoritative fold has not advanced, e.g. a concurrent
    submit or an at-least-once retry — replays the original outcome with NO second money leg. Set
    ``freeze_occurrence`` to model that re-fire (the fold stays put, so two calls derive the same
    number-pinned key and dedupe). ``sca_required_calls`` drives the §P8 step-up gate exactly like
    :class:`_FakeEngine`."""

    def __init__(self, term_months: int = 2, sca_required_calls: int = 0) -> None:  # noqa: D401 — bypass the real httpx client
        self.term_months = term_months
        self.installments_paid = 0
        self.money_legs = 0
        self._receipts: dict[int, int] = {}  # occurrence number -> commit_sequence (the dedup log)
        self.installment_attempts = 0
        self.installment_loan: str | None = None
        self.installment_collection_ref: str | None = None
        # The gateway-attested caller this call forwarded to the engine (ADR-IC-010 §P3).
        self.client_id_forwarded: str | None = None
        # §P8 step-up-SCA gate simulation (bd babelstone-ziu3.5).
        self._sca_required_remaining = sca_required_calls
        # When True, the authoritative fold does NOT advance between calls — modelling a concurrent /
        # at-least-once re-fire of the SAME occurrence, so its number-pinned key dedupes.
        self.freeze_occurrence = False

    def _maybe_require_sca(self) -> None:
        if self._sca_required_remaining > 0:
            self._sca_required_remaining -= 1
            raise ScaRequiredError()

    async def pay_installment(
        self, loan_id: str, collection_account_ref: str, client_id: str | None = None
    ) -> dict[str, Any]:
        self.installment_attempts += 1
        self._maybe_require_sca()
        self.installment_loan = loan_id
        self.installment_collection_ref = collection_account_ref
        self.client_id_forwarded = client_id
        # The engine derives the occurrence from the authoritative fold (InstallmentsPaid + 1) and the
        # number-pinned key from that occurrence — never a caller key (ADR-PC-036 §Decision 1+3).
        occurrence = self.installments_paid + 1
        if occurrence in self._receipts:
            commit_sequence = self._receipts[occurrence]  # number-pinned replay — NO second money leg
        else:
            self.money_legs += 1
            commit_sequence = occurrence  # a stand-in monotonic per-stream sequence
            self._receipts[occurrence] = commit_sequence
            if not self.freeze_occurrence:
                self.installments_paid = occurrence
        status = "SETTLED" if self.installments_paid >= self.term_months else "ACTIVE"
        return {"loan_id": loan_id, "status": status, "commit_sequence": commit_sequence}


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
    # The personal-loan installment money-mover (bd babelstone-6cpq.2) — also a §P6 tool.
    assert "pay_installment" in by_name
    assert by_name["pay_installment"].outputSchema is not None
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
# §P8 human-in-the-loop elicitation (Epic J.4 ar1y + Q-BE resolution ziu3.5)
#
# Form mode is the SHIPPABLE path on the one non-irreversible clarification we have today: constituting
# a PERIODIC deposit asks the human to confirm the periodic-coupon choice. URL mode is now a REAL
# step-up-SCA gate on the irreversible money-movers (mature / pay_interest): the ENGINE 422s without
# fresh gateway-attested SCA, the tool fires the step-up elicitation and retries with the refreshed
# token. Both modes carry only generic text (no PII) in the prompt.
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


# --- Step-up SCA gate: mature_deposit (Q-BE resolved, bd babelstone-ziu3.5) ------------------
#
# The gate is the ENGINE: it 422s a money-mover without fresh gateway-attested SCA (ScaRequiredError).
# The tool then fires the URL-mode step-up elicitation and RETRIES with the refreshed token. So the
# elicitation fires IFF the engine demanded it — never proactively (no over-prompting a caller who
# already holds fresh SCA). An accept the agent fabricates is still 422'd on the retry — the gate
# cannot be bypassed from the client side.


async def test_mature_deposit_with_fresh_sca_settles_without_prompting() -> None:
    # The caller already holds fresh SCA → the engine settles first try → no step-up prompt fires.
    fake = _FakeEngine(sca_required_calls=0)
    server.set_engine(fake)
    ctx = _FakeContext(
        client_id="CLI-MATURE-1",
        scope="deposits:write",
        elicit_url_result=AcceptedUrlElicitation(),
    )

    result = await server.mature_deposit(deposit_id="d-42", ctx=ctx)

    assert ctx.elicit_url_called is False
    assert fake.mature_attempts == 1
    assert fake.matured == "d-42"
    assert result.lifecycle == "Matured"


async def test_mature_deposit_422_steps_up_then_retries_and_settles() -> None:
    # The engine demands SCA (422), the human completes the step-up (accept), the retry settles.
    fake = _FakeEngine(sca_required_calls=1)
    server.set_engine(fake)
    ctx = _FakeContext(
        client_id="CLI-MATURE-1",
        scope="deposits:write",
        elicit_url_result=AcceptedUrlElicitation(),
    )

    result = await server.mature_deposit(deposit_id="d-42", ctx=ctx)

    assert ctx.elicit_url_called is True
    assert fake.mature_attempts == 2  # initial 422 + the post-step-up retry
    assert fake.matured == "d-42"
    assert result.lifecycle == "Matured"
    # The URL carries only a stable op code + the elicitation UUID — never the deposit id.
    _msg, url, _eid = ctx.elicit_url_args  # type: ignore[misc]
    assert "operation=MATURE_DEPOSIT" in url
    assert "d-42" not in url


async def test_mature_deposit_step_up_declined_raises_mcp_error_without_settling() -> None:
    fake = _FakeEngine(sca_required_calls=1)
    server.set_engine(fake)
    ctx = _FakeContext(
        client_id="CLI-MATURE-1",
        scope="deposits:write",
        elicit_url_result=DeclinedElicitation(),
    )

    with pytest.raises(McpError) as exc:
        await server.mature_deposit(deposit_id="d-42", ctx=ctx)

    # Declining the step-up aborts before any retry; nothing settled, error is static (no PII).
    assert fake.matured is None
    assert fake.mature_attempts == 1  # only the initial 422; no retry after a decline
    assert "d-42" not in exc.value.error.message


async def test_mature_deposit_retry_still_422_raises_mcp_error_never_settles() -> None:
    # The agent "accepted" but never obtained a genuinely refreshed token → the retry is STILL 422.
    # The tool surfaces an McpError; it never settles on the agent's word (the bypass-resistance test).
    fake = _FakeEngine(sca_required_calls=2)
    server.set_engine(fake)
    ctx = _FakeContext(
        client_id="CLI-MATURE-1",
        scope="deposits:write",
        elicit_url_result=AcceptedUrlElicitation(),
    )

    with pytest.raises(McpError) as exc:
        await server.mature_deposit(deposit_id="d-42", ctx=ctx)

    assert fake.matured is None
    assert fake.mature_attempts == 2  # initial 422 + one retry, both refused
    assert "d-42" not in exc.value.error.message


async def test_mature_deposit_url_mode_disabled_still_gated_by_engine(
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    # With the prompt off, a 422 is surfaced directly as an McpError — the engine STILL gates; the tool
    # just cannot run the step-up prompt. The operation is NOT bypassed.
    monkeypatch.setattr(server, "ELICITATION_URL_MODE_ENABLED", False)
    fake = _FakeEngine(sca_required_calls=1)
    server.set_engine(fake)
    ctx = _FakeContext(client_id="CLI-MATURE-1", scope="deposits:write")

    with pytest.raises(McpError):
        await server.mature_deposit(deposit_id="d-42", ctx=ctx)

    assert ctx.elicit_url_called is False
    assert fake.matured is None


# --- Step-up SCA gate: pay_interest ----------------------------------------------------------


async def test_pay_interest_with_fresh_sca_settles_without_prompting() -> None:
    fake = _FakeEngine(sca_required_calls=0)
    server.set_engine(fake)
    ctx = _FakeContext(
        client_id="CLI-COUPON-2",
        scope="deposits:write",
        elicit_url_result=AcceptedUrlElicitation(),
    )

    result = await server.pay_interest(deposit_id="d-42", ctx=ctx)

    assert ctx.elicit_url_called is False
    assert fake.interest_attempts == 1
    assert fake.interest_paid == "d-42"
    assert result.coupons_paid == 1


async def test_pay_interest_422_steps_up_then_retries_and_settles() -> None:
    fake = _FakeEngine(sca_required_calls=1)
    server.set_engine(fake)
    ctx = _FakeContext(
        client_id="CLI-COUPON-2",
        scope="deposits:write",
        elicit_url_result=AcceptedUrlElicitation(),
    )

    result = await server.pay_interest(deposit_id="d-42", ctx=ctx)

    assert ctx.elicit_url_called is True
    assert fake.interest_attempts == 2
    assert fake.interest_paid == "d-42"
    assert result.coupons_paid == 1
    _msg, url, _eid = ctx.elicit_url_args  # type: ignore[misc]
    assert "operation=PAY_INTEREST" in url
    assert "d-42" not in url


async def test_pay_interest_step_up_declined_raises_mcp_error_without_settling() -> None:
    fake = _FakeEngine(sca_required_calls=1)
    server.set_engine(fake)
    ctx = _FakeContext(
        client_id="CLI-COUPON-2",
        scope="deposits:write",
        elicit_url_result=DeclinedElicitation(),
    )

    with pytest.raises(McpError) as exc:
        await server.pay_interest(deposit_id="d-42", ctx=ctx)

    assert fake.interest_paid is None
    assert fake.interest_attempts == 1
    assert "d-42" not in exc.value.error.message


async def test_pay_interest_retry_still_422_raises_mcp_error_never_settles() -> None:
    fake = _FakeEngine(sca_required_calls=2)
    server.set_engine(fake)
    ctx = _FakeContext(
        client_id="CLI-COUPON-2",
        scope="deposits:write",
        elicit_url_result=AcceptedUrlElicitation(),
    )

    with pytest.raises(McpError) as exc:
        await server.pay_interest(deposit_id="d-42", ctx=ctx)

    assert fake.interest_paid is None
    assert fake.interest_attempts == 2
    assert "d-42" not in exc.value.error.message


# --- pay_installment: the personal-loan installment money-mover (bd babelstone-6cpq.2) -------
#
# The loan analogue of mature_deposit / pay_interest: a money-mover that POSTs the engine loan command
# surface and INHERITS the same §P8 step-up-SCA gate. Two things make it distinct: it returns the loan
# COMMAND OUTCOME ({loan_id, status, commit_sequence}), not a full position; and it reuses the E1
# SERVER-DERIVED idempotency key (ADR-PC-036 / bd babelstone-6cpq.1) — the tool/client supply no key, so
# a repeat firing of the SAME occurrence dedupes to one money leg at the engine's number-pinned key.


async def test_pay_installment_pays_via_the_engine_command_surface() -> None:
    fake = _FakeLoanEngine(term_months=2)
    server.set_engine(fake)

    result = await server.pay_installment(
        loan_id="loan-1",
        collection_account_ref="acct-ref-001",
        ctx=_write_ctx(client_id="CLI-LOAN-1"),
    )

    # The tool maps to the engine loan command surface with the opaque collection account ref, and
    # forwards the gateway-attested caller (§P3) — never a tool argument.
    assert fake.installment_loan == "loan-1"
    assert fake.installment_collection_ref == "acct-ref-001"
    assert fake.client_id_forwarded == "CLI-LOAN-1"
    # Installment 1 of 2 paid: one money leg, the loan stays ACTIVE, the commit_sequence comes back.
    assert result.loan_id == "loan-1"
    assert result.status == "ACTIVE"
    assert result.commit_sequence == 1
    assert fake.money_legs == 1


async def test_pay_installment_repeat_for_same_occurrence_appends_no_second_money_leg() -> None:
    # (b) The dedup story at the MCP boundary: the tool supplies NO key, so a repeat firing of the SAME
    # occurrence (the authoritative fold has not advanced) is deduped by the engine's SERVER-DERIVED
    # number-pinned key (ADR-PC-036 §Decision 1+3) — one money leg, the original commit_sequence replayed.
    fake = _FakeLoanEngine(term_months=2)
    fake.freeze_occurrence = True  # the fold does not advance — the SAME occurrence is re-fired
    server.set_engine(fake)
    ctx = _write_ctx()

    first = await server.pay_installment(
        loan_id="loan-1", collection_account_ref="acct-ref-001", ctx=ctx
    )
    second = await server.pay_installment(
        loan_id="loan-1", collection_account_ref="acct-ref-001", ctx=ctx
    )

    assert fake.installment_attempts == 2  # both calls reached the engine command surface
    assert fake.money_legs == 1  # but only ONE money leg — the re-fire deduped
    assert first.commit_sequence == second.commit_sequence  # the original outcome replayed


async def test_pay_installment_pays_both_installments_and_settles() -> None:
    # Two distinct occurrences (the fold advances): the final installment clears the balance and the loan
    # folds to SETTLED — two money legs, monotonic commit sequences.
    fake = _FakeLoanEngine(term_months=2)
    server.set_engine(fake)
    ctx = _write_ctx()

    first = await server.pay_installment(
        loan_id="loan-1", collection_account_ref="acct-ref-001", ctx=ctx
    )
    second = await server.pay_installment(
        loan_id="loan-1", collection_account_ref="acct-ref-001", ctx=ctx
    )

    assert first.status == "ACTIVE"
    assert second.status == "SETTLED"
    assert fake.money_legs == 2
    assert (first.commit_sequence, second.commit_sequence) == (1, 2)


async def test_pay_installment_requires_the_write_scope() -> None:
    # The installment is a WRITE (deposits:write) — a read-only token cannot reach it, and the rejection
    # happens BEFORE the engine is touched (ADR-IC-010 §P4).
    fake = _FakeLoanEngine()
    server.set_engine(fake)

    with pytest.raises(McpError):
        await server.pay_installment(
            loan_id="loan-1", collection_account_ref="acct-ref-001", ctx=_read_ctx()
        )

    assert fake.installment_attempts == 0


async def test_pay_installment_with_fresh_sca_settles_without_prompting() -> None:
    # The caller already holds fresh SCA → the engine collects first try → no step-up prompt fires.
    fake = _FakeLoanEngine(sca_required_calls=0)
    server.set_engine(fake)
    ctx = _FakeContext(
        client_id="CLI-LOAN-1",
        scope="deposits:write",
        elicit_url_result=AcceptedUrlElicitation(),
    )

    result = await server.pay_installment(
        loan_id="loan-1", collection_account_ref="acct-ref-001", ctx=ctx
    )

    assert ctx.elicit_url_called is False
    assert fake.installment_attempts == 1
    assert result.status == "ACTIVE"


async def test_pay_installment_422_steps_up_then_retries_and_settles() -> None:
    # The engine demands SCA (422), the human completes the step-up (accept), the retry collects.
    fake = _FakeLoanEngine(sca_required_calls=1)
    server.set_engine(fake)
    ctx = _FakeContext(
        client_id="CLI-LOAN-1",
        scope="deposits:write",
        elicit_url_result=AcceptedUrlElicitation(),
    )

    result = await server.pay_installment(
        loan_id="loan-1", collection_account_ref="acct-ref-001", ctx=ctx
    )

    assert ctx.elicit_url_called is True
    assert fake.installment_attempts == 2  # initial 422 + the post-step-up retry
    assert result.status == "ACTIVE"
    assert fake.money_legs == 1
    # The URL carries only a stable op code + the elicitation UUID — never the loan id (no business id leak).
    _msg, url, _eid = ctx.elicit_url_args  # type: ignore[misc]
    assert "operation=PAY_INSTALLMENT" in url
    assert "loan-1" not in url


async def test_pay_installment_step_up_declined_raises_mcp_error_without_collecting() -> None:
    fake = _FakeLoanEngine(sca_required_calls=1)
    server.set_engine(fake)
    ctx = _FakeContext(
        client_id="CLI-LOAN-1",
        scope="deposits:write",
        elicit_url_result=DeclinedElicitation(),
    )

    with pytest.raises(McpError) as exc:
        await server.pay_installment(
            loan_id="loan-1", collection_account_ref="acct-ref-001", ctx=ctx
        )

    # Declining aborts before any retry; nothing collected, the error is static (no business id leak).
    assert fake.money_legs == 0
    assert fake.installment_attempts == 1  # only the initial 422; no retry after a decline
    assert "loan-1" not in exc.value.error.message


async def test_pay_installment_retry_still_422_raises_mcp_error_never_collects() -> None:
    # The agent "accepted" but never obtained a genuinely refreshed token → the retry is STILL 422. The
    # tool surfaces an McpError; it never collects on the agent's word (the bypass-resistance invariant).
    fake = _FakeLoanEngine(sca_required_calls=2)
    server.set_engine(fake)
    ctx = _FakeContext(
        client_id="CLI-LOAN-1",
        scope="deposits:write",
        elicit_url_result=AcceptedUrlElicitation(),
    )

    with pytest.raises(McpError) as exc:
        await server.pay_installment(
            loan_id="loan-1", collection_account_ref="acct-ref-001", ctx=ctx
        )

    assert fake.money_legs == 0
    assert fake.installment_attempts == 2  # initial 422 + one retry, both refused
    assert "loan-1" not in exc.value.error.message
