# Pack effective-date and per-instance pinning

This page explains *why* a deposit keeps computing under the same regulatory pack
for its whole life — even after a newer pack ships — and why the pack manifest's
`pack_effective_from` field is, in v1, **just a date written on the label** rather
than a switch that changes anything. It is background reading, not a procedure: if
you came here to actually publish a new pack version, [version and release a
pack](../how-to/version-and-release-a-pack.md) is the task. The aim is that after
reading this you understand the one counterintuitive fact at the centre of the pack
lifecycle: **a pack is immutable for any instance constituted under it**, and a new
pack does not reach back and re-govern deposits that already exist.

This sits alongside [rate-sheet versioning and point-in-time
resolution](./rate-sheet-versioning-and-resolution.md): that page explains how a
*price* is pinned at constitution; this one explains how the *pack* (the
jurisdiction vocabulary — day-count, withholding rate, FGD ceiling) is pinned the
same way. They are two applications of one idea.

## The pack is pinned at constitution, then frozen for life

When a deposit is constituted, the engine resolves the **currently-active** pack
version — the version the operating bank has adopted for new constitutions — and
stamps it onto the deposit's creation event. A deposit constituted on 2026-03-15
under `pack: pt.2026.1` keeps computing under `pt.2026.1` for its entire life, even
after `pt.2027.1` ships next year. Its day-count stays Act/360 as `pt.2026.1`
declared it; its withholding stays what `pt.2026.1` declared it; nothing a later
pack changes touches it.

This is not an implementation detail you can opt out of — it is the load-bearing
guarantee a banking engine has to make. A regulator, an auditor, and the customer
all expect that the deposit pays out under the rules that were in force when it was
opened. Retroactively re-governing a live deposit because a new pack landed would
break exactly that expectation.

The mechanism is the **event envelope**: every event an instance emits carries a
`pack_version` field. Constitution stamps the active version; every later lifecycle
event copies the instance's current pin forward on the stream. The pin is never
re-derived from the wall clock after constitution — it is data flowing forward on
the event stream. The full reasoning (why the pin lives on the event rather than on
a projection or a side table) is
[ADR-PC-009 §P1–§P2](../../product-management/product_concepts/adrs/ADR-PC-009-per-instance-version-pinning.md);
the short version is that, in an event-sourced engine, a projection is *rebuilt
from* events, so the only place a pin can live without circularity — and the only
place that can record a mid-life migration boundary — is on the events themselves.

## Why "immutable per instance" and "immutable once published" are the same promise

Two immutabilities reinforce each other, and it is worth seeing they are one idea:

- **Immutable once published** — a `pack_version` is never edited after it is
  published. A correction is a *new* version (a higher `N`), never a rewrite of the
  old one. This is the same forward-only rule the rate sheet follows, and it is
  enforced by the publish format: the pack ships pulled-by-digest, so the bytes
  behind `pt.2026.1` cannot change underneath you
  ([ADR-PC-007 §P1–§P2](../../product-management/product_concepts/adrs/ADR-PC-007-signed-yaml-oci-pack.md)).
- **Immutable per instance** — once a deposit pins `pt.2026.1`, that is the pack it
  computes under for life, regardless of what later versions exist.

Together they mean a deposit's governing rules are *decidable from the event stream
alone*: read the `pack_version` off any event, resolve it to the exact published
bytes, and you know precisely which day-count and which withholding rate applied —
no "what was active on 2026-03-15" reconstruction, no time-range query. The pack
version on the event *is* the answer.

## Retroactive change is possible — but only as an explicit, audited migration

"Pinned for life" does not mean a deposit can *never* move to a new pack. It means
it never moves **silently**. The only way to re-pin an existing instance is an
explicit, operator-initiated **pack migration**: an operator issues a migration
with an explicit instance filter, and the engine emits one `PackVersionMigrated`
event per affected instance. Events before that point stay pinned to the old pack;
events after it carry the new one. The migration boundary is therefore *intrinsic
to the stream* — you can still replay the instance under its old pack for the
history before the migration.

