# Reading a CUE schema (without learning CUE)

The constraints that decide whether your pack and its variants are accepted are
written in **CUE** — a language you will never author. But you will read it.
When a validation run rejects a variant, the rule it broke lives in a `.cue`
file, and the fastest way to understand *why* the rejection is correct (or to
predict it before you commit) is to read that rule directly.

This page is not a CUE tutorial. It gives you a **durable method** for reading
babelstone's `.cue` schemas as a config author: a way to extract the rule that
matters without parsing the language as a whole. The method has four layers,
ordered easiest-first. You rarely need all four — usually the first one is
enough.

> Why this page exists: the reading method below was the working knowledge of
> the people who built the schemas, but it had no home in the docs. It is not a
> task you follow (that is a how-to) and not a field list you look up (that is
> the reference) — it is the *understanding* that makes those two usable. That
> understanding-shaped gap is exactly what the Diátaxis framework calls
> [explanation](https://diataxis.fr/explanation/).

---

## A mental model first: what a CUE schema *is* here

A babelstone `.cue` schema is a **contract that YAML data must satisfy**. You
write YAML — a pack file, or a product variant — and the schema describes the
shape that YAML is allowed to take: which fields exist, what types and ranges
they hold, which combinations are legal. Validation is the act of checking your
YAML against that contract; it answers one question — *does this data fit?* —
and, when it does not, names the field and the rule that failed.

Two properties make this contract trustworthy, and both are worth holding in
your head before you read any rule:

- **The schema is the source of truth, and you read it — you do not edit it.**
  The `.cue` files are owned by engineering on a quarterly cadence; authors
  write the YAML the schema governs, never the schema itself. See
  [contracts/cue/README.md](https://github.com/jhosm/babelstone/blob/main/contracts/cue/README.md).
- **The schema is *closed*.** A field the schema does not declare is not
  ignored — it is a hard error. We return to why that is a feature
  [below](#why-closedness-is-a-feature).

### Two kinds of schema, validating two different things

There are two families of `.cue` file, and knowing which one you are reading
tells you what is being checked:

| File | Validates | You meet it when… |
|---|---|---|
| [`contracts/cue/pack/pack.cue`](https://github.com/jhosm/babelstone/blob/main/contracts/cue/pack/pack.cue) | a **pack's own data** — the manifest, primitives, parameters, rate-sheet refs, sealed corpus | authoring or reviewing a `pt.YYYY.N` pack |
| [`contracts/cue/families/term-deposit.cue`](https://github.com/jhosm/babelstone/blob/main/contracts/cue/families/term-deposit.cue) | a **product variant** — one configured term-deposit shape | reasoning about which variants a pack will accept |

The `pack.cue` source says this in its own header comment: it "describe[s] the
shape of the pack's YAML data … distinct from the *family* schema, which
validates variant YAML." When a rejection confuses you, your first orientation
question is always *which of these two am I in?* — because they answer different
questions about different files.

The deeper decision behind choosing CUE for both — and why it is not JSON
Schema or a bespoke DSL — is recorded in
[ADR-PC-006](../../product-management/product_concepts/adrs/ADR-PC-006-cue-schema-language.md).
You do not need it to read a schema, but it is where the *why* lives.

---

## Layer 1 — Read the fixture pair (the single best technique)

The most reliable way to learn a rule is to look at a passing example and a
failing one side by side. babelstone ships exactly that, on purpose, in
[`contracts/cue/testdata/term-deposit/`](https://github.com/jhosm/babelstone/tree/main/contracts/cue/testdata/term-deposit):

- `valid/` — variants the schema **must accept**.
- `invalid/` — variants the schema **must reject**, with a deliberate
  **one-broken-rule-per-file** discipline, each carrying a comment that *names
  the rule it breaks*.

Read the valid file to see a correct whole; read the matching invalid file to
see exactly **where the boundary is**. The difference between them is the rule,
isolated.

A concrete example. The valid `flat-at-maturity.yaml` is a full term-deposit
variant; among its fields are:

```yaml
# Variant A from authoring §3.2 — flat-rate, interest at maturity.
day_count: pt.act_360          # a pack-bound reference, not an inline value
interest_variant: AT_MATURITY
```

The invalid `period-without-periodic.yaml` is the same variant with one field
added, and its top comment tells you the rule before you read a line of YAML:

```yaml
# INVALID: payment_period_months set on a non-PERIODIC variant — the guard
# forbids the field unless interest_variant is PERIODIC.
interest_variant: AT_MATURITY
payment_period_months: 3      # <- the single broken rule
```

You have now learned a real cross-field rule — *`payment_period_months` is only
allowed when `interest_variant` is `PERIODIC`* — without reading any CUE at all.
The fixture filenames are a catalogue of the rules worth knowing:
`unknown-field`, `unbound-day-count`, `non-eur-currency`,
`both-rate-shapes`, `principal-max-below-min`, and so on. Browsing that
`invalid/` directory is the fastest tour of the schema's edges available.

Because each invalid file isolates one violation, a failed run names one rule.
That is a design choice, not an accident — it is the method working as intended.

---

## Layer 2 — Read the comment, then the constraint

When the fixtures don't cover your question, open the `.cue` file itself. This
codebase comments the **why** above each constraint, so the durable habit is:
**read the comment first, the constraint second.** The comment is plain prose
aimed at exactly your question; the line beneath it is the enforcement.

From [`families/term-deposit.cue`](https://github.com/jhosm/babelstone/blob/main/contracts/cue/families/term-deposit.cue),
the day-count field:

```cue
// Pack-bound: PT retail deposits use Act/360. The schema only declares the
// binding; depth-4 regulatory coherence (in the pack) is what rejects e.g.
// Act/365 for a PT deposit.
day_count: #PackBoundPrimitive
```

You can read that comment and walk away understanding the field — that
`day_count` is a *reference the pack supplies*, not a value you type inline, and
that the rule which rejects `Act/365` actually lives in the pack's
regulatory-coherence layer — without ever decoding `#PackBoundPrimitive`. The
prose does the teaching. The line below it is only there to make the prose
enforceable.

This pattern holds throughout. The cross-field guard you met in Layer 1 reads,
in the schema, as a comment explaining the invariant followed by the two
`if`-guarded clauses that enforce it. Read the comment; trust that the clauses
say what the comment claims; move on.

---

## Layer 3 — A decoder ring for the symbols

Sometimes you do want to read the constraint line. CUE leans on a small set of
operators, and a handful of them cover almost everything in these schemas. This
table is **generic CUE pedagogy** — it is not babelstone-specific truth, just a
reading aid — so a glance here lets you decode a line without learning the
language:

| You see | It means | Read it as |
|---|---|---|
| `#Name` | a **closed definition** | "a named shape that rejects any field it didn't declare" — the no-escape-hatch guarantee |
| `&` | **and** (unify) | "must satisfy both" — e.g. `int & >0` is "an integer *and* greater than zero" |
| `\|` | **or / enum** | "one of these" — e.g. `"AT_MATURITY" \| "PERIODIC" \| "ADVANCE"` is a closed set of allowed values |
| `=~"..."` | **regex match** | "a string matching this pattern" — e.g. an ISO-date or a snake_case id |
| `*X` | **default** | "if absent, X" |
| `[string]: T` | **a map** | "any string key, each mapping to a value of type T" |
| `!=""` | **non-empty** | "a string, and not the empty one" |
| `field?: T` | **optional field** | "may be absent; if present, must be type T" |
| `if cond { ... }` | **conditional constraint** | "when `cond` holds, these extra rules apply" — this is how cross-field invariants are written |

Armed with that, a line like `term_days: int & >0` reads as "an integer greater
than zero", and `interest_variant: "AT_MATURITY" | "PERIODIC" | "ADVANCE"` reads
as "exactly one of these three strings". The `if interest_variant ==
"PERIODIC"` block is the cross-field guard from Layer 1, now legible: *when the
variant is periodic, the period field becomes required and bounded.*

You do not need to memorise this. You need to know it exists so that a strange
symbol is a quick lookup, not a wall.

---

## Layer 4 — Ask CUE itself

The last resort is also the most authoritative: stop reading and **make the
data wrong on purpose**, then let the validator tell you in plain English what
it objects to. Copy a working variant, introduce the single change you are
unsure about, and run a local validation pass — the same depths-1–4 check the
how-to walks through in
[validate-a-pack-locally.md](../how-to/validate-a-pack-locally.md):

```sh
make pack-validate PACK=pt.2026.1
```

The diagnostic names the field and the failed constraint — for an undeclared
field, for instance, the complaint is the now-familiar `field not allowed`. This
turns a question about a rule into an experiment with an answer, and it is the
only layer that is guaranteed current, because it runs the actual schema rather
than your reading of it. It is offline and fast; treat it as a cheap way to
*confirm* an understanding the first three layers gave you, not a substitute for
forming one.

---

## Why closedness is a feature

The single most surprising thing for a newcomer is that a **misspelled field or
constant is a hard error, not a silent no-op.** Write `promo_flag: true` in a
variant — a field the schema never declared — and validation fails with `field
not allowed`, exactly as the `invalid/unknown-field.yaml` fixture demonstrates.

This is deliberate, and it protects you. Every type in these schemas is a CUE
*definition* (`#Name`), and CUE definitions are **closed**: they reject anything
they did not declare. The reason, in the schema's own words, is that there is
**no DSL escape hatch** — a variant must not be able to smuggle behaviour
through a field the engine doesn't model. The practical payoff for a config
author is sharper: a typo in a field name cannot quietly become a setting the
engine ignores. The rule that would have caught `interst_variant` is the same
rule that refuses `promo_flag`. Closedness means *what you didn't mean to say is
caught at the door, not discovered in production.* The rationale is recorded in
the [ADR-PC-006](../../product-management/product_concepts/adrs/ADR-PC-006-cue-schema-language.md)
Decision and restated atop every schema file.

---

## Where this method takes you

Hold the four layers as a ladder you climb only as far as you need:

1. **Fixture pair** — the boundary, shown. Start here.
2. **Comment, then constraint** — the why, in prose, above the rule.
3. **Decoder ring** — the symbols, when you must read the line itself.
4. **Ask CUE** — the authoritative answer, when reading isn't enough.

The reading you do here is reading, not authoring: the `.cue` files are the
source of truth, owned and changed by engineering, and your job is to understand
the contract well enough to write YAML that fits it. When you need the *rendered*
field-by-field shape of a schema rather than its constraint source, look it up
in the generated, drift-checked reference, not by retyping a `.cue` file from
memory:

- The generated reference home —
  [`reference/README.md`](../../product-management/reference/README.md)
- The pack-format schema, rendered —
  [`reference/pack-format/README.md`](../../product-management/reference/pack-format/README.md)

And when you are ready to turn reading into a validation run on your own pack,
follow [validate-a-pack-locally.md](../how-to/validate-a-pack-locally.md) or
return to [the front door](../README.md) for the rest of the set.
