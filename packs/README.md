# /packs

Populated **regulatory-pack** YAML data (`pt.YYYY.N`) — the jurisdiction-scoped
vocabulary. Ships as a **signed OCI artefact** pinned by digest, decoupled from
engine releases.

- **Build provenance:** in-house (config data)
- **Runtime / stack:** signed YAML → OCI — [ADR-PC-007](../docs/product-management/product_concepts/adrs/ADR-PC-007-signed-yaml-oci-pack.md)
- **CODEOWNERS:** **Engine team + regulatory counsel** (one of the three config-surface owners)
- **Cadence:** per regulatory change
- **Path-scoped CI:** `/pack-validate` + `cosign` signature verify + pack-load smoke test

> Status: skeleton — no data yet. Layout governed by [ADR-PC-019 §P1](../docs/product-management/product_concepts/adrs/ADR-PC-019-repository-strategy-monorepo.md). The reserved day-one config-data repo split ([candidate C](../docs/product-management/product_concepts/adrs/ADR-PC-019-repository-strategy-monorepo.md)) would extract this path first.
