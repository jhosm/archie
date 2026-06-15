"""Contract tests for the MCP resource surface — the deposit-position resource template.

In plain English: these tests pin down the one read-oriented MCP resource the agent channel
exposes — ``bank://deposits/{deposit_id}`` — and the scope rule that guards it. A host attaches a
specific deposit as ambient context; reading it touches deposit data, so it mirrors the
``get_deposit`` tool's ``deposits:read`` scope. The body must be JSON and must carry no PII.

Formally: this is the resource-layer half of ADR-IC-010 §A1 (resource = host-attached context) and
§A3/§P4 (read/write tiering keys on SCOPE — a read resource requires ``deposits:read``). The resource
function reads the gateway-attested ``X-Client-Id`` / ``X-OAuth-Scope`` from the request headers,
never from the URI or an argument (Document 11 hallucinated-parameters rule). No PII in the body
(Document 10 Principle 3 / ADR-PC-004 §P2).
"""

from __future__ import annotations

import json
from typing import Any

import pytest
from mcp.shared.exceptions import McpError

from babelstone_mcp import resources, server
from babelstone_mcp.auth import (
    DEPOSITS_READ,
    RESOURCE_SCOPES,
    AuthContext,
    check_resource_scope,
)

# Reuse the in-process Context + fake engine harness from the tool contract tests.
from tests.test_server import _FakeContext, _FakeEngine, _read_ctx, _write_ctx


# --- auth.py additions: RESOURCE_SCOPES registry + check_resource_scope --------------------


def test_resource_scopes_covers_deposit_position_resource() -> None:
    # The registry maps the one resource to the reserved read scope (§A3/§P4).
    assert RESOURCE_SCOPES["deposit_position_resource"] == DEPOSITS_READ


def test_check_resource_scope_deposits_read_passes() -> None:
    auth = AuthContext(client_id="C", scopes=frozenset({DEPOSITS_READ}))
    # No exception: a read token may read a read resource.
    check_resource_scope(auth, "deposit_position_resource")


def test_check_resource_scope_write_only_raises() -> None:
    auth = AuthContext(client_id="C", scopes=frozenset({"deposits:write"}))
    with pytest.raises(McpError) as ei:
        check_resource_scope(auth, "deposit_position_resource")
    assert "Insufficient scope" in str(ei.value)


def test_check_resource_scope_unknown_resource_raises() -> None:
    auth = AuthContext(client_id="C", scopes=frozenset({DEPOSITS_READ}))
    with pytest.raises(McpError) as ei:
        check_resource_scope(auth, "nonexistent")
    assert "Unknown resource" in str(ei.value)


# --- resource template registration -------------------------------------------------------


async def test_deposit_resource_template_registered() -> None:
    templates = await server.mcp.list_resource_templates()
    uri_templates = [t.uriTemplate for t in templates]
    assert "bank://deposits/{deposit_id}" in uri_templates


async def test_deposit_resource_template_mime_type_is_json() -> None:
    templates = await server.mcp.list_resource_templates()
    by_uri = {t.uriTemplate: t for t in templates}
    assert by_uri["bank://deposits/{deposit_id}"].mimeType == "application/json"


# --- resource read: engine plumbing + scope + identity + no-PII ----------------------------


async def test_deposit_resource_read_calls_engine_and_returns_json_body() -> None:
    fake = _FakeEngine()
    server.set_engine(fake)

    body = await resources.deposit_position_resource(
        deposit_id="d-42", ctx=_read_ctx(client_id="CLI-ATTESTED-99")
    )

    # The deposit_id from the URI path reached the engine read.
    assert fake.position_requested == "d-42"
    # The resource path is eventually consistent — no read-your-writes barrier (no min_sequence).
    assert fake.min_sequence_requested is None
    # The gateway-attested caller is forwarded to the engine (§P3), exactly like get_deposit.
    assert fake.client_id_forwarded == "CLI-ATTESTED-99"

    parsed: dict[str, Any] = json.loads(body)
    assert parsed["deposit_id"] == "d-42"
    assert parsed["principal_cents"] == 1_000_000
    assert parsed["tan_basis_points"] == 300
    assert parsed["lifecycle"] == "Active"


async def test_deposit_resource_read_no_pii_in_body() -> None:
    fake = _FakeEngine()
    server.set_engine(fake)

    body = await resources.deposit_position_resource(deposit_id="d-42", ctx=_read_ctx())
    parsed = json.loads(body)

    # No identity-bearing PII in the resource body (Document 10 Principle 3 / ADR-PC-004 §P2).
    for forbidden in ("client_name", "nif", "iban", "email", "client_id"):
        assert forbidden not in parsed


async def test_deposit_resource_requires_deposits_read_scope() -> None:
    fake = _FakeEngine()
    server.set_engine(fake)

    # A write-only token may not read the read resource (§A3 — scope, not method).
    with pytest.raises(McpError) as ei:
        await resources.deposit_position_resource(deposit_id="d-42", ctx=_write_ctx())
    assert "Insufficient scope" in str(ei.value)
    # The scope check fails BEFORE the engine is touched.
    assert fake.position_requested is None


async def test_deposit_resource_missing_x_client_id_raises() -> None:
    fake = _FakeEngine()
    server.set_engine(fake)

    # No gateway-attested identity → fail-closed before the engine is touched.
    ctx = _FakeContext(client_id="", scope="deposits:read")
    with pytest.raises(McpError):
        await resources.deposit_position_resource(deposit_id="d-42", ctx=ctx)
    assert fake.position_requested is None
