"""The FastMCP server: ``constitute_deposit``, ``get_deposit``, ``mature_deposit``, ``pay_interest``.

All map 1:1 to the engine's HTTP API. Per ADR-IC-010's 2026-05-31 amendment, the tool/resource
axis is *control ownership* (model-invokable vs host-attached), not CQRS command/query — so a read
the agent fetches on demand is a tool, not a resource. ``constitute_deposit`` / ``mature_deposit`` /
``pay_interest`` are writes (engine commands); ``get_deposit`` is the read-only ``deposit_position``
projection. Each declares a structured return type, so the SDK publishes an ``outputSchema``
(ADR-IC-010 P6 — mandatory on every tool).

Auth (Epic J, babelstone-e50n): the secured edge fronts this server with Kong + OAuth. Every tool
reads the gateway-attested caller identity (``X-Client-Id``, derived from the OAuth token ``sub`` —
NEVER a tool argument; Document 11) from the request headers and enforces *scope-per-tool* (§P4) via
``check_tool_scope``: ``get_deposit`` needs ``deposits:read``, the writes need ``deposits:write``.
The authoritative ``aud`` re-check (§P3) and the public RFC 9728 metadata (§P2) live in ``app.py``.
§P8 elicitation on the irreversible writes is a deliberate follow-up (ar1y).
"""

from __future__ import annotations

import os

from mcp.server.fastmcp import Context, FastMCP
from pydantic import BaseModel, Field

from .auth import AuthContext, check_tool_scope
from .engine_client import EngineClient

mcp = FastMCP("babelstone-deposits")

_engine: EngineClient | None = None


def _authorize(ctx: Context, tool: str) -> AuthContext:
    """Build the gateway-attested ``AuthContext`` for this request and enforce the tool's scope.

    Reads ``X-Client-Id`` / ``X-OAuth-Scope`` off the Starlette request the Streamable-HTTP transport
    threads onto the request context (ADR-IC-010 §P3/§P4). The gateway set those headers from the
    OAuth token; the identity is NEVER taken from a tool argument (Document 11). Raises ``McpError``
    on a missing identity or insufficient scope before the engine is touched.
    """
    request = ctx.request_context.request
    auth = AuthContext.from_headers(request.headers)
    check_tool_scope(auth, tool)
    return auth


def engine() -> EngineClient:
    """The engine client, lazily built from ``BABELSTONE_ENGINE_URL`` (overridable in tests)."""
    global _engine
    if _engine is None:
        _engine = EngineClient(os.environ.get("BABELSTONE_ENGINE_URL", "http://localhost:8080"))
    return _engine


def set_engine(client: EngineClient) -> None:
    """Inject an engine client (tests / a configured host)."""
    global _engine
    _engine = client


class ConstituteDepositResult(BaseModel):
    """Structured tool output (ADR-IC-010 P6) — the assigned id and lifecycle state."""

    deposit_id: str = Field(description="The engine-assigned deposit id (UUID).")
    status: str = Field(description="Lifecycle state — ACTIVE on a constituted deposit.")
    commit_sequence: int = Field(
        description="The per-stream version this constitution committed (ADR-IC-005 §P3). Pass it as "
        "get_deposit's min_sequence to read your own write before the projector catches up."
    )


@mcp.tool()
async def constitute_deposit(
    product_id: str,
    role: str,
    principal_cents: int,
    term_days: int,
    start_date: str,
    funding_account: str,
    ctx: Context,
    interest_variant: str = "AT_MATURITY",
    auto_renewal_policy: str = "NONE",
    payment_period_months: int = 0,
) -> ConstituteDepositResult:
    """Constitute a term deposit. ``principal_cents`` and all money are integer cents (never a float).

    ``product_id`` is the variant the rate sheet prices (e.g. ``dpz_pt_12m_juros_venc``); ``role`` is
    the pricing role (e.g. ``standard``); ``start_date`` is ISO-8601 (YYYY-MM-DD). The resolved TAN
    is stamped by the engine from the active rate sheet — never supplied here.

    ``interest_variant`` is one of AT_MATURITY (interest + principal at maturity), PERIODIC (coupons
    paid out to the current account, principal at maturity), or ADVANCE (full-term interest at t=0).
    ``payment_period_months`` is required for PERIODIC — 1 (monthly) or 3 (quarterly), the only
    cadences priced — and is 0/omitted for AT_MATURITY and ADVANCE.

    Requires ``deposits:write`` (ADR-IC-010 §P4). The actor is the gateway-attested ``X-Client-Id``
    (OAuth ``sub``), never a tool argument (Document 11).
    """
    _authorize(ctx, "constitute_deposit")
    result = await engine().constitute(
        {
            "principal_cents": principal_cents,
            "product_id": product_id,
            "role": role,
            "term_days": term_days,
            "start_date": start_date,
            "interest_variant": interest_variant,
            "auto_renewal_policy": auto_renewal_policy,
            "funding_account": funding_account,
            "payment_period_months": payment_period_months,
        }
    )
    return ConstituteDepositResult(
        deposit_id=result["deposit_id"],
        status=result["status"],
        commit_sequence=result["commit_sequence"],
    )


