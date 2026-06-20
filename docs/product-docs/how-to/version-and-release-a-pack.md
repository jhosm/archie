# How to version and release a pack

You have an existing pack (`pt.2026.1`) and a regulatory change to ship — a new
withholding rate, a new day-count, a corrected parameter. You do **not** edit the
pack in place: a published pack version is immutable. Instead you cut a **new
version** (`pt.2026.2`, or `pt.2027.1`) that declares what changed relative to the
one it supersedes. This guide is the recipe for that `pt.YYYY.N` lifecycle step.

This is the inner authoring loop only — it gets you a *validated, version-correct*
new pack on your own machine. Signing and publishing it to a registry is a separate
guide ([sign and publish a pack](./sign-and-publish-a-pack.md)), and parts of that
path are not built yet.

## Before you start

- You need the pinned toolchain active (`make bootstrap` then `make doctor`) — it
  brings `cue` and the `pack-validate` binary. If validation later errors with
  "command not found" or a CUE/Roslyn version mismatch, that is almost always a
  toolchain-not-on-PATH problem; re-run `make doctor`.
- New to packs entirely? Do [author your first pack](../tutorials/author-your-first-pack.md)
  first — this guide assumes you already have one.
- The `pack-author` skill scaffolds the whole `pt.YYYY.N` layout and the
  versioning fields for you; reach for it rather than hand-rolling a directory.

## The rule that shapes everything: a published version is immutable

The version key is `pt.YYYY.N` (`<pack_id>.<pack_version>`), and **it is immutable
once published** ([ADR-PC-007 §P1](../../product-management/product_concepts/adrs/ADR-PC-007-signed-yaml-oci-pack.md)).
You never go back and change `pt.2026.1`. Every change — even fixing a typo in a
parameter — is a *new* version with a higher `N` (within the same year) or a new
year. The old version stays exactly as published, because the deposits constituted
under it are pinned to it for life and must keep computing under the rules they were
born with (see [pack effective-date and per-instance
pinning](../explanation/pack-effective-date-and-per-instance-pinning.md)).

So the mental model is *forward-only*: cut a new version, declare your delta, never
overwrite.

## Step 1 — Create the new version directory

Copy the previous version's directory to the new key. The directory name **is** the
version key, and the validator checks the manifest matches it:

```sh
cp -rf packs/pt.2026.1 packs/pt.2026.2
```

(Substitute your own jurisdiction and year. The shape is `<pack_id>.YYYY.N`.)

## Step 2 — Set the versioning fields in `pack.yaml`

Three manifest fields carry the version lineage. Their authoritative shape lives in
the generated [pack manifest format reference](../reference/pack-format/README.md) —
this guide only tells you *how to fill them*, not their types.

- **`pack_version`** — bump it to match the new directory (`"2026.2"`). It must
  match the directory name or validation fails.
- **`based_on_pack_version`** — set it to the version you are superseding
  (`"2026.1"`). It is `null` only for the very first pack in a line — `pt.2026.1`
  itself carries `based_on_pack_version: null`
  ([`packs/pt.2026.1/pack.yaml`](../../../packs/pt.2026.1/pack.yaml)).
- **`delta_summary`** — a human-readable changelog of what changed versus
  `based_on_pack_version`. It is required and must be non-empty. Write it for the
  auditor who will `cat` the manifest: name the regulatory driver, name the
  primitives/parameters you touched, and call out anything deferred. The
  `pt.2026.1` `delta_summary` is a worked example of the expected depth.
