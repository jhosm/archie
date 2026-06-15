"""Spec-first tests for the MCP human-in-the-loop elicitation helpers (Epic J.4, bd babelstone-ar1y).

In plain English: our agent channel can pause mid-tool-call and ask the human a question — either a
small structured form ("you picked periodic interest, confirm?") or a "go to this URL" hand-off the
bank controls (the vehicle for a strong-customer-authentication re-challenge). These tests pin the
thin machinery that wraps the MCP SDK's two elicitation modes: it returns the validated answer on
accept and raises a single clean ``ElicitationAborted`` on decline/cancel, and it never lets customer
data (PII) leak into the prompt the agent sees.

Formally: this exercises ADR-IC-010 §P8 (URL mode for irreversible operations, form mode for
non-irreversible parameter clarifications) and Document 11 §Human-in-the-Loop. The no-PII guard
realises ADR-PC-004 §P2 / Document 11 §"prompt injection via bank-returned content" — elicitation
messages carry only stable codes and generic text, never a deposit id, client id, IBAN, or amount.
"""

from __future__ import annotations

import pytest
from mcp.server.elicitation import (
    AcceptedElicitation,
    AcceptedUrlElicitation,
    CancelledElicitation,
    DeclinedElicitation,
)

from babelstone_mcp.elicitation import (
    PeriodicInterestConfirmation,
    _PERIODIC_CONFIRM_MSG,
    _STEPUP_MSG,
    ElicitationAborted,
    elicit_form_clarification,
    elicit_url_stepup,
)


class _ElicitingFakeContext:
    """A Context stand-in whose ``elicit`` / ``elicit_url`` return pre-seeded results.

    The real ``Context.elicit`` / ``Context.elicit_url`` call through the live MCP session; in tests
    we inject the three-way SDK result directly so accept/decline/cancel paths are exercised without a
    transport. Records the message it was handed so the no-PII assertion can inspect it.
    """

    def __init__(
        self,
        *,
        elicit_result: object | None = None,
        elicit_url_result: object | None = None,
    ) -> None:
        self._elicit_result = elicit_result
        self._elicit_url_result = elicit_url_result
        self.elicit_called_with: tuple[str, type] | None = None
        self.elicit_url_called_with: tuple[str, str, str] | None = None

    async def elicit(self, message: str, schema: type) -> object:
        self.elicit_called_with = (message, schema)
        return self._elicit_result

    async def elicit_url(self, message: str, url: str, elicitation_id: str) -> object:
        self.elicit_url_called_with = (message, url, elicitation_id)
        return self._elicit_url_result


# --- form mode --------------------------------------------------------------------------------


async def test_elicit_form_clarification_accept_returns_validated_data() -> None:
    ctx = _ElicitingFakeContext(
        elicit_result=AcceptedElicitation(
            data=PeriodicInterestConfirmation(confirmed=True)
        )
    )

    result = await elicit_form_clarification(
        ctx, _PERIODIC_CONFIRM_MSG, PeriodicInterestConfirmation
    )

    assert isinstance(result, PeriodicInterestConfirmation)
    assert result.confirmed is True
    # The helper hands the SDK exactly the message + schema it was given (no rewriting).
    assert ctx.elicit_called_with == (_PERIODIC_CONFIRM_MSG, PeriodicInterestConfirmation)


async def test_elicit_form_clarification_decline_raises_elicitation_aborted() -> None:
    ctx = _ElicitingFakeContext(elicit_result=DeclinedElicitation())

    with pytest.raises(ElicitationAborted):
        await elicit_form_clarification(
            ctx, _PERIODIC_CONFIRM_MSG, PeriodicInterestConfirmation
        )


async def test_elicit_form_clarification_cancel_raises_elicitation_aborted() -> None:
    ctx = _ElicitingFakeContext(elicit_result=CancelledElicitation())

    with pytest.raises(ElicitationAborted):
        await elicit_form_clarification(
            ctx, _PERIODIC_CONFIRM_MSG, PeriodicInterestConfirmation
        )


# --- URL mode ---------------------------------------------------------------------------------


async def test_elicit_url_stepup_accept_returns_true() -> None:
    ctx = _ElicitingFakeContext(elicit_url_result=AcceptedUrlElicitation())

    result = await elicit_url_stepup(
        ctx, _STEPUP_MSG, "https://example.test/sca/stepup?operation=MATURE_DEPOSIT", "elicit-1"
    )

    assert result is True
    assert ctx.elicit_url_called_with == (
        _STEPUP_MSG,
        "https://example.test/sca/stepup?operation=MATURE_DEPOSIT",
        "elicit-1",
    )


async def test_elicit_url_stepup_decline_raises_elicitation_aborted() -> None:
    ctx = _ElicitingFakeContext(elicit_url_result=DeclinedElicitation())

    with pytest.raises(ElicitationAborted):
        await elicit_url_stepup(ctx, _STEPUP_MSG, "https://example.test/sca", "elicit-2")


async def test_elicit_url_stepup_cancel_raises_elicitation_aborted() -> None:
    ctx = _ElicitingFakeContext(elicit_url_result=CancelledElicitation())

    with pytest.raises(ElicitationAborted):
        await elicit_url_stepup(ctx, _STEPUP_MSG, "https://example.test/sca", "elicit-3")


# --- schema + no-PII structural guards --------------------------------------------------------


def test_periodic_confirmation_schema_is_primitive_only() -> None:
    # ADR-IC-010 §P8 form mode: the elicitation schema must contain only primitive field types, or
    # the SDK's _validate_elicitation_schema raises TypeError at elicit time. Confirm our schema's
    # single field is a bool (no nested model, no business/financial data).
    fields = PeriodicInterestConfirmation.model_fields
    assert set(fields) == {"confirmed"}
    assert fields["confirmed"].annotation is bool


def test_no_pii_in_elicitation_message_constants() -> None:
    # Structural guard on the no-PII invariant (ADR-PC-004 §P2, Document 11 anti-injection rule):
    # the human-facing message constants must carry only generic text — no deposit id, client id,
    # IBAN, email, or amount-looking digits.
    for msg in (_PERIODIC_CONFIRM_MSG, _STEPUP_MSG):
        lowered = msg.lower()
        assert "cli-" not in lowered  # client id prefix
        assert "dep-" not in lowered  # deposit id prefix
        assert "pt50" not in lowered  # IBAN prefix
        assert "iban" not in lowered
        assert "@" not in msg  # email
        # No standalone digit runs (amounts / ids) — generic instructional text only.
        assert not any(ch.isdigit() for ch in msg)
