---
name: new-adr
description: >-
  Scaffold a new Architectural Decision Record (ADR-PC for engine concerns, or
  ADR-IC for the shared/in-house integration estate). Runs the disk+bd dual
  number-check, picks the ADR-PC-000 shape (tool-selection vs contract-shape),
  writes the correct skeleton with cross-links per the location rules, seeds the
  Verifiable-commitments section as catalogue references, and adds the row to the
  namespace README index. Use whenever the user wants to add/create/draft an ADR
  or record a new architectural decision.
---

# new-adr — scaffold a conformant ADR

You create a new ADR that obeys the [ADR-PC-000](docs/product-management/product_concepts/adrs/ADR-PC-000-namespace-and-contract-shape-framework.md)
conventions and lands review-ready. An ADR is **`Proposed`** when you create it —
acceptance is a separate human step, never yours.

> An ADR (and its bd issue, if any) comes **before** the code it governs. This skill
> scaffolds the decision; the author fills the reasoning.

## Step 1 — Pick the namespace

| Namespace | For | Folder |
|---|---|---|
| **ADR-PC** | the product engine's *own* concerns — source of truth/state, config surface, runtime, boundary signal contracts, coexistence | `docs/product-management/product_concepts/adrs/` |
| **ADR-IC** | infrastructure *shared* across the bank's integration estate, **and the in-house estate** (orchestrator, outbox, MCP, notification, ACL — per [ADR-IC-013](docs/product-management/integration_concepts/adrs/ADR-IC-013-in-house-estate-build-and-repository-placement.md)) | `docs/product-management/integration_concepts/adrs/` |

The two number spaces are **independent** ([ADR-PC-000 §D1](docs/product-management/product_concepts/adrs/ADR-PC-000-namespace-and-contract-shape-framework.md)): `ADR-PC-005` and `ADR-IC-005` are unrelated and never aliased. Cross-references always carry the prefix.

## Step 2 — Dual number-check (disk + bd), within the chosen namespace only

This is mandatory — on-disk files and bd-planned ADRs share one number space *per
namespace*, and picking from one source alone has caused a real collision (bd memory
`adr-numbering-check-disk-and-bd`). Run **both**, show both, take `max + 1`:

```bash
# for ADR-PC (swap PC↔IC and the folder for an ADR-IC):
ls docs/product-management/product_concepts/adrs/ADR-PC-*.md
bd list --all | grep -iE 'ADR-PC'      # --all is REQUIRED: number reservations sit on CLOSED issues, which bare `bd list` hides
```

The next number is one past the highest seen in **either** list. Disk is the
authoritative record of *written* ADRs; `bd list --all` catches numbers *reserved or
planned* in the tracker (often on a closed issue — `bd list` without `--all` shows only
open issues and will miss them). Never reuse a number, even of a Rejected/Superseded
ADR. (A legacy bare-`ADR-NNN` epic shares the ADR-IC number space — if checking IC, also
skim `bd list --all | grep -iE 'ADR-[0-9]'` for un-prefixed reservations.)

## Step 3 — Pick the shape ([ADR-PC-000 §D4](docs/product-management/product_concepts/adrs/ADR-PC-000-namespace-and-contract-shape-framework.md))

- **Tool-selection** (the default) — the deliverable is a tool, library, runtime, or
  mechanism. Uses the [ADR-IC-000](docs/product-management/integration_concepts/adrs/ADR-IC-000-common-evaluation-criteria.md) framework: hard filters **F1** (cost/licensing) + **F2** (regulatory fit: GDPR/DORA/PSD2), soft criteria **S1** (operational complexity, 1–2 people) / **S2** (ecosystem coherence) / **S3** (exit cost) / **S4** (community + longevity). Verdicts: `Pass` / `Pass (conditional)` (names its mitigation in-cell + restated in Consequences/Residual risks) / `Fail`.
- **Contract-shape** — the deliverable is a *boundary contract* between the engine and
  a counterparty. **No F1/F2 table.** Rigor comes from the **six required slots** below.
