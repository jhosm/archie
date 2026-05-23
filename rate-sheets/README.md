# /rate-sheets

Versioned **rate-sheet** data — the numerical rates, on their own fast cadence.

- **Build provenance:** in-house (config data)
- **Runtime / stack:** storage + deploy API — [ADR-PC-008](../docs/product-management/product_concepts/adrs/ADR-PC-008-rate-sheet-storage-and-deploy-api.md)
- **CODEOWNERS:** **Treasury / ALM** (one of the three config-surface owners)
- **Cadence:** daily–weekly
- **Path-scoped CI:** rate-sheet schema validation

A weekly rate change clears treasury sign-off without paying a product-redesign
approval — the point of the three-owner split ([01 §3](../docs/product-management/product_concepts/01-product-architecture.md)).

> Status: skeleton — no data yet. Extraction-ready subtree per [ADR-PC-019 §P2](../docs/product-management/product_concepts/adrs/ADR-PC-019-repository-strategy-monorepo.md); reserved for the future config-data split once Treasury cadence is observed.
