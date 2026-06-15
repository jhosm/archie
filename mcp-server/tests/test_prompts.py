"""Contract tests for the MCP prompt surface — the two vetted agent-workflow templates.

In plain English: prompts are reusable, bank-vetted procedure templates the agent can render to
know the correct multi-step sequence for a common operation (open a deposit; review a deposit near
maturity). These tests pin down that both prompts register, that they render the arguments the
caller passes in, that they carry no PII, and that rendering needs no auth — a prompt is pure text,
not a data read.

Formally: prompts/get returns a rendered ``GetPromptResult`` (ADR-IC-010 §A1). No scope guard:
prompts make no engine call and carry no PII (Document 10 Principle 3 / ADR-PC-004 §P2). Requiring
no scope is also necessary for discoverability — a read-only token must still be able to enumerate
and render prompts to know how to proceed.
"""

from __future__ import annotations

from babelstone_mcp import server


def _text(result: object) -> str:
    """Concatenate the rendered text of every message in a GetPromptResult."""
    parts: list[str] = []
    for msg in result.messages:  # type: ignore[attr-defined]
        content = msg.content
        parts.append(content.text if hasattr(content, "text") else str(content))
    return "\n".join(parts)


# --- registration --------------------------------------------------------------------------


async def test_prompts_list_includes_constitute_term_deposit() -> None:
    prompts = await server.mcp.list_prompts()
    assert "constitute_term_deposit" in {p.name for p in prompts}


async def test_prompts_list_includes_review_upcoming_maturities() -> None:
    prompts = await server.mcp.list_prompts()
    assert "review_upcoming_maturities" in {p.name for p in prompts}


# --- constitute_term_deposit rendering -----------------------------------------------------

_CONSTITUTE_ARGS = {
    "product_id": "dpz_pt_12m",
    "principal_cents": 100000,
    "term_days": 365,
    "start_date": "2026-06-15",
    "funding_account": "PT50-DDA-001",
}


async def test_constitute_prompt_renders_product_id() -> None:
    result = await server.mcp.get_prompt("constitute_term_deposit", _CONSTITUTE_ARGS)
    assert "dpz_pt_12m" in _text(result)


async def test_constitute_prompt_renders_principal_cents() -> None:
    result = await server.mcp.get_prompt("constitute_term_deposit", _CONSTITUTE_ARGS)
    assert "100000" in _text(result)


async def test_constitute_prompt_renders_funding_account() -> None:
    result = await server.mcp.get_prompt("constitute_term_deposit", _CONSTITUTE_ARGS)
    assert "PT50-DDA-001" in _text(result)


async def test_constitute_prompt_mentions_no_client_id_argument() -> None:
    # The prompt must instruct the agent NOT to supply a client_id (Document 11 — identity is
    # gateway-attested, never an argument).
    result = await server.mcp.get_prompt("constitute_term_deposit", _CONSTITUTE_ARGS)
    assert "Do NOT supply a client_id" in _text(result)


# --- review_upcoming_maturities rendering --------------------------------------------------

_REVIEW_ARGS = {"deposit_id": "d-99", "today": "2026-07-01"}


async def test_review_prompt_renders_deposit_id_and_today() -> None:
    result = await server.mcp.get_prompt("review_upcoming_maturities", _REVIEW_ARGS)
    text = _text(result)
    assert "d-99" in text
    assert "2026-07-01" in text


async def test_review_prompt_warns_maturation_is_irreversible() -> None:
    result = await server.mcp.get_prompt("review_upcoming_maturities", _REVIEW_ARGS)
    assert "irreversible" in _text(result).lower()


# --- no-PII + no-auth -----------------------------------------------------------------------


async def test_prompts_carry_no_pii() -> None:
    # The rendered templates contain only categorical placeholders, no identity-bearing PII.
    constitute = _text(await server.mcp.get_prompt("constitute_term_deposit", _CONSTITUTE_ARGS))
    review = _text(await server.mcp.get_prompt("review_upcoming_maturities", _REVIEW_ARGS))
    for forbidden in ("nif", "iban", "@"):
        assert forbidden.lower() not in constitute.lower()
        assert forbidden.lower() not in review.lower()


async def test_prompts_need_no_auth_context() -> None:
    # Both prompts render with no X-Client-Id / X-OAuth-Scope headers in scope — prompts are pure
    # templates, not data reads, so they require no scope guard (necessary for discoverability).
    constitute = await server.mcp.get_prompt("constitute_term_deposit", _CONSTITUTE_ARGS)
    review = await server.mcp.get_prompt("review_upcoming_maturities", _REVIEW_ARGS)
    assert constitute.messages
    assert review.messages
