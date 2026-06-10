# pt.2026.1 — PT term-deposit pack

The first regulatory pack in the PT line (ADR-PC-007). Jurisdiction-scoped
vocabulary the engine resolves against at constitution and pins per instance
for life (surface §3.5). **Declarative data, not code.**

## Contents

| Path | What |
|---|---|
| `pack.yaml` | Manifest — identity, metadata, deps, pins (§P1) |
| `primitives/day-count.yaml` | Day-count conventions (default `act_360`) |
| `primitives/withholding.yaml` | 28% IRS on deposit interest (`irs_juros`) |
| `primitives/fgd.yaml` | Deposit-guarantee-fund coverage (€100k) |
| `primitives/reporting.yaml` | Regulatory reporting hooks (BdP retail-rate stats + FGD coverage active) |
| `parameters/constants.yaml` | Pack-level scalar constants |
| `rate-sheet-refs/deposits-pt.yaml` | Version-pinned rate-sheet refs (ADR-PC-008) |
| `test-corpus/` | Sealed regression evidence (§3.9) |
| `schemas/` | **Not committed** — staged at build from `/contracts/cue` |

## Version key

`pt.2026.1` = `<pack_id>.<pack_version>`. **Immutable once published.** The
engine pins it per instance via the event envelope (ADR-PC-009); a correction
ships as a new version, never an in-place edit.

## Changelog

- **2026.1** — Initial pack. Act/360 day-count, 28% IRS withholding, FGD
  coverage, BdP retail-rate statistics hook. No prior pack.

## Building & verifying

```sh
make pack-validate PACK=pt.2026.1  # stage schemas + cue-vet manifest & data
make pack-build    PACK=pt.2026.1  # validate + oras push, prints the digest
make pack-verify   PACK=pt.2026.1  # build, then pull by digest + re-validate
```

The build copies the digest-pinned family schemas from `/contracts/cue` into a
staging `schemas/` directory so the artefact is self-contained, then packages a
deterministic OCI artefact (media type `application/vnd.babelstone.pack.v1+yaml`)
pulled by digest, never by tag (ADR-PC-007 §P2). Keyless cosign signing in CI
(OIDC) lands with **Q.5**; `pack.sh` carries the `sign`/`verify-signature`
commands for it.
