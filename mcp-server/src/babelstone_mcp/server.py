"""The FastMCP server: a ``constitute_deposit`` tool + a ``get_deposit`` tool.

Both map 1:1 to the engine's HTTP API. Per ADR-IC-010's 2026-05-31 amendment, the tool/resource
axis is *control ownership* (model-invokable vs host-attached), not CQRS command/query — so a read
the agent fetches on demand is a tool, not a resource. ``constitute_deposit`` is a write (engine
command); ``get_deposit`` is the read-only ``deposit_position`` projection. Both declare a structured
return type, so the SDK publishes an ``outputSchema`` (ADR-IC-010 P6 — mandatory on every tool).
Auth is deferred — this dev server hits the engine directly (Epic J adds OAuth/Kong; the read tool's
``deposits:read`` scope vs the write tools' ``deposits:write`` is where the gateway tiers them).
"""

from __future__ import annotations

import os

from mcp.server.fastmcp import FastMCP
from pydantic import BaseModel, Field

from .engine_client import EngineClient

mcp = FastMCP("babelstone-deposits")

_engine: EngineClient | None = None


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


@mcp.tool()
async def constitute_deposit(
    product_id: str,
    role: str,
    principal_cents: int,
    term_days: int,
    start_date: str,
    funding_account: str,
    interest_variant: str = "AT_MATURITY",
    auto_renewal_policy: str = "NONE",
) -> ConstituteDepositResult:
    """Constitute a term deposit. ``principal_cents`` and all money are integer cents (never a float).

    ``product_id`` is the variant the rate sheet prices (e.g. ``dpz_pt_12m_juros_venc``); ``role`` is
    the pricing role (e.g. ``standard``); ``start_date`` is ISO-8601 (YYYY-MM-DD). The resolved TAN
    is stamped by the engine from the active rate sheet — never supplied here.
    """
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
        }
    )
    return ConstituteDepositResult(deposit_id=result["deposit_id"], status=result["status"])


class DepositPosition(BaseModel):
    """Structured tool output (ADR-IC-010 P6) — the folded ``deposit_position`` read model.

    All money is integer cents (ADR-PC-010 §P1), never a float. The fields are the as-of-now fold
    of the deposit's events, not a maturity projection: ``accrued_*`` / ``*_payout`` stay at 0 until
    accrual or maturity events are applied.
    """

    deposit_id: str = Field(description="The deposit id (UUID).")
    principal_cents: int = Field(description="Principal in integer cents.")
    tan_basis_points: int = Field(description="Resolved TAN in basis points, stamped by the engine.")
    rate_sheet_version_id: str = Field(description="Rate sheet version the TAN was resolved from.")
    term_days: int = Field(description="Term length in days.")
    start_date: str = Field(description="ISO-8601 start date.")
    maturity_date: str = Field(description="ISO-8601 maturity date.")
    interest_variant: str = Field(description="Interest variant (e.g. AT_MATURITY).")
    auto_renewal_policy: str = Field(description="Auto-renewal policy (e.g. NONE).")
    accrued_gross_interest_cents: int = Field(description="Gross interest accrued to date, cents.")
    withholding_to_date_cents: int = Field(description="Withholding tax accrued to date, cents.")
    net_interest_cents: int = Field(description="Net interest to date, cents.")
    total_payout_cents: int = Field(description="Total payout to date, cents.")
    lifecycle: str = Field(description="Lifecycle state (e.g. Active, Matured).")


@mcp.tool()
async def get_deposit(deposit_id: str) -> DepositPosition:
    """Read a term deposit's current state — the folded ``deposit_position`` projection.

    ``deposit_id`` is the engine-assigned UUID returned by ``constitute_deposit``. Money is integer
    cents. This is the as-of-now event fold, not a maturity forecast (interest fields are 0 until
    accrual/maturity events land). Scoped ``deposits:read`` at the gateway (ADR-IC-010 §P4).
    """
    return DepositPosition(**await engine().deposit_position(deposit_id))