- **Conventions** — reserved for namespace-level process ADRs (like ADR-PC-000). Rare.
  Conventions ADRs are **exempt** from the Verifiable-commitments section (§A2).

**When in doubt, default to tool-selection.** Empty hard-filter cells (nothing bought or
installed) do **not** automatically mean contract-shape — they have two readings:
- If the deliverable is a genuine **boundary contract** with a counterparty → switch to
  contract-shape (don't fabricate straw-man alternatives to fill F1/F2).
- If it is **operational/engineering discipline** — tuning, parameterising, or a posture
  decision *for an already-chosen mechanism* (DR posture, version pinning, repo strategy,
  a poll-interval) — it stays **tool-selection** as the [§D3](docs/product-management/product_concepts/adrs/ADR-PC-000-namespace-and-contract-shape-framework.md)
  *residual category* (this is why ADR-PC-009/011/019/020 are tool-selection despite
  trivial F1/F2). The F1/F2 rows degenerate to `Pass`; the decision rides on S1–S4.

Contract-shape is for a contract *between two teams/systems*; a config/posture decision is not.

## Step 4 — Write the file

Filename: `ADR-(PC|IC)-NNN-short-kebab-case-slug.md`. The slug names the **decision
topic or chosen tool**, never the alternatives (`...-event-store-technology.md`, not
`...-postgres-vs-kurrent.md`). Status starts `Proposed`. Use today's date from the
environment (`date +%F`).

> The skeletons below are shown in **ADR-PC dialect**. For an **ADR-IC**, transpose: the
> heading is `# ADR-IC-NNN`, the `Common criteria` link is the *same-folder*
> `[ADR-IC-000](./ADR-IC-000-common-evaluation-criteria.md)` **without** the "(reused per
> ADR-PC-000 D2)" suffix, and same-folder ADR links use `./ADR-IC-NNN-….md`. The
> bracketed links *in this SKILL.md* are repo-root-relative so they resolve when you read
> the skill — but the links you write *into the ADR* must follow the Step 5 ADR-relative
> rules, not be copied verbatim from here.

### Tool-selection skeleton

```markdown
# ADR-PC-NNN: <decision topic> — <chosen tool, in the title>

| Field | Value |
|---|---|
| Status | Proposed |
| Date | YYYY-MM-DD |
| Deciders | jhosm |
| Shape | Tool-selection |
| Common criteria | [ADR-IC-000](../../integration_concepts/adrs/ADR-IC-000-common-evaluation-criteria.md) (reused per [ADR-PC-000](./ADR-PC-000-namespace-and-contract-shape-framework.md) D2) |
| Depends on | <ADRs this builds on> |
| Resolves | bd `<id>` |

## Context
<what concern this serves; the constraints; the candidates evaluated (a table)>

## Evaluation
### Hard filter results
#### F1 · Cost / licensing
| Candidate | Licence / cost | Verdict |
#### F2 · Regulatory fit (GDPR / DORA / PSD2)
| Candidate | … | Verdict |
### Soft criteria
#### <Candidate A> — CHOSEN   (S1–S4 prose + decisive reason)
#### <Candidate B> — rejected (decisive reason)

## Decision
<chosen, then each rejected option with its decisive reason>

## Consequences
**Easier:** … **Harder/impossible:** … **Residual risks:** …

## Verifiable commitments
<see Step 6>
```

### Contract-shape skeleton ([ADR-PC-000 §D3](docs/product-management/product_concepts/adrs/ADR-PC-000-namespace-and-contract-shape-framework.md))