This is what makes a sentence like *"from 2027-01-01 the new rules apply to all
existing instances"* expressible **without** breaking pinning: the pin still never
moves on its own; it moves only at an explicit, per-instance, audited event. The
event names the `operator_actor` and a `migration_id`, so the affected set is fully
auditable and the migration is reversible-in-principle by replay. The migration
semantics live in
[ADR-PC-009 §P3](../../product-management/product_concepts/adrs/ADR-PC-009-per-instance-version-pinning.md);
the operator opt-in (adoption is not the same as migration — there are no silent
upgrades) is
[§P4](../../product-management/product_concepts/adrs/ADR-PC-009-per-instance-version-pinning.md).

## Why `pack_effective_from` is metadata-only in v1

The pack manifest carries a `pack_effective_from` date (in `pt.2026.1` it is
`2026-01-01` — see [`packs/pt.2026.1/pack.yaml`](../../../packs/pt.2026.1/pack.yaml)).
A natural first reading is that this date *drives* something — that on that date the
pack starts governing deposits, or that some primitives begin "floating" to the
latest pack while others stay pinned. **In v1, it drives nothing.**

v1 makes one simple choice: **pin everything at constitution, float nothing.** There
is no per-primitive pin-or-float policy. `pack_effective_from` is read as
informational metadata — a date you can `cat` and `diff`, useful to a human reading
the manifest, but not a value the engine branches on. The field exists so the
manifest *shape* does not have to change later, not because v1 acts on it. This is a
deliberate reserved placeholder, stated as such in
[ADR-PC-009 §P5](../../product-management/product_concepts/adrs/ADR-PC-009-per-instance-version-pinning.md).

The reason for the reservation: a richer future policy is already sketched. The
intended *direction* (confirmed, but not implemented in v1) is **per-primitive** —
instrument-defining primitives (cash-flow shape, day-count, the contracted TAN)
would stay pinned, while regulation-tracking primitives (the withholding rate, the
FGD ceiling, disclosure templates) would *float* by accrual date so a regulatory
rate change reaches in-flight deposits without a per-instance migration. That is a
v2+ deliverable; v1 ships none of it and still pins everything. Keeping the field
present now means the v2 design space stays open without a v1 manifest shape it
cannot later honour. Treat `pack_effective_from` today as documentation of *when the
pack's rules took regulatory effect*, not as a runtime switch.

## Why this matters to you as a config author

- **A published pack version is frozen.** You never edit `pt.2026.1`; a change is
  `pt.2026.2` (or `pt.2027.1`). The publish format makes the old bytes
  unchangeable, so the deposits that pinned it stay correct.
- **A new pack does not disturb existing deposits.** Publishing `pt.2027.1` changes
  what *new* constitutions pin; it does nothing to deposits already live under an
  earlier version. Moving them is a separate, explicit, audited migration.
- **`pack_effective_from` is a label, not a lever — for now.** Set it to the date
  the pack's rules take effect, but do not expect v1 to branch on it. The
  pin-or-float behaviour it anticipates is v2+.

If you hold one sentence, hold this: *the pack a deposit was born under governs it
for life, and the only thing that changes that is an explicit migration.*

## Where to go next

- To publish a new pack version (the `pt.YYYY.N` lifecycle): [version and release a
  pack](../how-to/version-and-release-a-pack.md).
- For *how a price* (not the pack) is pinned the same way: [rate-sheet versioning
  and point-in-time resolution](./rate-sheet-versioning-and-resolution.md).
- For the per-instance pinning decision in full (envelope vs projection vs registry,
  migration semantics, the `pack_effective_from` placeholder):
  [ADR-PC-009](../../product-management/product_concepts/adrs/ADR-PC-009-per-instance-version-pinning.md).
- For the pack format, signing, and distribution model:
  [ADR-PC-007](../../product-management/product_concepts/adrs/ADR-PC-007-signed-yaml-oci-pack.md).
- For the field-level manifest shape (never restated here): the generated [pack
  manifest format reference](../reference/pack-format/README.md).
- Back to the [product-docs front door](../README.md).