class DepositPosition(BaseModel):
    """Structured tool output (ADR-IC-010 P6) — the ONE canonical deposit resource (ADR-IC-005).

    All money is integer cents (ADR-PC-010 §P1), never a float. The engine serves this from the fast
    denormalized read model by default and folds the event stream only for read-your-writes — the
    CQRS read/write split is the engine's internal business, not two shapes. ``last_sequence`` is the
    per-stream version this view reflects (thread it forward as ``min_sequence`` for monotonic reads);
    ``last_updated`` is the producing event's timestamp, for staleness display.
    """

    deposit_id: str = Field(description="The deposit id (UUID).")
    sor: str = Field(description="System of record — 'engine' for an engine-materialised deposit (ADR-PC-018 §6.2).")
    principal_cents: int = Field(description="Principal in integer cents.")
    tan_basis_points: int = Field(description="Resolved TAN in basis points, stamped by the engine.")
    rate_sheet_version_id: str = Field(description="Rate sheet version the TAN was resolved from (price/version key).")
    product_code: str = Field(description="Catalogue structural product code (which product); '' for pre-v794 deposits.")
    term_days: int = Field(description="Term length in days.")
    start_date: str = Field(description="ISO-8601 start date.")
    maturity_date: str = Field(description="ISO-8601 maturity date.")
    interest_variant: str = Field(description="Interest variant (AT_MATURITY, PERIODIC, or ADVANCE).")
    auto_renewal_policy: str = Field(description="Auto-renewal policy (e.g. NONE).")
    payment_period_months: int = Field(
        description="PERIODIC coupon cadence in months (1 monthly, 3 quarterly); 0 for AT_MATURITY/ADVANCE."
    )
    accrued_gross_interest_cents: int = Field(description="Gross interest accrued to date, cents.")
    withholding_to_date_cents: int = Field(description="Withholding tax accrued to date, cents.")
    net_interest_cents: int = Field(description="Net interest to date, cents.")
    total_payout_cents: int = Field(description="Total payout to date, cents.")
    coupons_paid: int = Field(description="PERIODIC coupons paid out so far (0 for AT_MATURITY/ADVANCE).")
    lifecycle: str = Field(description="Lifecycle state (e.g. Active, Matured).")
    last_sequence: int = Field(description="The per-stream version this view reflects (ADR-IC-005 §P3 read-your-writes barrier).")
    last_updated: str = Field(description="ISO-8601 timestamp of the producing event (for staleness display).")


@mcp.tool()
async def get_deposit(
    deposit_id: str, ctx: Context, min_sequence: int | None = None
) -> DepositPosition:
    """Read a term deposit's current state — the ONE canonical deposit resource (ADR-IC-005).

    ``deposit_id`` is the engine-assigned UUID returned by ``constitute_deposit``. Served from the
    fast denormalized read model by default. For read-your-writes (e.g. reading right after a
    constitute/mature), pass ``min_sequence`` = the ``commit_sequence`` that command returned: the
    engine then folds the event stream if the projection has not caught up, so you always see your own
    write. Money is integer cents; ``last_sequence`` on the result is the version served (thread it
    forward for monotonic reads).

    Requires ``deposits:read`` (ADR-IC-010 §P4) — the reserved read scope; a ``deposits:read`` token
    cannot reach the write tools.
    """
    _authorize(ctx, "get_deposit")
    return DepositPosition(**await engine().deposit_position(deposit_id, min_sequence))


@mcp.tool()
async def mature_deposit(deposit_id: str, ctx: Context) -> DepositPosition:
    """Mature (settle) a term deposit — runs accrual to term end and returns the matured position.

    ``deposit_id`` is the engine-assigned UUID. Returns the same ``DepositPosition`` shape with the
    interest fields now folded in (``accrued_gross_interest_cents``, ``withholding_to_date_cents``,
    ``net_interest_cents``, ``total_payout_cents``) and ``lifecycle`` = ``Matured``. Money is integer
    cents.

    Requires ``deposits:write`` (ADR-IC-010 §P4). Settlement is irreversible, so if the secured edge
    classes it under §P8 it gets ``elicitation/create`` confirmation — a deliberate follow-up (ar1y).
    """
    _authorize(ctx, "mature_deposit")
    return DepositPosition(**await engine().mature(deposit_id))


@mcp.tool()
async def pay_interest(deposit_id: str, ctx: Context) -> DepositPosition:
    """Pay one PERIODIC coupon on a term deposit — accrues the next coupon window, withholds tax on
    that one flow, pays the net to the current account, and returns the updated position.

    ``deposit_id`` is the engine-assigned UUID. Only an Active PERIODIC deposit pays coupons; the
    coupon window is derived by the engine from the deposit's schedule and the coupons already paid
    (not supplied here). Returns the same ``DepositPosition`` shape with the coupon's gross/withholding/
    net folded in and ``coupons_paid`` incremented; the final coupon is paid with the principal at
    maturity (use ``mature_deposit`` for that), so calling this once no intermediate coupon remains is
    rejected. Money is integer cents.

    Requires ``deposits:write`` (ADR-IC-010 §P4). Like ``mature_deposit``, the coupon settlement is
    irreversible; §P8 elicitation is a deliberate follow-up (ar1y).
    """
    _authorize(ctx, "pay_interest")
    return DepositPosition(**await engine().pay_interest(deposit_id))
