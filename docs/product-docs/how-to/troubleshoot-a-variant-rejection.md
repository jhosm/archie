# How to troubleshoot a variant rejection

You ran `make validate-variant` and it failed. This guide gets you from the
diagnostic line to the fix — fast. The key idea is simple:

> **The depth that rejected you tells you what kind of problem it is, and
> therefore where the fix lives.** A depth-1 problem is in your variant's shape;
> a depth-4 problem is the *pack's regulatory law* refusing an otherwise
> well-formed variant. The fix for those two lives in different files, owned by
> different people.

This is the diagnosis companion to the [tutorial](../tutorials/write-your-first-variant.md)
(which only ever goes green) and to [how to add a day-count primitive](./add-a-day-count-primitive.md)
(which authors the pack rule on the *other* side of a depth-4 rejection).

## How to read a diagnostic line

Every rejection prints one line per problem, in this shape:

```
  ✗ depth 4  day_count  [forbidden_day_count]  day-count "act_365" is not regulatorily permitted for a PT term_deposit (pack pt.2026.1 permits: act_360)
```

Read it left to right:

- **`depth 4`** — *which layer* caught it. This is the most important field; the
  table below turns it into an action.
- **`day_count`** — the field at fault (or a `file:line` for a pure shape
  error).
- **`[forbidden_day_count]`** — a stable **reason code** you can grep for.
- the **message** — the specific rule, in plain English, naming the pack.

Because each invalid fixture isolates one rule, a run names one problem at a
time. Fix it, re-run, repeat.

## The depth → fix map

| Depth | Reason codes you'll see | What it means | Where the fix lives |
|---|---|---|---|
| **1 — syntactic** | `shape_mismatch`, `unknown_field` | The YAML doesn't fit the closed `#TermDeposit` *shape*: a misspelled/extra field, a cross-field rule, or a free string where a pack-bound *reference* is required (the binding shape). | **Your variant.** Correct the shape. |
| **2 — type** | `type_mismatch`, `unknown_primitive` | A field is the wrong type or out of range (e.g. `currency: USD`), or a well-formed reference (e.g. `day_count: pt.act_999`) names no primitive the **pinned pack carries**. | **Your variant** (fix the value, or name a carried primitive) — or add the primitive to the pack. |
| **3 — pack-compliance** | `pack_bound_violation` | The variant pins a different pack than the one it's validated against, or breaches a pack bound. | **Your variant** (pin/respect the right pack). |
| **4 — regulatory** | `forbidden_day_count`, `forbidden_renewal_policy`, `non_ascending_steps`, `open_tail_not_last` | The variant is *well-formed and resolves* — but the pack's regulatory rules (or a cross-field obligation CUE can't express element-wise) forbid it for this family. | **Usually your variant** (comply). Changing what the pack *permits* is a separate, heavier pack change. |
| **5 — corpus** | *(skipped)* | The sealed-corpus engine simulation. Not run locally yet — see *Honest limits* below. | n/a |

The depth definitions, budgets, and the *why* are normative in
[ADR-PC-006 §Context (the depth table) and §P3](../../product-management/product_concepts/adrs/ADR-PC-006-cue-schema-language.md);
the lines above are an orientation, not the contract.

## Worked diagnoses

Each entry below is a real diagnostic from a shipped fixture. To *see* any of
them yourself, point `validate-variant` at the fixture — the fixture directories
are a worked catalogue of every rule, one violation per file:

```sh
make validate-variant VARIANT=pack-validate/testdata/term-deposit/invalid/depth4-act365-deposit.yaml PACK=pt.2026.1
```

### Depth 1 — a field that isn't in the schema

```
  ✗ depth 1  unknown-field.yaml:12:1  [unknown_field]  #TermDeposit.promo_flag: field not allowed
```

