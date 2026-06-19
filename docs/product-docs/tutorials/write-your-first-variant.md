# Tutorial: Write your first product variant

In this tutorial we build a **product variant** — the actual term-deposit
product a bank sells — from the worked PT example, change it into our own
product, and validate it locally until it goes green. A variant is the layer
*above* the pack: where the pack (`pt.2026.1`) is the jurisdiction's regulatory
vocabulary, a variant is one configured product that draws on it.

By the end we will have run:

```sh
make validate-variant VARIANT=/tmp/my-first-variant.yaml PACK=pt.2026.1
```

and seen it finish with `OK` — the four offline depths all passing. That green
run is our destination.

This is a learning path: one route, no detours. We will not explain *what a
variant is* or *why it only carries references to the pack* here — that
understanding lives in the explanation page linked at the end. Here we just
build one and watch it pass.

> **Heads-up: a variant has no committed home in the repo yet.** Packs live
> under `packs/`, but product variants (product configs) get a registry in a
> later epic, so for now there is nowhere in the tree to commit one. We will
> work from a scratch file in `/tmp`. That is the honest state today, not a
> shortcut.

---

## Before we start

Work from the repository root for every command:

```sh
cd babelstone
```

If this is a fresh checkout, install the pinned toolchain once. `make
validate-variant` runs the Go `pack-validate` binary, so it needs the pinned Go
(and `cue`) that bootstrap brings in:

```sh
make bootstrap
make doctor      # confirms the pins are active
```

That is the only setup step. We are ready.

---

## Step 1 — Copy a working variant

We start from a variant that already exists and passes validation: the
`flat-at-maturity` fixture, a complete 12-month flat-rate PT term deposit. Copy
it to a scratch file we can edit freely:

```sh
cp -f contracts/cue/testdata/term-deposit/valid/flat-at-maturity.yaml /tmp/my-first-variant.yaml
```

> On Windows PowerShell, the equivalent is
> `Copy-Item -Force contracts/cue/testdata/term-deposit/valid/flat-at-maturity.yaml /tmp/my-first-variant.yaml`.

We now have a complete, well-formed variant at `/tmp/my-first-variant.yaml`. It
is a copy of someone else's product, so it does not yet describe *our* product.
We fix that next.

---

## Step 2 — Make it our own product

Open `/tmp/my-first-variant.yaml`. The copy reads like this:

```yaml
variant_id: dpz_pt_12m_flat_juros_venc
schema: term_deposit@2026.1
pack: pt.2026.1
term_days: 365
day_count: pt.act_360
currency: EUR
interest_variant: AT_MATURITY
rate:
  flat:
    rate_ref: { sheet: live, role_selector: deposit_origin }
# … early_termination, auto_renewal_policy, principal_bounds …
```

Two things to notice before we change anything — both are the whole point of a
variant, explained properly in the page linked at the end:

- **`day_count: pt.act_360` is a *reference*, not a value.** The variant does
  not spell out the accrual maths; it names a primitive the pinned pack
  supplies.
- **`pack: pt.2026.1` pins the law.** This variant will be judged against
  exactly that pack's rules.

Now make two edits — give the product our own identity, and change one thing the
validator will actually react to. We will turn this flat-at-maturity deposit
into one that **pays interest quarterly**:

```yaml
variant_id: dpz_pt_12m_flat_juros_trim   # our own id (trim = trimestral/quarterly)
interest_variant: PERIODIC               # was AT_MATURITY
payment_period_months: 3                 # NEW — required when PERIODIC; quarterly
```

That second change is real: the schema requires `payment_period_months`
**only** when `interest_variant` is `PERIODIC` (and it must be `1` or `3`). We
switched the product to periodic interest, so we had to add the period — and the
validator will hold us to exactly that rule. (Leave `day_count: pt.act_360`
alone; PT retail deposits are required to use Act/360, and naming a forbidden
convention is precisely the rejection the [troubleshooting
how-to](../how-to/troubleshoot-a-variant-rejection.md) covers.)

---

## Step 3 — Validate locally

Now we ask the validator to check our variant against the pack:

```sh
make validate-variant VARIANT=/tmp/my-first-variant.yaml PACK=pt.2026.1
```

This runs the Go `pack-validate` binary over our variant, checking it against
the pinned pack through the four offline depths. We expect a green run:

```
  depth 1 syntactic        ok    2ms
  depth 2 type             ok    0ms
  depth 3 pack-compliance  ok    0ms
  depth 4 regulatory       ok    0ms
OK
```

Each line is one depth of checking, in order:

1. **syntactic** — the YAML parses and fits the closed `#TermDeposit` shape
   (this is the depth that would have failed if we had set
   `payment_period_months` *without* making the variant `PERIODIC`, or fat-
   fingered a field name).
2. **type** — fields are the right type and in range, and every pack-bound
   reference like `day_count: pt.act_360` resolves to a primitive the pack
   carries.
3. **pack-compliance** — the variant respects the pinned pack (including that it
   pins the pack it is being validated against).
4. **regulatory** — the pack's regulatory rules accept the variant (e.g. PT
   deposits must use Act/360; a forbidden day-count or renewal policy is
   rejected here).

If the final line is `OK`, we are done.

---

## You did it

We took an existing variant, gave it our own identity, made a real structural
change (quarterly periodic interest), and validated it to green against the
pack. That is the full authoring loop for a variant.

What we deliberately did **not** do yet:

- **Hit a rejection.** Our change was a legal one, so every depth passed. When a
  change is *not* legal — a forbidden day-count, descending stepped-rate bands,
  a pack-restricted renewal policy — the validator names the depth and the rule.
  Reading those diagnostics is a skill of its own, covered next.
- **Pin a depth-5 simulation.** `validate-variant` runs depths 1–4 only; the
  depth-5 sealed-corpus engine simulation is engine-generated and still pending,
  the same gap the pack tutorial noted.

### Where to go next

- [How to troubleshoot a variant rejection](../how-to/troubleshoot-a-variant-rejection.md)
  — when validation says *no*, how to read which depth caught it and where the
  fix lives.
- [How packs and variants relate](../explanation/how-packs-and-variants-relate.md)
  — the *why* behind everything above: what a variant is, why it carries only
  references, and why the pack gets the final say.
- [How to add a day-count primitive](../how-to/add-a-day-count-primitive.md) —
  the pack side of the `day_count` reference our variant named.
- The rendered, drift-checked shape of every variant field lives in the
  generated [term-deposit family-schema reference](../reference/family-schemas/term-deposit.md)
  — look fields up there, never by retyping the schema.