```markdown
# ADR-PC-NNN: <decision topic>

| Field | Value |
|---|---|
| Status | Proposed |
| Date | YYYY-MM-DD |
| Shape | Contract-shape |
| Counterparty | <system/team on the other side> |

## Context
<which boundary this crosses; which engine concern it serves; the motivating question>

## Decision
<the contract — ALL SIX slots filled; if one doesn't apply, say so and why>
1. **Payload shape** — schema (or a ref to the Avro/CUE/JSON-Schema artefact).
2. **Semantics** — field meanings; the state transition; what the receiver does.
3. **Ordering and delivery** — at-least-once / effectively-once; per-partition vs none; gap-detection owner.
4. **Idempotency** — the key (`event_id` / `correlation_id` / composite); uniqueness window; which side dedupes.
5. **Error model** — how the receiver says "cannot accept"; **gated (blocks the flow) vs post-flagged (record + continue)**.
6. **Ownership and versioning** — owning team; how breaking changes ship (new event / parallel topic / deprecation); CDC gating ([ADR-IC-009](../../integration_concepts/adrs/ADR-IC-009-testing-infrastructure.md)).

## Consequences
<easy / hard / impossible — for both sides; forward-compat; locked-in coupling>

## Residual risks
<what the contract does NOT commit to; deferred questions; permitted failure modes>

## Verifiable commitments
<see Step 6>
```

## Step 5 — Cross-links by location ([ADR-PC-000 §D5](docs/product-management/product_concepts/adrs/ADR-PC-000-namespace-and-contract-shape-framework.md) / CLAUDE.md)

- ADR → concept doc in its own series: `../NN-name.md`
- ADR-PC → ADR-IC (cross-namespace): `../../integration_concepts/adrs/ADR-IC-NNN-….md`
- ADR-PC → financial-concepts: `../../financial_concepts/banking_products_financial_mathematics.md`
- Within the same adrs/ folder: `./ADR-PC-NNN-….md`

## Step 6 — Seed the Verifiable-commitments section ([ADR-PC-000 §A1/§A2](docs/product-management/product_concepts/adrs/ADR-PC-000-namespace-and-contract-shape-framework.md))

Placement: **after Consequences** (tool-selection) or **after Residual risks**
(contract-shape). Three valid forms — pick one:

1. **Catalogue reference (preferred when the commitment is load-bearing).** The
   [commitment catalogue](docs/product-management/product_concepts/adrs/commitment-catalogue.md)
   is the **single source of truth** for the claim/gate/status; the ADR section only
   *references* the Test ID. If a new load-bearing invariant belongs here, add the row
   to the catalogue **and** reference it — do **not** restate the claim/gate/status in
   the ADR (that would let them drift):
   ```
   ## Verifiable commitments
   This ADR's load-bearing commitments live in the [commitment catalogue](<LINK>):
   - `TEST_ID_HERE` (§Px) — one-line gloss.
   ```
   `<LINK>` is namespace-relative: from an **ADR-PC** file it's `./commitment-catalogue.md`
   (same folder); from an **ADR-IC** file the catalogue lives across the namespace at
   `../../product_concepts/adrs/commitment-catalogue.md`.
2. **Inline table** — only when the commitment is not (yet) catalogued centrally:
   `| # | Commitment (with §-anchor) | Gate (pyramid level) | Test ID | Status (Live/Planned/Gap) |`
3. **None** — `> No executable commitments — this decision is realised entirely by [the cited downstream ADR].` (Never silently omit the section on a tool-selection/contract-shape ADR.)

A `Gap` (no gate yet) is a deliberate, listed hole — visibility is the point.

## Step 7 — Update the namespace README index

Add a row, in number order, to the namespace's index — and **match that index's live
header**, because the two differ:
- **ADR-PC** ([README](docs/product-management/product_concepts/adrs/README.md)): `# | Title | Shape | Chosen / Decision | Supports docs` (5 columns).
- **ADR-IC** ([README](docs/product-management/integration_concepts/adrs/README.md)): `# | Title | Chosen | Supports docs` (4 columns — **no Shape column**).

Copy the existing header row before writing yours; don't assume the PC shape for an IC index.

## Guardrails

- **Never** set `Status: Accepted` yourself — you create `Proposed`. (A `Proposed` ADR
  is freely editable; the `adr-immutability.sh` hook only fires on `Accepted`.)
- **Never** pick a number from disk *or* bd alone — always both (Step 2).
- A Conventions ADR carries no Verifiable-commitments section.
- If you can't decide tool-selection vs contract-shape, default to tool-selection and
  let the empty-cells diagnostic correct you.
