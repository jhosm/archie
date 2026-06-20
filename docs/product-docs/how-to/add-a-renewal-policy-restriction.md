# How to add a renewal-policy restriction to a pack

This guide walks you through restricting an **auto-renewal policy** in a pack's
`primitives/renewal-policies.yaml` — the file that declares *which product
families may use a pack-restricted renewal policy*, using the same
`permitted_for` scope the day-count primitive uses.

You will: understand which policy is restricted, set its `permitted_for` scope,
and validate locally. The worked example is the PT term-deposit pack,
[`pt.2026.1`](../../../packs/pt.2026.1/).

**Before you start, know this:** the family schema **structurally** allows three
auto-renewal policies — `NONE`, `SAME_TERM_CURRENT_RATE`, and
`SAME_TERM_SAME_RATE`. Depths 1–2 accept all three. But one of them,
`SAME_TERM_SAME_RATE` (auto-renew at the deposit's **original** rate), is
"pack-restricted": a product may only use it where the jurisdiction's pack
permits it. This file is where that regulatory restriction lives, mirroring the
`permitted_for` pattern the day-count primitive uses — **auditor-visible in the
signed pack itself, not hardcoded in the validator**.

## What a renewal-policy entry looks like

In `primitives/renewal-policies.yaml` a restricted policy is one map entry,
keyed by the **policy id**, carrying a `permitted_for` list. Here is the shape,
as a short illustration — not the authoritative field list:

```yaml
# packs/pt.2026.1/primitives/renewal-policies.yaml
same_term_same_rate:
  description: >-
    Auto-renew at the deposit's original rate. Pack-restricted: only permitted
    for the families listed here.
  permitted_for: []     # the families allowed to use this policy
```

The authoritative field-by-field shape is the
[`pack.cue`](../../../contracts/cue/pack/pack.cue) schema, rendered in the
generated [pack-format reference](../reference/pack-format/README.md).
Do not copy a field table from elsewhere — link to those and you will never go
stale.

The key idea:

- **`permitted_for`** — the list of product-family ids allowed to use this
  **restricted** policy. An empty list `[]` means "no family may use it." Only a
  **restricted** policy declares a permitted-set; the **unrestricted** policies
  (`NONE`, `SAME_TERM_CURRENT_RATE`) are not listed here at all, because every
  family may use them — exactly as only the regulated day-counts narrow
  `permitted_for`.

## Which policies are restricted, and why

`NONE` and `SAME_TERM_CURRENT_RATE` are **unrestricted** — they need no entry in
this file. `SAME_TERM_SAME_RATE` is the **less common, pack-restricted** policy:
renewing at the original rate is a commercial commitment a regulator may not let
every product make, so the pack must explicitly permit it per family.

In `pt.2026.1` the policy is **not** permitted for term deposits — the launch
products use `NONE` or `SAME_TERM_CURRENT_RATE` — so the list is empty:

```yaml
same_term_same_rate:
  permitted_for: []        # not permitted for any family in PT v1
```

A term-deposit variant declaring `SAME_TERM_SAME_RATE` therefore parses (depths
1–2 pass) but is **rejected at depth 4 (regulatory coherence)**. A later pack
that onboards a same-term-same-rate product simply adds `term_deposit` to the
list — **zero validator change**, because the rule is data, not code.

## Steps

### 1. Locate (or add) the restricted policy entry

Open `primitives/renewal-policies.yaml`. The restricted policy
(`same_term_same_rate`) is keyed by its policy id. If your pack does not yet
carry it, add it with a `description` and a `permitted_for` list:

```yaml
same_term_same_rate:
  description: >-
    Auto-renew at the deposit's original rate. Pack-restricted.
  permitted_for: ...    # set in step 2
```

Remember a pack is **immutable once published** — if `pt.2026.1` is already
released, your edit belongs in a new pack version, never an in-place change.

### 2. Set `permitted_for` — the regulatory lever

List every product family regulatorily allowed to use the restricted policy:

```yaml
same_term_same_rate:
  permitted_for: [term_deposit]   # term deposits MAY auto-renew at the original rate
```

To **forbid** the policy for every family (the PT v1 default), leave the list
empty:

```yaml
same_term_same_rate:
  permitted_for: []               # no family may use it; depth-4 rejects any variant naming it
```

> **Branch — leave `permitted_for` empty and no variant can use it.** An empty
> list is a deliberate "carried but forbidden" state, not a default to fill in
> later. If you intend a family to use `SAME_TERM_SAME_RATE`, you **must** add
> that family's id, or every variant naming it fails depth-4.

> **Branch — do not list the unrestricted policies.** `NONE` and
> `SAME_TERM_CURRENT_RATE` are unrestricted; adding them here would imply they
> are restricted. Only the restricted policy declares a permitted-set.

### 3. Validate locally

Run the offline depth-1–4 validation over the pack's data:

```bash
make pack-validate PACK=pt.2026.1     # use your pack's dirname
```

To confirm the **restriction actually bites**, validate a *variant* that names
`SAME_TERM_SAME_RATE` against the pack — this is the path that exercises depth-4:

```bash
make validate-variant VARIANT=path/to/variant.yaml PACK=pt.2026.1
```

A variant naming the policy while `permitted_for: []` fails here with a
`not regulatorily permitted` diagnostic; one whose family is in the list passes.

## Honest local limits

A clean `make pack-validate` covers depths 1–4 only. Be aware:

- **Depth-5 (sealed-corpus engine simulation) does not run locally yet** — it
  does not exercise the renewal behaviour itself, only the depth-1–4 declaration.
- **The engine-side pack loader/verifier is pending** — the depth-4 rejection is
  the *designed* behaviour, observable through `make validate-variant`, but the
  end-to-end engine-load path is not yet wired locally.
- **Signing and a production registry are not wired for you** — do not treat a
  locally built pack as published.

## Related

- [Add a day-count primitive](./add-a-day-count-primitive.md) — the same
  `permitted_for` regulatory-lever pattern, for accrual conventions
- [Reading a CUE schema](../explanation/reading-a-cue-schema.md) — what the five
  validation depths mean and why `permitted_for` lives in the pack
- [The pack-format reference](../reference/pack-format/README.md) — the
  authoritative, generated field list
- [Product-docs home](../README.md)
