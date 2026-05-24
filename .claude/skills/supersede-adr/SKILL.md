---
name: supersede-adr
description: >-
  Supersede an Accepted ADR with a new one (ADR-PC-000 §D5) when the decision itself
  is reversed or replaced. Creates the superseding ADR (via new-adr) with a back-link
  to the old one, flips the old ADR's Status to "Superseded by …", and updates the
  README index. Use when a decision changes wholesale — not for additive
  clarifications (use amend-adr for those).
---

# supersede-adr — replace an Accepted decision on the record

The wholesale-reversal half of the explicit-drift gate ([ADR-PC-020 §D3/§P9](docs/product-management/product_concepts/adrs/ADR-PC-020-llm-toolchain-and-conformance-governance.md)).
When implementation (or new information) shows an Accepted decision is *wrong* — not
just incomplete — the replacement is a **new ADR**, and the old one is retired on the
record, never deleted and never silently overwritten.

## Supersede or amend?

Ask: *does the original `## Decision` still hold?*

- **No — it's reversed/replaced** → supersede (this skill).
- **Yes — you're only adding to it** → use `amend-adr` (a dated, appended amendment).

## Procedure

1. **Confirm the target is `Accepted`** (a `Proposed` draft is just edited; a
   `Superseded` ADR is already dead — supersede the live one).

2. **Create the superseding ADR with the `new-adr` skill.** It runs the disk+bd dual
   number-check, picks the shape, and scaffolds the skeleton. Then, in the new ADR:
   - Add a front-matter row: `| Supersedes | [ADR-PC-OLD](./ADR-PC-OLD-….md) |`.
   - In **Context**, summarise what the old decision was, what changed, and why the
     reversal is warranted (this is the audit trail).

3. **Flip the old ADR's Status — and only the Status.** Edit *just* the status line:
   ```
   | Status | Superseded by [ADR-PC-NEW](./ADR-PC-NEW-….md) |
   ```
   This is the **one** in-place edit to an Accepted ADR that §D5 sanctions. Use a
   **targeted `Edit`** of the status line, **not** a `Write`/full-overwrite — the
   `adr-immutability.sh` hook keys on changes to the `## Decision` *body*; a status-line
   edit that leaves the Decision text untouched is legitimate. (A full-file `Write`
   trips the hook's "cannot diff → warn" path. Don't.) Optionally add a one-line note
   directly under the old ADR's front-matter table: `> Superseded by [ADR-PC-NEW](…) —
   see there for the current decision.`

4. **Leave the old ADR in the folder.** Superseded ADRs are kept as evidence the option
   was decided and later replaced; readers follow the link forward.

5. **Update the [README index](docs/product-management/product_concepts/adrs/README.md)** —
   add the new ADR's row, and update the old row's *Chosen / Decision* cell to note it's
   superseded (e.g. prefix `**[Superseded by ADR-PC-NEW]**`).

6. **Carry commitments forward.** If the old ADR governed rows in the
   [commitment catalogue](docs/product-management/product_concepts/adrs/commitment-catalogue.md),
   repoint each row's governing-source to the new ADR (the catalogue is the SoT). A code
   anchor to the now-superseded ADR is a finding the `spec-coverage-check.sh` checker
   will flag — update those anchors to the new ADR in the same change.

7. **Ride it with the code in the same PR**; the PR body's "ADRs touched/honoured"
   section names the supersession ([ADR-PC-020 §P1/§D3](docs/product-management/product_concepts/adrs/ADR-PC-020-llm-toolchain-and-conformance-governance.md)).

## Guardrails

- Supersede = the Decision is replaced. Additive change → `amend-adr` instead.
- Never delete the old ADR; never overwrite its Decision. Flip its Status, link forward.
- Edit only the old ADR's Status line in place (targeted `Edit`, never `Write`).
- New numbers are never reused — `new-adr`'s dual-check handles this.
