"""Human-in-the-loop elicitation machinery for the secured MCP agent channel (Epic J.4, ar1y).

In plain English: sometimes a tool needs to pause and ask the human a question before it acts —
either a small structured form ("you picked periodic interest, confirm?") or a "go to this URL"
hand-off the bank controls (the vehicle for a strong-customer-authentication re-challenge). This
module is the thin, tested wrapper over the MCP SDK's two elicitation modes. It does one job: turn
the SDK's three-way accept/decline/cancel result into a clean signal — the validated answer on
accept, or a single ``ElicitationAborted`` exception the calling tool catches and surfaces as an
``McpError`` on decline/cancel.

Formally: this realises ADR-IC-010 §P8 (URL mode for irreversible operations, form mode for
non-irreversible parameter clarifications) and Document 11 §Human-in-the-Loop. Two helpers:

  * ``elicit_form_clarification`` — form mode (``ctx.elicit``), for confirming a non-irreversible
    choice. Returns the validated schema instance on accept.
  * ``elicit_url_stepup`` — URL mode (``ctx.elicit_url``), the machinery for step-up SCA on an
    irreversible operation. Returns ``True`` on accept (the human consented to navigate).

NO-PII INVARIANT (ADR-PC-004 §P2, Document 11 §"prompt injection via bank-returned content"): the
``message`` handed to either helper MUST be a static, generic string — never an f-string
interpolated from engine response data or tool arguments. The two module-level message constants
(``_PERIODIC_CONFIRM_MSG`` / ``_STEPUP_MSG``) are the canonical generic prompts; callers reuse them.
The only dynamic content allowed in a URL is a stable operation code and an ``elicitation_id`` UUID
(which is NOT a business identifier). No deposit id, client id, IBAN, or amount ever reaches the
elicitation channel — identity stays the gateway-attested ``X-Client-Id`` (Document 11), and the
prompt the agent sees carries only stable codes and generic text.

WHAT THIS MODULE DELIBERATELY DOES NOT DECIDE — the step-up SCA fork (flagged for the maintainer):
how a money-mover *detects* that fresh SCA is needed, and how the post-SCA fresh token/proof
*re-enters* the tool call, are genuine security-flow decisions that touch the saga orchestrator
(ADR-IC-010 §P8's note: "realised by the saga orchestrator") and possibly a Kong-level SCA gate on
the ``/mcp`` route (ADR-IC-006 §P2 — present on the constitute REST route, ABSENT on ``/mcp``). This
module ships the elicitation MACHINERY only; it does not invent that gate. See ``server.py``'s
maintainer-flag comments on the money-mover tools.
"""

from __future__ import annotations

from mcp.server.elicitation import AcceptedElicitation, AcceptedUrlElicitation
from mcp.shared.exceptions import McpError
from mcp.types import INVALID_PARAMS, ErrorData
from pydantic import BaseModel, Field


class ElicitationAborted(Exception):
    """Raised when the human declines or cancels an elicitation.

    Collapses the SDK's two non-accept outcomes (``DeclinedElicitation`` / ``CancelledElicitation``)
    into one signal the calling tool catches and re-raises as an ``McpError`` with a static,
    PII-free message. A tool MUST NOT proceed to an engine command after this is raised.
    """


class PeriodicInterestConfirmation(BaseModel):
    """Form-mode schema (ADR-IC-010 §P8): confirm a PERIODIC interest selection.

    Single ``bool`` field — a primitive, so the SDK's ``_validate_elicitation_schema`` accepts it
    (no nested model, no financial data). This is the non-irreversible parameter clarification §P8
    reserves form mode for: confirming the customer means "coupons paid periodically", not the
    AT_MATURITY default. It carries NO money and NO identifiers.
    """

    confirmed: bool = Field(
        description="True if the human confirms periodic (coupon) interest payments."
    )


# Static, generic, PII-free human-facing prompts (the no-PII invariant). Never interpolate engine
# response data or tool arguments into these — that is the whole point of pinning them here.
_PERIODIC_CONFIRM_MSG = (
    "You selected periodic interest payments. Please confirm: do you want coupons paid "
    "periodically to your current account, rather than all interest at maturity?"
)
_STEPUP_MSG = (
    "This operation requires additional authentication. Please complete the verification at the "
    "provided URL in your bank-controlled context, then retry."
)


async def elicit_form_clarification(
    ctx: object, message: str, schema: type[BaseModel]
) -> BaseModel:
    """Form-mode elicitation: ask the human a structured question, return the validated answer.

    Calls ``ctx.elicit(message, schema)``. On accept, returns the validated ``schema`` instance. On
    decline or cancel, raises ``ElicitationAborted`` (the caller catches it and surfaces an
    ``McpError`` — it MUST NOT proceed to the engine).

    ``message`` must be a static, generic string (the no-PII invariant); ``schema`` must contain
    only primitive field types — the SDK raises ``TypeError`` at elicit time otherwise, which this
    helper deliberately lets surface (a schema-shape bug, not a runtime user outcome).
    """
    result = await ctx.elicit(message, schema)  # type: ignore[attr-defined]
    if isinstance(result, AcceptedElicitation):
        return result.data
    raise ElicitationAborted()


async def elicit_url_stepup(
    ctx: object, message: str, url: str, elicitation_id: str
) -> bool:
    """URL-mode elicitation: direct the human to a bank-controlled URL, return their consent.

    The machinery for step-up SCA on an irreversible operation (ADR-IC-010 §P8). Calls
    ``ctx.elicit_url(message, url, elicitation_id)``. Returns ``True`` on accept (the human
    consented to navigate out-of-band). On decline or cancel, raises ``ElicitationAborted``.

    ``message`` must be a static, generic string; ``url`` may contain ONLY a stable operation code
    and the ``elicitation_id`` UUID (never a deposit id, client id, IBAN, or amount). The actual SCA
    interaction happens out-of-band in a bank-controlled context — the agent is not in the trust
    path for the confirmation (Document 11 §Human-in-the-Loop).
    """
    result = await ctx.elicit_url(message, url, elicitation_id)  # type: ignore[attr-defined]
    if isinstance(result, AcceptedUrlElicitation):
        return True
    raise ElicitationAborted()


def aborted_error(message: str) -> McpError:
    """Build the ``McpError`` a tool raises when an elicitation is aborted (decline/cancel).

    ``message`` is a static, PII-free string supplied by the calling tool. Centralised here so the
    error code (``INVALID_PARAMS``) is consistent with the rest of the MCP edge's client-fault model
    (see ``auth.py``).
    """
    return McpError(ErrorData(code=INVALID_PARAMS, message=message))
