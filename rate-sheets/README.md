# /rate-sheets

Versioned **rate-sheet** data — the numerical rates, on their own fast cadence.

- **Build provenance:** in-house (config data)
- **Runtime / stack:** storage + deploy API — [ADR-PC-008](../docs/product-management/product_concepts/adrs/ADR-PC-008-rate-sheet-storage-and-deploy-api.md)
- **CODEOWNERS:** **Treasury / ALM** (one of the three config-surface owners)
- **Cadence:** daily–weekly
- **Path-scoped CI:** rate-sheet schema validation

A weekly rate change clears treasury sign-off without paying a product-redesign
approval — the point of the three-owner split ([01 §3](../docs/product-management/product_concepts/01-product-architecture.md)).

## Layout

A rate sheet is a **committed YAML file** — the auditor-readable source of truth
that deploy serialises 1:1 into an immutable `rate_sheets` row ([ADR-PC-008 §P1](../docs/product-management/product_concepts/adrs/ADR-PC-008-rate-sheet-storage-and-deploy-api.md)).
Files are grouped by product family; the filename is the `rate_sheet_version_id`,
and a new version is always a new file (forward-only, never an edit in place).

| File | Family | Version |
|---|---|---|
| `term_deposit/pt-deposits-2026.1.yaml` | `term_deposit` | `pt-deposits-2026.1` — the starter sheet the local demo stack runs on |

To author, deploy, and confirm a new version, see
[how to author and deploy a complete rate-sheet version](../docs/product-docs/how-to/author-and-deploy-a-rate-sheet.md).

## Deploying a sheet

Authoring and deploying are **pure YAML** end to end. Deploy a committed file with
the YAML-native deploy tool — it serialises the YAML to JSON 1:1 (pinned `js-yaml`)
and POSTs it with the gateway-attested `X-Deploy-Actor` header:

```sh
make deploy-rate-sheet SHEET=rate-sheets/term_deposit/pt-deposits-2026.1.yaml
# or: scripts/deploy-rate-sheet.sh rate-sheets/term_deposit/pt-deposits-2026.1.yaml \
#       --base-url http://localhost:8080 --actor treasury.analyst@bank.internal
```

The demo scripts (`make demo-mcp` / `make demo-saga` / `make demo`) deploy straight
from the committed file above, so the YAML is the single source and cannot drift from
what the demo runs. Validate a sheet's shape before you deploy (also the CI gate) with
`make rate-sheet-check`. Full loop: [how to author and deploy a complete rate-sheet
version](../docs/product-docs/how-to/author-and-deploy-a-rate-sheet.md).

> Extraction-ready subtree per [ADR-PC-019 §P2](../docs/product-management/product_concepts/adrs/ADR-PC-019-repository-strategy-monorepo.md);
> reserved for the future config-data split once Treasury cadence is observed.