The schema is **closed**: a field it does not declare is an error, not an
ignored extra. Cause: a typo (`interst_variant`) or a field the engine doesn't
model (`promo_flag`). **Fix:** remove or correct the field. (Why closedness is a
feature: [reading a CUE schema](../explanation/reading-a-cue-schema.md#why-closedness-is-a-feature).)

### Depth 1 — a free string where a reference belongs

```
  ✗ depth 1  …/common.cue:55:22  [shape_mismatch]  #TermDeposit.day_count: invalid value "Act/365" (out of bound =~"^[a-z]{2}\.[a-z0-9_]+(\.[a-z0-9_]+)*$")
```

`day_count` must be a dotted, pack-namespaced **reference** (`pt.act_360`),
because the pack supplies the convention. Writing `day_count: "Act/365"` doesn't
*set* Act/365 — it's a bare string, not a reference, so it fails the binding
*shape*. **Fix:** use a pack-bound reference like `pt.act_360`.

### Depth 2 — a reference the pack doesn't carry

```
  ✗ depth 2  day_count  [unknown_primitive]  day_count "pt.act_999" resolves to no day-count primitive in pack pt.2026.1
```

The reference is well-formed (depth 1 passed) but the pinned pack carries no
such primitive. **Fix:** name a primitive the pack actually supplies (the pack's
`primitives/day-count.yaml` lists them), or — if the convention is genuinely
missing — [add it to the pack](./add-a-day-count-primitive.md).

### Depth 3 — the variant pins the wrong pack

```
  ✗ depth 3  pack  [pack_bound_violation]  variant pins pack "pt.2099.1" but is being validated against pack "pt.2026.1"
```

The variant's `pack:` field and the `PACK=` you validated against disagree.
**Fix:** pin the pack you mean, or validate against the pack the variant pins.

### Depth 4 — the pack forbids it (regulatory coherence)

This is the depth that surprises people, so it gets the most attention. The
variant is **well-formed and its references resolve** — depths 1–3 all passed —
and it is *still* rejected, because the pack's regulatory law says this family
may not do this:

```
  ✗ depth 4  day_count  [forbidden_day_count]  day-count "act_365" is not regulatorily permitted for a PT term_deposit (pack pt.2026.1 permits: act_360)
```

```
  ✗ depth 4  auto_renewal_policy  [forbidden_renewal_policy]  auto-renewal policy "SAME_TERM_SAME_RATE" is pack-restricted and not permitted for a PT term_deposit (pack pt.2026.1; 02 §2.4.4)
```

Both are the pack's `permitted_for` law biting: the pack *carries* `act_365` and
the `SAME_TERM_SAME_RATE` policy, but does not **permit** either for a term
deposit. Depth 4 also enforces obligations CUE can't express across list
elements:

```
  ✗ depth 4  rate.stepped.steps.1.from_day  [non_ascending_steps]  from_day 90 is not strictly greater than the preceding step (365)
```

```
  ✗ depth 4  early_termination.banded.0.up_to_days  [open_tail_not_last]  the open (null) up_to_days band must be the single last band
```

**Fix — and this is the important judgement call:** a depth-4 rejection almost
always means *your variant should comply*, not *the pack should change*. The
pack's regulatory rules are deliberately strict law, auditor-visible in plain
YAML, applied uniformly to every variant. Make the variant conform (use the
permitted day-count; pick an unrestricted renewal policy; sort the steps; move
the open band last).

Changing what the pack *permits* — adding a family to a `permitted_for` set — is
a **regulatory change to the pack**, on the pack's slow, heavily-approved
cadence, not a variant edit. Do that only when the regulation genuinely allows
it, via [how to add a day-count primitive](./add-a-day-count-primitive.md), and
understand the relationship first in
[how packs and variants relate](../explanation/how-packs-and-variants-relate.md).

## Honest limits

- **Depth 5 (sealed-corpus engine simulation) does not run here.** It is
  engine-generated and still pending, so a clean `validate-variant` proves
  depths 1–4 only — not that the variant's *accrual output* is correct. See
  [ADR-PC-006 §P4](../../product-management/product_concepts/adrs/ADR-PC-006-cue-schema-language.md).
- **`validate-variant` is offline and pre-deploy.** It does not exercise the
  engine-side pack loader/verifier (still pending) or a running rate sheet. A
  green variant is *valid against the pinned pack*, not *deployed*.

## Related

- [Tutorial: write your first variant](../tutorials/write-your-first-variant.md)
  — the happy path this page is the inverse of.
- [How to validate a pack locally](./validate-a-pack-locally.md) — the
  pack-side gate (`make pack-validate`) and how to read its failures.
- [How to add a day-count primitive](./add-a-day-count-primitive.md) — authoring
  the `permitted_for` rule that a `forbidden_day_count` rejection enforces.
- [Reading a CUE schema](../explanation/reading-a-cue-schema.md) — the four-layer
  method for understanding *why* a rule rejects what it rejects.
- [How packs and variants relate](../explanation/how-packs-and-variants-relate.md)
  — why depth 4 is the pack judging the variant.
- Back to the [product-docs front door](../README.md).
