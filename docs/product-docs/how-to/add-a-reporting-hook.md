# How to add a regulatory reporting hook to a pack

This guide walks you through adding a **regulatory reporting hook** to a pack's
`primitives/reporting.yaml` — the file that declares which regulatory returns
your jurisdiction activates, on what cadence, and to which regulator.

You will: add the entry, set its activation and cadence, and validate locally.
The worked example is the PT term-deposit pack,
[`pt.2026.1`](../../../packs/pt.2026.1/), which activates the Banco de Portugal
retail-rate statistics and the FGD deposit-coverage return.

**Before you start, know this:** the engine **emits signals**; the reports
themselves are assembled by a **downstream reporting application**. A reporting
hook in this file declares *that a return exists and when it is due* — it does
not build the return. And critically, the signals the engine emits carry
**subject references only, never PII** — see
[the no-PII-on-the-bus discipline](#the-no-pii-on-the-bus-discipline) below.

## What a reporting entry looks like

In `primitives/reporting.yaml` each return is one map entry, keyed by the
**report id**. Here is the shape, as a short illustration — not the
authoritative field list:

```yaml
# packs/pt.2026.1/primitives/reporting.yaml
bdp_estatisticas_taxas_juro:
  active: true
  frequency: monthly
  regulator: banco_de_portugal
```

The authoritative field-by-field shape is the
[`pack.cue`](../../../contracts/cue/pack/pack.cue) schema, rendered in the
generated [pack-format reference](../reference/pack-format/README.md).
Do not copy a field table from elsewhere — link to those and you will never go
stale.

The fields you set:

- **`active`** — whether the return is turned on for this jurisdiction/version.
  A hook may be declared but `active: false` (reserved for a later version) — it
  is carried in the pack but emits nothing until activated.
- **`frequency`** — the scheduled cadence (`monthly`, `quarterly`, `annual`).
  This is the *scheduled* cadence; a regulator may also pull on demand (an
  on-demand downstream read), which is not a pack cadence.
- **`regulator`** — the regulator id the return goes to (e.g.
  `banco_de_portugal`, `fundo_garantia_depositos`).

## The no-PII-on-the-bus discipline

A reporting hook activates a downstream return that will, by its nature, be
*about* customers. **The engine never puts PII on the durable bus to make that
happen.** The signals it emits carry **subject references** (operational-tier
identifiers the downstream application resolves internally) — never the
customer's NIF, IBAN, account number, name, or email in cleartext, and never PII
ciphertext on the bus either.

This is a hard architectural rule, not a guideline, and it is **not restated
here** — it is owned by the engine's contracts. Link to them; do not re-document
the rule in your pack:

- [ADR-PC-004 — PII Encryption Envelope and Key Management](../../product-management/product_concepts/adrs/ADR-PC-004-pii-crypto-shredding.md):
  PII is encrypted at the boundary under a per-subject key; structural fields
  stay cleartext and queryable; PII is decrypted only when actually surfaced
  (display, or a regulatory report assembled **downstream**), never on the
  structural hot path.
- [ADR-PC-012 — GL Posting Signal Contract](../../product-management/product_concepts/adrs/ADR-PC-012-gl-posting-signal-contract.md):
  the engine emits **raw business events / references**; the downstream adapter
  (here, the reporting application) does the resolution. The reporting hook
  follows the same emit-references-resolve-downstream shape.
- [ADR-IC-007 §P4 — observability no-PII rule](../../product-management/integration_concepts/adrs/ADR-IC-007-observability-stack.md):
  the companion guarantee on the telemetry surface — only structural
  `babelstone.*` identifiers, money as integer cents, never PII.

The practical consequence for you, the pack author: a reporting hook is a
**scheduling-and-routing declaration**. It says "this return exists, runs at this
cadence, goes to this regulator." It does **not** name PII fields, and it does
not need to — the engine's references-not-PII signal contract and the downstream
application's internal resolution do the rest.

## Steps

### 1. Add the entry

Open `primitives/reporting.yaml` and add a new top-level key for the report id.
Use lowercase `snake_case`:

```yaml
bdp_estatisticas_taxas_juro:
  active: ...        # set in step 2
  frequency: ...
  regulator: ...
```

Remember a pack is **immutable once published** — if `pt.2026.1` is already
released, your edit belongs in a new pack version, never an in-place change.

### 2. Set `active`, `frequency`, and `regulator`

Turn the return on (or carry it inactive for a later version), set its cadence,
and name the regulator:

```yaml
bdp_estatisticas_taxas_juro:
  active: true                    # BdP retail-rate statistics, live in v1
  frequency: monthly
  regulator: banco_de_portugal
```

> **Branch — `active: false` carries a hook without emitting.** A return reserved
> for a later version (e.g. the BdP credit hooks
> `bdp_centralizacao_responsabilidades`, or IFRS 9 staging `ifrs9_staging`) is
> declared with `active: false` — present in the pack, auditable, but emitting
> nothing until a future version flips it on. This keeps the pack a complete
> statement of the jurisdiction's reporting surface.

### 3. Validate locally

Run the offline depth-1–4 validation over the pack's data:

```bash
make pack-validate PACK=pt.2026.1     # use your pack's dirname
```

This `cue vet`s `reporting.yaml` against its schema — catching a missing
`active`/`frequency`/`regulator`, a bad cadence value, or a mistyped key. It is
fully offline (no registry, no Docker).

## Honest local limits

A clean `make pack-validate` covers depths 1–4 only. Be aware:

- **The engine emits signals; downstream assembles the reports.** A local
  validate checks the *hook declaration*, not the downstream return.
- **Depth-5 (sealed-corpus engine simulation) does not run locally yet** — it
  does not exercise report emission.
- **Signing and a production registry are not wired for you** — do not treat a
  locally built pack as published.

## Related

- [Manage FGD deposit-guarantee coverage](./manage-fgd-coverage.md) — the
  `fgd_cobertura_depositos` return declared here consumes the FGD ceiling
- [Add a withholding rule](./add-a-withholding-rule.md) — a withholding rule's
  `reporting:` block (e.g. Modelo 39) is the per-rule companion to these
  pack-level hooks
- [Strangler-fig coexistence](../../product-management/product_concepts/feature-design-strangler-fig-coexistence.md)
  — why report assembly is downstream of the engine
- [The pack-format reference](../reference/pack-format/README.md) — the
  authoritative, generated field list
- [Product-docs home](../README.md)
