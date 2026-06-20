# How to sign and publish a pack with cosign and ORAS

This guide covers turning a validated pack into a **signed OCI artefact** in a
registry: `validate → build → push → sign → verify`. The intended end state is that
a pack is pulled by digest, cosign-signed, and the verified signature *is* the
attestation that validation passed
([ADR-PC-007 §P2](../../product-management/product_concepts/adrs/ADR-PC-007-signed-yaml-oci-pack.md),
[ADR-PC-006 §P3](../../product-management/product_concepts/adrs/ADR-PC-006-cue-schema-language.md)).

> ## ⚠️ Provisional page — built vs pending
>
> **The full publish path is not built yet.** This page describes the intended
> workflow and flags, at each step, what works today versus what is planned, so you
> are not misled into thinking signing-and-publishing is a finished, supported
> operation. The honest split:
>
> | Step | Status today |
> |---|---|
> | **`validate`** — `cue vet` the manifest + data (depths 1–4) | **Built.** Fully offline, run by `make pack-validate` and in CI. |
> | **`build`** — pack into an OCI layout, print its digest | **Built.** `make pack-build`; fully offline (oras OCI *layout*, no registry, no Docker). |
> | **`verify`** — pull-by-digest from the local layout + re-validate | **Built.** `make pack-verify`; offline. |
> | **`push`** — copy the built layout into a *real* registry by digest | **Wrapper exists, not wired.** `packs/pack.sh push` needs a running registry; no production registry is operated yet. |
> | **`sign`** — cosign-sign the registry digest | **Wrapper exists, not wired for production.** Keyless OIDC signing in CI is **pending** (bd Q.5). A throwaway-key CI loop exercises the *mechanism* only. |
> | **`verify-signature`** — cosign-verify the signed digest | **Wrapper exists**, same caveat as `sign`. |
> | Engine-side **load + signature verify** at startup | **Pending** (bd C.5). The "engine refuses an unsigned/wrong pack" guarantee is a design commitment, not yet observable. |
>
> In short: **everything offline (`validate` / `build` / `verify`) works today.**
> Anything that needs a registry, production OIDC, or the running engine
> (`push` / `sign` / `verify-signature` against a real registry, and engine
> load-time verification) is **pending**. Where this guide shows those steps, treat
> them as the planned shape, not a path you can run end-to-end against production.

## Before you start

- The pinned toolchain active (`make bootstrap`, `make doctor`).
- Your pack already validates green locally — see
  [validate a pack locally](./validate-a-pack-locally.md). Publishing an unvalidated
  pack is not a thing the workflow allows: `build` runs `validate` first.
- For the registry-touching steps you would additionally need `oras` and `cosign`
  on PATH and a reachable registry. Those steps are the pending ones.

## What works today: validate → build → verify (all offline)

These three commands are real, offline, and require no registry, no Docker, and no
running engine. They are driven through `packs/pack.sh`
([source](../../../packs/pack.sh)) and exposed as `make` targets.

### Build the pack into an OCI layout (and get its digest)

```sh
make pack-build PACK=pt.2026.1
```

This runs `pack.sh build`: it validates the pack, stages the YAML data plus the
digest-pinned `.cue` schemas into a single OCI tar layer (media type
`application/vnd.babelstone.pack.v1+yaml`), writes it into a local **OCI layout**
(an on-disk directory, not a registry), and prints the resulting `sha256:` **digest**.
The digest is the identity you sign and pull by — never a tag
([ADR-PC-007 §P2](../../product-management/product_concepts/adrs/ADR-PC-007-signed-yaml-oci-pack.md)).

### Verify the built layout round-trips by digest

```sh
make pack-verify PACK=pt.2026.1
```

This builds, then **pulls the artefact back by its digest** from the local layout
and re-validates it — proving the bytes you would publish are exactly the bytes that
pass validation. Still fully offline.

At this point you have a validated, digest-identified OCI artefact on disk. That is
as far as the **built** path goes.

## What is planned: push → sign → verify (pending — not runnable against production)

