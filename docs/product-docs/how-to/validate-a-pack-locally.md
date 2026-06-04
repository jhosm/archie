# How to validate a pack locally

You have edited a pack — added a primitive, adjusted a parameter, pointed a
rate-sheet ref at a new sheet — and you want to know it is sound *before* you
open a PR. This guide gets you a green (or honestly red) result on your own
machine, offline.

This is the day-to-day inner loop. It needs no registry, no Docker, and no
running engine.

## Before you start

You need the pinned toolchain active (it brings `cue` and `dotnet`). If you have
not set the machine up yet, or the validator cannot find `cue`/`dotnet`, run:

```sh
make bootstrap   # installs the pinned toolchain (mise.toml)
make doctor      # confirms the pins are active — cue, dotnet, etc.
```

If `make pack-validate` later errors with "command not found" or a Roslyn/CUE
version mismatch, that almost always means the pinned toolchain is not on your
PATH — re-run `make doctor`, fix any `MISSING` line, then retry.

## Step 1 — Validate your pack

From the repo root:

```sh
make pack-validate PACK=pt.2026.1
```

Substitute your own pack directory name for `pt.2026.1`. The name **is** the
version key (`<pack_id>.<pack_version>`), and the validator checks that the
directory name matches the manifest — a mismatch is a failure, not a warning.

This runs `packs/pack.sh validate` (see
[`packs/pack.sh`](../../../packs/pack.sh)): it stages a copy of your pack,
copies in the digest-pinned family schemas from
[`contracts/cue/`](../../../contracts/cue/README.md) (your committed pack does
**not** carry `schemas/` — that is added at build), and runs `cue vet` over the
manifest and every data file. It is fully offline.

A clean run prints one `ok` line per file plus the no-silent-gap sweep, and ends
with `OK`:

```
== validate pt.2026.1 ==
  ok (#Manifest)  pack.yaml
  ok (#DayCounts)  primitives/day-count.yaml
  ...
  skip          depth-5 corpus: expected-events.yaml empty (generation pending, C.3)
  ok            no-silent-gap sweep: all data .yaml covered
OK
```

## What depths 1–4 check

`make pack-validate` runs validator **depths 1–4** over your pack's YAML. In one
line each:

1. **Syntactic** — the YAML parses and matches the schema's structure.
2. **Type / range + pack-bound primitive resolution** — fields are the right
   type and within bounds, and a pack-bound reference like `day_count: pt.act_360`
   resolves to a primitive the pack actually supplies.
3. **Pack compliance** — your data respects the pack's own bounds (e.g. a rate
   does not exceed the pack's `max_consumer_rate_bps`).
4. **Regulatory coherence** — cross-field invariants regulation demands (e.g.
   stepped-rate bands must ascend; and, for a *variant*, the PT pack's rule that
   a term deposit must use Act/360, which rejects Act/365).

A nuance worth knowing: `make pack-validate` runs these depths over your **pack's
own data** (manifest, primitives, parameters, corpus inputs). The
regulatory-coherence rejection of a *forbidden day-count* is a check against a
**variant**, so you exercise it with `make validate-variant` (see
[how to add a day-count primitive](./add-a-day-count-primitive.md)), not with
`make pack-validate` alone.

These are summaries to orient you. **The authoritative depth definitions, budgets,
and the why live in the ADR — do not treat the lines above as the contract:**
[ADR-PC-006 §Context (the depth table) and §P3](../../product-management/product_concepts/adrs/ADR-PC-006-cue-schema-language.md).

### Depth 5 is not run here (and that is expected)

Depth 5 is the **sealed-corpus engine simulation** — it replays
`test-corpus/canonical-instances.yaml` through the engine and compares against
`test-corpus/expected-events.yaml`. That `expected-events.yaml` is
**engine-generated, not hand-authored**, and today it is an intentional empty
placeholder. So `pack-validate` reports depth 5 as a **logged skip**, never a
pass:

```
  skip          depth-5 corpus: expected-events.yaml empty (generation pending, C.3)
```

Treat that `skip` line as exactly that — work that has not run, not work that
passed. (A corpus file that is *present but unparseable* is a hard failure, not a
skip — the validator will not let a corrupted evidence file go green.) The
engine-side generation of `expected-events.yaml` is pending; see
[ADR-PC-006 §P4](../../product-management/product_concepts/adrs/ADR-PC-006-cue-schema-language.md).

## Two local gates — pick the right one

There are two distinct `make` targets, and they validate different things.
Reach for the one that matches what you changed.

