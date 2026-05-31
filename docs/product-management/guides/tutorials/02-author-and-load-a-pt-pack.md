# Tutorial 02 — Author and load a PT pack

**Persona:** [Pack author / compliance](../../reading-paths/README.md).
**You will finish with:** the shipped `pt.2026.1` [regulatory pack](../../reference/glossary.md#pack-regulatory-pack) validated, built into an OCI artefact, and re-pulled by digest — using only the real `make` targets, so you understand the author → validate → build → verify loop before you revise a pack.

Prerequisite: [Tutorial 00](./00-bring-up-the-dev-stack.md) (the toolchain — CUE, cosign, oras — is installed by `make bootstrap`). For *why* a pack is signed YAML pulled by digest, and *why* authors never write CUE handlers, this tutorial links the ADRs and the [pack-author skill](../../../../.claude/skills/pack-author/SKILL.md); it does not re-explain them ([guides invariant](../README.md)).

## Step 1 — Read what a pack is, then look at the real one

A pack is **declarative data, not code**: auditor-readable YAML (primitives, parameters, rate-sheet refs, sealed test corpus) plus bundled CUE *constraint* schemas, in the [ADR-PC-007](../../product_concepts/adrs/ADR-PC-007-signed-yaml-oci-pack.md) layout. The CUE language choice is [ADR-PC-006](../../product_concepts/adrs/ADR-PC-006-cue-schema-language.md). The authoring flow — scaffold, validate, sign, publish, pin — is the [pack-author skill](../../../../.claude/skills/pack-author/SKILL.md); follow that skill when you create or revise a pack rather than hand-rolling the layout.

The shipped `pt.2026.1` pack is the worked example. Open it and see the form:

```bash
ls packs/pt.2026.1
```

Expected:

```
README.md  pack.yaml  parameters  primitives  rate-sheet-refs  test-corpus
```

The per-pack changelog and conventions are in [`packs/README.md`](../../../../packs/README.md) and the pack's own `packs/pt.2026.1/README.md`. The [day count](../../reference/glossary.md#day-count) and [withholding](../../reference/glossary.md#withholding) glossary entries name the primitives a pack carries; the closed family schema a pack's data is checked against is in the generated [family-schemas reference](../../reference/family-schemas/term-deposit.md).

## Step 2 — Validate the pack (cue-vet the manifest + data)

```bash
make pack-validate PACK=pt.2026.1
```

`make pack-validate` runs `packs/pack.sh validate packs/pt.2026.1`. It stages the pack, copies in the digest-pinned family schemas, and `cue vet`s every manifest and data file against the constraints. Expected: an `ok` line per file and a final `OK`:

```
== validate pt.2026.1 ==
  ok (#Manifest)  pack.yaml
  ok (#DayCounts)  primitives/day-count.yaml
  ...
  ok            no-silent-gap sweep: all data .yaml covered
OK
```

The no-silent-gap sweep is deliberate: a data file no schema vetted is a *failure*, never a silent skip — the pack must not ship evidence nothing checked. (`PACK` defaults to `pt.2026.1`, so `make pack-validate` alone validates the same pack.)

## Step 3 — Build the pack into an OCI artefact

```bash
make pack-build PACK=pt.2026.1
```

`make pack-build` re-validates, then `oras push`es a deterministic tar layer into a local OCI layout and prints the artefact **digest** on stdout (progress goes to stderr). Expected: the validation lines, then a single `sha256:…` digest line. A pack is always pulled **by digest, never by tag** — that immutability is the whole point ([ADR-PC-007](../../product_concepts/adrs/ADR-PC-007-signed-yaml-oci-pack.md)).

## Step 4 — Verify it (build, then pull-by-digest and re-validate)

```bash
make pack-verify PACK=pt.2026.1
```

`make pack-verify` builds, captures the digest, then pulls the artefact back **by that digest** and re-validates the round-tripped pack — proving the bytes you published are the bytes you get back. Expect it to end on the same `OK`. This is the loop a pack revision goes through before it is signed and published.

> Heads-up: `make pack-validate` / `pack-build` / `pack-verify` are fully offline (OCI layout, no registry, no Docker). The keyless cosign **signing** step and the engine-side loader are later stories — see the status note in [`packs/README.md`](../../../../packs/README.md). Per the [pack-author skill](../../../../.claude/skills/pack-author/SKILL.md), never sign or publish a pack that has not passed validation: the signature *is* the attestation that it did.

## Step 5 — See where the pack feeds the engine

The pack you just validated is exactly the one [Tutorial 01](./01-constitute-a-term-deposit-end-to-end.md) loaded: `make demo-mcp` starts the engine with `Engine__PackVersion=pt.2026.1`, and the deposit's resolved primitives come from this pack. An instance keeps its `pack_version` for life until an explicit migration event — the per-instance pinning is [ADR-PC-009](../../product_concepts/adrs/ADR-PC-009-per-instance-version-pinning.md). The product-side view of what a configured deposit is built from is [v1 scope — term deposits](../../product_concepts/02-v1-scope-term-deposits.md) and [feature-design-configuration-authoring](../../product_concepts/feature-design-configuration-authoring.md).

## What you just did

You drove the full pack loop — author (read the layout) → validate → build → verify-by-digest — against the real shipped pack, using only documented `make` targets. When you revise a pack into a new `pt.YYYY.N` version (a pack is immutable, so a change is always a new version), the [pack-author skill](../../../../.claude/skills/pack-author/SKILL.md) is the authoritative procedure and this loop is its inner cycle.

- **Pack format reference:** the generated [pack-format reference](../../reference/pack-format/README.md).
- **All tutorials:** the [tutorials index](./README.md) · the [guides root](../README.md).