- **`pack_effective_from`** — the date the new rules take regulatory effect. Note
  this is **metadata only in v1** — the engine does not branch on it (see the
  [pinning explanation](../explanation/pack-effective-date-and-per-instance-pinning.md#why-pack_effective_from-is-metadata-only-in-v1)).
  Set it correctly for the human record regardless.

## Step 3 — Declare breaking changes (if any)

`breaking_changes` is a list. Leave it `[]` for a purely additive change. Populate
it when adoption of the new version should require an **explicit operator
acknowledgement** rather than silent uptake — for example, removing a primitive an
old variant relied on, or re-typing a field. A non-empty `breaking_changes` is the
signal that *"no silent pack upgrades"* applies: the operating bank has to opt in
deliberately. Each entry is an `{ id, description }` pair; the field shape is in the
[reference](../reference/pack-format/README.md), and the operator opt-in semantics
(adoption ≠ migration) are
[ADR-PC-009 §P4](../../product-management/product_concepts/adrs/ADR-PC-009-per-instance-version-pinning.md).

Most weekly-cadence work is additive and leaves this `[]`. Reach for it when you are
genuinely taking something away or changing its meaning.

## Step 4 — Make your actual change, then validate

Edit the primitive, parameter, or rate-sheet ref that motivated the new version.
Then validate locally — the same offline depths-1–4 gate you already know:

```sh
make pack-validate PACK=pt.2026.2
```

This runs `cue vet` over the manifest and every data file
([how to validate a pack locally](./validate-a-pack-locally.md) covers the depths
and how to read a failure). A clean run ends with `OK`. The validator confirms the
version key matches the directory and that your delta respects the schema and the
pack's own bounds.

> Depth-5 (the sealed-corpus engine simulation) is reported as a logged `skip`, not
> a pass, until the engine generates `expected-events.yaml`. That is expected — see
> [the sealed test corpus](./write-a-sealed-test-corpus.md). It is not a failure of
> your version bump.

## Step 5 — Refresh the test corpus for the new shape

If your change alters what a canonical instance should produce — a new interest
shape, a changed primitive that affects the lifecycle — add or adjust the
hand-authored **inputs** in `test-corpus/canonical-instances.yaml`. You author the
*inputs* only; the `expected-events.yaml` companion is generated, never
hand-written. See [write a sealed test corpus](./write-a-sealed-test-corpus.md) for
exactly which file you touch and which you must not.

## Step 6 — Update the pack README changelog

Each pack version ships a `README.md` that is the human-readable per-version
changelog ([ADR-PC-007 §P1](../../product-management/product_concepts/adrs/ADR-PC-007-signed-yaml-oci-pack.md)).
Record the same delta you wrote in `delta_summary`, with any extra context a
reviewer needs. This is the page a regulator or a colleague reads first.

## What "release" means once validation is green

Locally, a green `make pack-validate` plus a correct `based_on` / `delta_summary` /
`breaking_changes` triple is the version *authored and validated*. Turning that into
a *released* artefact — building the OCI bundle, signing it, and pushing it to a
registry by digest — is the next guide:

➡️ [Sign and publish a pack with cosign and ORAS](./sign-and-publish-a-pack.md)

Be aware that the production publish path (an authenticated OCI registry and CI
keyless signing) is **not fully built yet**; that guide states plainly what works
today versus what is planned.

## Honest gaps

- **Cross-artefact rate-sheet coverage is not enforced at pack-version time.** If
  your new version's rate-sheet ref points at a sheet, the validator does not yet
  confirm that sheet exists and covers the active configs — that needs a
  product-config registry that is a later epic. Rate sheets deploy separately via
  the treasury-gated `POST /v1/rate-sheets` API
  ([ADR-PC-008](../../product-management/product_concepts/adrs/ADR-PC-008-rate-sheet-storage-and-deploy-api.md)),
  not as pack files.
- **The engine-side pack loader/verifier is pending.** "The engine rejects an
  unsigned or wrong-bound pack at load" is a design commitment
  ([ADR-PC-007 §P4](../../product-management/product_concepts/adrs/ADR-PC-007-signed-yaml-oci-pack.md)),
  not yet something you can observe locally.

## Related

- The immutability and per-instance pinning behind "never edit, always new version":
  [pack effective-date and per-instance pinning](../explanation/pack-effective-date-and-per-instance-pinning.md).
- The full pack-format / signing / distribution decision:
  [ADR-PC-007](../../product-management/product_concepts/adrs/ADR-PC-007-signed-yaml-oci-pack.md).
- The validation depths and failure decoder:
  [validate a pack locally](./validate-a-pack-locally.md) and
  [interpret a validation failure](./interpret-a-validation-failure.md).
- The field-level manifest shape (never restated here):
  [pack manifest format reference](../reference/pack-format/README.md).
- Back to the [product-docs front door](../README.md).
