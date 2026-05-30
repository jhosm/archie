"""The FastMCP server: a ``constitute_deposit`` tool + a ``deposit_position`` resource.

The tool maps 1:1 to the engine's constitute command (ADR-IC-010: tools are commands); the
resource is the read-only ``deposit_position`` projection (resources are CQRS read models). The
tool declares a structured return type, so the SDK publishes an ``outputSchema`` (ADR-IC-010 P6).
Auth is deferred — this dev server hits the engine directly (Epic J adds OAuth/Kong).
"""

from __future__ import annotations

import os
from typing import Any

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


@mcp.resource("bank://deposits/{deposit_id}")
async def deposit_position(deposit_id: str) -> dict[str, Any]:
    """The folded ``deposit_position`` read model for a deposit (money as integer cents)."""
    return await engine().deposit_position(deposit_id)