The remaining steps move the artefact into a real registry and attach a cosign
signature. The `pack.sh` subcommands for them **exist as wrappers**, but the
production registry and the keyless-OIDC signing path are **not built yet** — so the
commands below are the *intended* shape, shown for orientation, not a supported
production workflow.

### Push the built layout into a registry, by digest

```sh
# PENDING — needs a running registry; no production registry is operated yet.
packs/pack.sh push packs/pt.2026.1 --registry <registry-ref> --digest <sha256>
```

`push` is `oras cp` from the local OCI layout into a registry, preserving the
digest. It exists because cosign signs a **registry** digest, not an on-disk layout
digest, so the artefact has to land in a registry before it can be signed
([`pack.sh`](../../../packs/pack.sh) header).

### Sign the registry digest with cosign

```sh
# PENDING for production — keyless OIDC signing in CI is not wired (bd Q.5).
packs/pack.sh sign <registry-ref>@<sha256>
```

The **production** signing model is cosign **keyless OIDC** (Sigstore;
engine-team identity in v1, bank-internal OIDC in production —
[ADR-PC-007 §P2](../../product-management/product_concepts/adrs/ADR-PC-007-signed-yaml-oci-pack.md)).
That keyless path is **not wired yet**. The repository's `packs` CI job exercises a
**key-based** cosign loop against a throwaway local registry with a disposable key
pair (set via `COSIGN_KEY`) — but that is a **test of the verify mechanism
end-to-end, not the production publish path**. Read a green CI signature loop as
"the mechanism works", not "packs are being published and signed for production".

### Verify the signature

```sh
# PENDING for production — same caveats as `sign`.
packs/pack.sh verify-signature <registry-ref>@<sha256>
```

A *verified* signature is the attestation that CUE depths 1–4 already passed in CI
([ADR-PC-006 §P3](../../product-management/product_concepts/adrs/ADR-PC-006-cue-schema-language.md)):
**verified-signature ⇒ already-validated**. That is why the engine, at load, does a
structural re-parse plus a signature check rather than re-running full validation —
once that engine-side loader exists (bd C.5; today **pending**).

## Why pull-by-digest and sign-the-digest matter

Two invariants run through the whole workflow and are worth holding even while the
back half is pending:

- **By digest, never by tag.** A tag can be re-pointed; a `sha256:` digest cannot.
  Pulling and signing by digest is what makes a published pack tamper-evident and
  immutable — the same forward-only, never-rewritten property the
  [pinning explanation](../explanation/pack-effective-date-and-per-instance-pinning.md)
  relies on.
- **The signature carries the validation guarantee.** Because the signature attests
  that depths 1–4 passed, the engine can trust a verified pack without re-validating
  it on every load. This is the `CI-validates → cosign-signs → engine-trusts`
  pattern.

## If you only need to author and validate

You do not need any of the pending steps to do day-to-day pack work. Authoring a new
version and validating it locally is complete and supported today — see
[version and release a pack](./version-and-release-a-pack.md) and
[validate a pack locally](./validate-a-pack-locally.md). The signing/publishing path
above becomes relevant only when the registry and CI signing land.

## Related

- The lifecycle this publishes (cut a version, declare the delta):
  [version and release a pack](./version-and-release-a-pack.md).
- Why a published pack is immutable and pinned per instance:
  [pack effective-date and per-instance pinning](../explanation/pack-effective-date-and-per-instance-pinning.md).
- The format, signing, and distribution decision:
  [ADR-PC-007](../../product-management/product_concepts/adrs/ADR-PC-007-signed-yaml-oci-pack.md).
- The `CI-validates → engine-trusts-signature` pattern:
  [ADR-PC-006 §P3](../../product-management/product_concepts/adrs/ADR-PC-006-cue-schema-language.md).
- The build/verify tooling itself: [`packs/pack.sh`](../../../packs/pack.sh) and
  [`packs/README.md`](../../../packs/README.md).
- Back to the [product-docs front door](../README.md).
