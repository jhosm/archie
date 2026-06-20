---
name: amend-adr
description: >-
  Append a dated amendment to an Accepted ADR (ADR-PC-000 §D5) WITHOUT editing its
  Decision in place — the explicit-drift gate's one-command companion. Use when a
  change reveals an Accepted ADR needs an additive clarification, refinement, or
  extra slot that does NOT reverse the decision. If the decision itself is being
  replaced or reversed, use supersede-adr instead.
---

# amend-adr — append a dated amendment to an Accepted ADR

The cheap, on-the-record half of the explicit-drift gate ([ADR-PC-020 §D3/§P9](docs/product-management/product_concepts/adrs/ADR-PC-020-llm-toolchain-and-conformance-governance.md)).
When implementation reveals an Accepted ADR is incomplete or needs clarifying, the
decision change rides in the **same change** as the code — as an appended, dated
amendment, never a silent in-place edit.

## Amend or supersede?

- **Amend** (this skill) — *additive*: clarify a slot, add a constraint, extend scope,
  add the Verifiable-commitments section, record a refinement. The original Decision
  stays true; you are adding to it.
- **Supersede** (`supersede-adr`) — the Decision itself is *reversed or replaced*. A new
  ADR with a new number; the old one's Status flips to `Superseded by …`.

If you're unsure, ask: *does the original `## Decision` still hold?* Yes → amend. No →
supersede.

## Procedure

1. **Check the target's Status.**
   - `Accepted` → amend (this skill).
   - `Proposed` → no amendment needed; edit the draft directly (the immutability hook
     only fires on Accepted).
   - `Superseded` → amend the **superseding** ADR instead, not the dead one.

2. **Do NOT touch the `## Decision` body.** The `adr-immutability.sh` PreToolUse hook
   warns and `adr-immutability-check.sh` CI **hard-fails** an in-place change to an
   Accepted Decision. The amendment is *appended*, leaving the original Decision text
   verbatim. (Editing the Status line to point at a supersession is the only allowed
   in-place touch — that's `supersede-adr`'s job, not this one.)

3. **Append the amendment block** at the end of the body — after Consequences/Residual
   risks, before any `## Cross-references` block. Follow ADR-PC-000's own house style
   (it carries two real amendments dated 2026-05-23 and 2026-05-24 — read them as the
   canonical shape):

   ```markdown
   ## Amendment — YYYY-MM-DD: <short title of what changed>

   <one paragraph: what implementation/decision revealed this, and why it's additive.>

   ### A<n> · <the specific change>
   <the new constraint / clarified slot / added section, §-anchored to what it refines.>

   ### A<n+1> · This amends the decision; it does not supersede this ADR
   <name the §D-sections that remain binding as written; state the amendment is
   appended to — not a revision of — them.>
   ```

   Use today's date from the environment (`date +%F`). Number the sub-points
   `A1`, `A2`, … continuing from any existing amendment.

4. **If the amendment changes a load-bearing commitment**, the
   [commitment catalogue](docs/product-management/product_concepts/adrs/commitment-catalogue.md)
   is the source of truth — update the row there (claim/gate/status), and make sure the
   ADR's Verifiable-commitments section still references the right Test ID. Don't
   restate the mutable fields in the ADR.

5. **Ride it with the code.** The amendment and the contradicting/clarified code land
   in the **same PR**, and the PR body's "ADRs touched/honoured" section names the ADR
   as amended ([ADR-PC-020 §P1/§D3](docs/product-management/product_concepts/adrs/ADR-PC-020-llm-toolchain-and-conformance-governance.md)).

## Guardrails

- Append only — never edit the Accepted `## Decision` in place.
- An amendment is *additive*; if you find yourself reversing the decision, stop and use
  `supersede-adr`.
- Date from the environment, not from memory.
- A deliberate, time-bounded gap (you're knowingly deferring conformance) is recorded in
  [04-open-questions](docs/product-management/product_concepts/04-open-questions.md),
  not as an amendment.
