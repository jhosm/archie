<!-- SCAFFOLD PLACEHOLDER — replaced by the generated index once Epic R · R.3/R.4 land. Do not build on this text. -->
# Reference

**Generated, exhaustive, dry.** This tree is the lookup quadrant of the documentation overlay ([ADR-PC-022 §P2](../product_concepts/adrs/ADR-PC-022-product-documentation-architecture.md)). Every page under `reference/` is **rendered from a machine-readable source** and regenerated-and-diffed in CI — it **cannot drift**, and it must never be hand-edited.

```
make docs-gen      # regenerate this tree from its sources
make docs-verify   # regenerate into a scratch tree and diff; non-empty diff fails CI
```

Planned reference sets, one renderer per source kind ([ADR-PC-022 §P2](../product_concepts/adrs/ADR-PC-022-product-documentation-architecture.md)):

| Set | Source | Status |
|---|---|---|
| `events/` | `contracts/avro/**/*.avsc` | _generator: Epic R · R.3/R.4_ |
| `family-schemas/` | `contracts/cue/**/*.cue` | _generator: Epic R · R.3/R.4_ |
| `mcp-tools/` | the `mcp-server` tool surface | _generator: Epic R · R.3/R.4_ |
| `adr-index/` | ADR front-matter (both namespaces) | _generator: Epic R · R.3/R.4_ |
| `pack-format/` | the pack/rate-sheet CUE + [ADR-PC-007](../product_concepts/adrs/ADR-PC-007-signed-yaml-oci-pack.md) layout | _generator: Epic R · R.3/R.4_ |

> Until the generator lands, this directory holds only this placeholder. The generated pages each open with a `DO NOT EDIT — generated` banner.
