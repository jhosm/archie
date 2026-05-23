# /pack-validate

A **Go** static binary embedding `cuelang.org/go`. Validates regulatory packs and
product-config structure against their CUE schemas — synchronously, at commit time.

- **Build provenance:** in-house (co-located build artefact of the product engine)
- **Runtime / stack:** Go static binary — [ADR-PC-006](../docs/product-management/product_concepts/adrs/ADR-PC-006-cue-schema-language.md)
- **CODEOWNERS:** engine team
- **Path-scoped CI:** `go build` + `go test`

> Status: skeleton — no source yet. Layout governed by [ADR-PC-019 §P1](../docs/product-management/product_concepts/adrs/ADR-PC-019-repository-strategy-monorepo.md).
