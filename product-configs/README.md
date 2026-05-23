# /product-configs

Product-team-authored **variant configurations** — the *structure* artefact of the
three-owner configuration surface ([01 §3](../docs/product-management/product_concepts/01-product-architecture.md)).
Declarative variants layered on a family schema (`/families`) and a chosen pack
(`/packs`); numerical rates come from `/rate-sheets`.

- **Build provenance:** in-house (config data, not engine code)
- **CODEOWNERS:** **Product team** (one of the three config-surface owners)
- **Cadence:** days–weeks
- **Path-scoped CI:** structural validation via `/pack-validate` against the family schema + active pack

This path exists so the three-owner split is enforceable by `CODEOWNERS`: the
cheapest, most frequent change (a variant) does not inherit the most expensive
approval (the pack). See [ADR-PC-019 F2](../docs/product-management/product_concepts/adrs/ADR-PC-019-repository-strategy-monorepo.md).

> Status: skeleton — no data yet.
