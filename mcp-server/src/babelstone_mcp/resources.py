"""MCP resources for the term-deposit agent channel (Epic J.2, bd babelstone-2ep0).

In plain English: this registers ``bank://deposits/{deposit_id}`` as a host-attached resource
template. A host (an MCP client acting as the application layer) can attach a specific deposit as
ambient context for an agent session — e.g. pin the deposit the conversation is about so the agent
always sees its current state. The resource body is the same DepositPosition data the get_deposit
tool returns, served as JSON, with no PII.

Formally: a resource is host-controlled context (Document 11 / ADR-IC-010 §A1). The control-ownership
split (ADR-IC-010 2026-05-31 amendment) makes a template the right primitive for a host-pinned,
long-lived view — the on-demand agent read mid-reasoning stays the get_deposit tool. Scope:
``deposits:read`` (§A3/§P4 — read resources key on the reserved read scope, the tiering is on scope,
not on MCP method). The gateway-attested ``X-Client-Id`` is read from the request headers (never the
URI or an argument — Document 11) and forwarded to the engine for audit/ownership, exactly like the
get_deposit tool. No PII in the body (Document 10 Principle 3 / ADR-PC-004 §P2).
"""

from __future__ import annotations

import json

from mcp.server.fastmcp import Context

from .auth import AuthContext, check_resource_scope
from .server import engine, mcp


@mcp.resource(
    "bank://deposits/{deposit_id}",
    name="deposit_position_resource",
    description=(
        "The current position of a term deposit, as host-attached context. The body is a JSON object "
        "with the same fields as the get_deposit tool result (the DepositPosition shape: deposit_id, "
        "principal_cents, tan_basis_points, maturity_date, lifecycle, accrued_gross_interest_cents, "
        "etc.). Money is integer cents. Eventually consistent — served from the denormalized read "
        "model with no read-your-writes barrier; use the get_deposit tool with min_sequence when you "
        "need to read your own just-committed write. Requires deposits:read scope (ADR-IC-010 §P4/"
        "§A3). No PII in the body (Document 10 Principle 3)."
    ),
    mime_type="application/json",
)
async def deposit_position_resource(deposit_id: str, ctx: Context) -> str:
    """Return a term deposit's position as a JSON string (host-attached resource context).

    Reads the gateway-attested ``X-Client-Id`` / ``X-OAuth-Scope`` from the request headers (never
    from the URI or an argument — Document 11), enforces ``deposits:read`` (§P4/§A3), then forwards
    the attested caller to the engine read (§P3), exactly as the get_deposit tool does. No
    ``min_sequence`` — the resource is the eventually-consistent host-attached view (the control-
    ownership split: the host attaches it, the engine may serve a slightly lagged read model). The
    ``deposit_id`` comes from the URI path; the engine 404s an unknown or cross-customer id, so
    cross-customer access prevention lives in the engine, not this ACL — same as get_deposit.
    """
    request = ctx.request_context.request
    auth = AuthContext.from_headers(request.headers)
    check_resource_scope(auth, "deposit_position_resource")
    position = await engine().deposit_position(deposit_id, client_id=auth.client_id)
    return json.dumps(position)