| You changed… | Run | What it proves |
|---|---|---|
| **Your pack's data** (a primitive, a parameter, a rate-sheet ref) | `make pack-validate PACK=…` | Your YAML is sound against the schemas + pack bounds (depths 1–4). |
| **A family schema** (`.cue`) — or you want to confirm the schema is itself sound | `make contracts-check` | The schema compiles, is canonically formatted, and accepts/rejects its fixture corpus. **No pack data involved.** |

`make contracts-check` runs [`contracts/cue/check.sh`](../../../contracts/cue/README.md):
it has no pack and resolves no pack-bound primitive. It exercises every fixture
under [`contracts/cue/testdata/term-deposit/`](../../../contracts/cue/testdata/term-deposit/)
— each `valid/` file must be accepted, each `invalid/` file must be rejected.

As a config author you will usually run `make pack-validate`. Run
`make contracts-check` when a schema change is in play or when a pack failure
makes you suspect the schema, not your data, is the problem.

## How to read a failure

The validator names the file, the failing definition, and the CUE diagnostic.
Two common shapes:

**An unknown or misspelled field — rejected at depth 1.** The family schema is a
**closed** CUE definition: a field it does not declare is not "ignored", it is an
error. So a typo like `principal_bonds:` or a stray `promo_flag: true` fails
immediately with `field not allowed`. This is the structural "no escape hatch"
guarantee — there is no passthrough for unmodelled fields.

**A free-string pack-bound value — rejected because it is not a real reference.**
A field like `day_count` must be a dotted, jurisdiction-namespaced reference
(`pt.act_360`), because the actual convention is supplied by the pack. Writing
`day_count: "Act/365"` does not "set Act/365" — it fails, because that is a bare
string, not a pack-bound primitive reference.

### The fixture catalogue is your worked list of "what each rule rejects"

The single best reference for failure messages is the invalid-fixture directory:
[`contracts/cue/testdata/term-deposit/invalid/`](../../../contracts/cue/testdata/term-deposit/invalid/).
Each file isolates **one** rule violation, named in its filename and explained in
a comment at the top — for example `unknown-field.yaml` (the `promo_flag` escape
hatch), `unbound-day-count.yaml` (the `"Act/365"` free string),
`non-eur-currency.yaml`, `principal-max-below-min.yaml`, `malformed-version-key.yaml`.

To *see* a message rather than read about it, make a deliberately broken copy and
validate it directly with `cue vet`:

```sh
cp -f contracts/cue/testdata/term-deposit/valid/flat-at-maturity.yaml /tmp/broken.yaml
# edit /tmp/broken.yaml — e.g. add `promo_flag: true`, or set day_count: "Act/365"
cue vet -d '#TermDeposit' /tmp/broken.yaml \
    contracts/cue/common.cue contracts/cue/families/term-deposit.cue
```

The diagnostic you get is the same one `make pack-validate` (and CI) will print —
now you know what that rule's failure looks like before it ever blocks a PR.

## Honest local gaps

`make pack-validate` is real and offline, but a few things deliberately do *not*
run locally yet — do not read green here as "fully published-ready":

- **Depth-5 simulation** is a logged skip until the engine generates
  `expected-events.yaml` (above).
- **Signing** (`cosign`) and **publishing to an authenticated production
  registry** are not part of this loop. Local signing needs your own
  `COSIGN_KEY`; keyless OIDC signing in CI is not wired yet. Only an
  unauthenticated local OCI registry (`localhost:5001`) is documented.
- **Cross-artefact rate-sheet invariants** (every `product_id` exists in an
  active config; every active config's `rate_ref` is covered) are **not** yet
  enforced — they need a product-config registry that does not exist yet. Rate
  sheets are deployed separately via the treasury-gated `POST /v1/rate-sheets`
  API, not as pack files; see
  [ADR-PC-008](../../product-management/product_concepts/adrs/ADR-PC-008-rate-sheet-storage-and-deploy-api.md).
- The pack manifest, primitives, parameters, and signing model are governed by
  [ADR-PC-007](../../product-management/product_concepts/adrs/ADR-PC-007-signed-yaml-oci-pack.md).

## Related

- New to packs? Start with
  [Tutorial: author your first pack](../tutorials/author-your-first-pack.md).
- Want to understand *why* the schema rejects what it rejects — closed structs,
  pack-bound references, the validator depths? See
  [Explanation: reading a CUE schema](../explanation/reading-a-cue-schema.md).
- Back to the [product-docs front door](../README.md).
