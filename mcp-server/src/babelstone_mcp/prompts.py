"""MCP prompts for the term-deposit agent channel (Epic J.2, bd babelstone-2ep0).

In plain English: prompts are reusable, bank-vetted procedure templates. They give the agent the
correct multi-step workflow for common operations (constituting a deposit, reviewing an upcoming
maturity) so the agent does not have to discover or invent the sequence. Prompts are pure text —
they make no engine call and carry no PII.

Formally: prompts/get returns a rendered list of messages (Document 11 / ADR-IC-010 §A1). No scope
required: prompts are templates, not data reads — no engine call, no PII (Document 10 Principle 3 /
ADR-PC-004 §P2; every placeholder is categorical, not identity-bearing). Carrying no scope guard is
also necessary for discoverability — a read-only token must still be able to enumerate and render
the prompts to know how to proceed (§A3 keys enforcement on scope, and a pure template touches no
scoped resource).
"""

from __future__ import annotations

from mcp.server.fastmcp.prompts.base import UserMessage

from .server import mcp


@mcp.prompt(
    name="constitute_term_deposit",
    description=(
        "Vetted procedure for constituting a term deposit on behalf of a customer. Instructs the "
        "agent to call constitute_deposit with the supplied parameters, confirm the deposit_id, then "
        "read the confirmed position with get_deposit using the commit_sequence token for "
        "read-your-writes. The tool-call step requires deposits:write scope; rendering this prompt "
        "requires no scope (it is a pure template)."
    ),
)
def constitute_term_deposit(
    product_id: str,
    principal_cents: int,
    term_days: int,
    start_date: str,
    funding_account: str,
    interest_variant: str = "AT_MATURITY",
) -> list[UserMessage]:
    """Prompt template for opening a term deposit — encodes the bank-vetted call sequence."""
    text = (
        "You are assisting a customer to open a term deposit. "
        "Follow this procedure exactly and do not deviate from it.\n\n"
        "Step 1 — Constitute the deposit.\n"
        "Call the constitute_deposit tool with these parameters:\n"
        f"  product_id: {product_id!r}\n"
        f"  principal_cents: {principal_cents}  (integer cents — do NOT convert to a decimal)\n"
        f"  term_days: {term_days}\n"
        f"  start_date: {start_date!r}  (ISO-8601 YYYY-MM-DD)\n"
        f"  funding_account: {funding_account!r}\n"
        f"  interest_variant: {interest_variant!r}\n"
        "Do NOT supply a client_id argument. The gateway attests the caller's identity from the "
        "OAuth token; a client_id argument would be ignored or rejected.\n\n"
        "Step 2 — Confirm to the customer.\n"
        "The tool returns a deposit_id (UUID) and a commit_sequence. Tell the customer: "
        '"Your term deposit has been submitted. Deposit reference: <deposit_id>."\n\n'
        "Step 3 — Read the confirmed position.\n"
        "Call get_deposit(deposit_id=<deposit_id>, min_sequence=<commit_sequence>) to read the "
        "confirmed deposit state. Present to the customer in plain language: the principal amount, "
        "the maturity date, the annual rate (tan_basis_points divided by 100 to express as a "
        "percentage), and the interest variant.\n\n"
        "All money is in integer cents throughout. Never convert to a float. Never add, subtract, or "
        "modify amounts the engine returns."
    )
    return [UserMessage(content=text)]


@mcp.prompt(
    name="review_upcoming_maturities",
    description=(
        "Vetted procedure for reviewing a term deposit approaching maturity and guiding the customer "
        "through their options (mature, renew, or defer). Instructs the agent to fetch the deposit "
        "state, summarise it, and present the available actions without executing any irreversible "
        "step until the customer explicitly confirms. Reading the deposit requires deposits:read; "
        "maturing it (option a) requires deposits:write. Rendering this prompt requires no scope."
    ),
)
def review_upcoming_maturities(
    deposit_id: str,
    today: str,
) -> list[UserMessage]:
    """Prompt template for reviewing a deposit near maturity — encodes the safe decision flow."""
    text = (
        "You are reviewing a term deposit that may be approaching maturity. "
        "Follow this procedure. Do NOT take irreversible action until the customer confirms.\n\n"
        "Step 1 — Fetch the deposit position.\n"
        f"Call get_deposit(deposit_id={deposit_id!r}) to fetch the current state.\n\n"
        "Step 2 — Summarise for the customer.\n"
        "Present the following fields in plain language:\n"
        "  - Principal: principal_cents (in euros: divide by 100)\n"
        "  - Maturity date: maturity_date\n"
        f"  - Days remaining as of {today}: compute maturity_date minus {today}\n"
        "  - Annual rate: tan_basis_points / 100 expressed as a percentage\n"
        "  - Accrued gross interest so far: accrued_gross_interest_cents\n"
        "  - Withholding tax accrued: withholding_to_date_cents\n"
        "  - Net interest so far: net_interest_cents\n"
        "  - Auto-renewal policy: auto_renewal_policy\n"
        "  - Interest variant: interest_variant\n\n"
        "Step 3 — Ask the customer what they would like to do.\n"
        "Present these options:\n"
        "  a) Mature the deposit on or after the maturity date. This calls mature_deposit and is "
        "IRREVERSIBLE — confirm explicitly before calling it.\n"
        "  b) Renew for the same or a new term. Ask for the new product_id, start_date, and any "
        "changed parameters, then after maturing the current deposit call constitute_deposit for "
        "the renewal.\n"
        "  c) Take no action now and review again later.\n\n"
        "Do NOT call mature_deposit unless the customer explicitly says they want option (a) and "
        "confirms the maturity date. Maturation is irreversible. All money is integer cents. Never "
        "convert to float."
    )
    return [UserMessage(content=text)]
