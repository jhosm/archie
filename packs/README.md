# /packs

Populated **regulatory-pack** YAML data (`pt.YYYY.N`) — the jurisdiction-scoped
vocabulary. Ships as a **signed OCI artefact** pinned by digest, decoupled from
engine releases.

- **Build provenance:** in-house (config data)
- **Runtime / stack:** signed YAML → OCI — [ADR-PC-007](../docs/product-management/product_concepts/adrs/ADR-PC-007-signed-yaml-oci-pack.md)
- **CODEOWNERS:** **Engine team + regulatory counsel** (one of the three config-surface owners)
- **Cadence:** per regulatory change
- **Path-scoped CI:** `/pack-validate` + `cosign` signature verify + pack-load smoke test

> Status: `pt.2026.1` pack + format tooling have landed (Epic C.4 — `pack.sh`
> build/verify, OCI artefact pulled by digest). Keyless cosign signing in CI
> (Q.5) and the engine-side loader/verifier (C.5) are still pending. Layout
> governed by [ADR-PC-019 §P1](../docs/product-management/product_concepts/adrs/ADR-PC-019-repository-strategy-monorepo.md). The reserved day-one config-data repo split ([candidate C](../docs/product-management/product_concepts/adrs/ADR-PC-019-repository-strategy-monorepo.md)) would extract this path first.

## Packs

- [`pt.2026.1`](./pt.2026.1/) — PT term-deposit pack (the v1 jurisdiction vocabulary).

The format, build/verify tooling, and conventions live in [`pack.sh`](./pack.sh)
and the per-pack READMEs.
