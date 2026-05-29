# /pack-validate

A **Go** static binary embedding `cuelang.org/go`. Validates a product **variant**
(YAML) against its **CUE family schema** and the pinned **regulatory pack** —
synchronously, at commit time. The purpose-built validator chosen in
[ADR-PC-006](../docs/product-management/product_concepts/adrs/ADR-PC-006-cue-schema-language.md);
the depths come from [feature-design-configuration-authoring §5](../docs/product-management/product_concepts/feature-design-configuration-authoring.md).

- **Build provenance:** in-house (co-located build artefact of the product engine)
- **Runtime / stack:** Go static binary (Go pinned in [`mise.toml`](../mise.toml); CUE 0.16.1)
- **CODEOWNERS:** engine team
- **Path-scoped CI:** `go build ./...` + `go test ./...` (the `pack-validate` job)

The **same single binary** serves three contexts — the PM author's pre-commit
hook, the PR CI gate, and (later) the engine at pack-load — so depth structure,
diagnostics, and budgets never drift between laptop, CI, and production
(ADR-PC-006 S1).

## The four synchronous depths

Validation is *one CLI invocation, four logically-distinct checks*, **layered**
(each assumes the previous passed) and short-circuiting at the first depth that
produces diagnostics. CUE unifies structure, type and range in one pass, so the
binary runs CUE once and **attributes** each finding to a depth; depths 3–4 are
Go-side checks the CUE schema cannot express.

| Depth | Budget | What it checks |
|---|---|---|
| 1 syntactic | < 1 s | variant parses; matches the schema's structural shape — closed-struct "no DSL escape hatch", required fields, disjunction branch (flat XOR stepped …), version-key / pack-binding shape. **No pack needed.** |
| 2 type-check | < 5 s | field types & ranges; **pack-bound primitive resolution** (e.g. `day_count: pt.act_360` names a primitive the pinned pack carries) |
| 3 pack compliance | < 10 s | variant respects the pinned pack: it pins the pack/schema it is validated against; the pack can price its family. *(The numeric `tan ≤ max_consumer_rate_bps` bound fires at constitution after rate-sheet resolution — C.6 — since a variant carries a rate **reference**, not a number.)* |
| 4 regulatory coherence | < 10 s | cross-field invariants CUE can't express element-wise: ascending stepped-rate `from_day`; ascending early-termination `up_to_days` with a single open (`null`) tail last; the PT rule **Act/360 required for a deposit** (rejects Act/365) |

Aggregate budget < 30 s. A depth that overruns is surfaced (`over_budget`) but
does not by itself reject the variant. Depth 5 (simulation) is **not** here — it
runs on the engine substrate in CI (C.3, ADR-PC-006 §P4).

## CLI

Four depth subcommands (each runs depths 1..N), plus `validate` (= the full run)
and `version`:

```
pack-validate syntactic        <variant.yaml>                 # depth 1
pack-validate type             <variant.yaml> --pack <dir>    # depths 1→2
pack-validate pack-compliance  <variant.yaml> --pack <dir>    # depths 1→3
pack-validate regulatory       <variant.yaml> --pack <dir>    # depths 1→4
pack-validate validate         <variant.yaml> --pack <dir>    # alias = regulatory
pack-validate version
```

Flags: `--pack <dir>` (required for depths ≥ 2), `--schema-dir <dir>`
(default: auto-discovered `contracts/cue`, or a pack's bundled `schemas/`),
`--format json|human` (default `human`). Exit `0` conforms · `1` diagnostics ·
`2` usage/toolchain error.

```console
$ pack-validate validate product-configs/dpz_pt_12m.yaml --pack packs/pt.2026.1
  depth 1 syntactic        ok    4ms
  depth 2 type             ok    0ms
  depth 3 pack-compliance  ok    0ms
  depth 4 regulatory       ok    0ms
OK
```

## JSON diagnostic contract

`--format json` emits the **versioned** contract the CI gate and the .NET engine
consume (ADR-PC-006 §P2; Open Action #3). The per-diagnostic shape is
`{depth, path, kind, message, pos}`; the wrapper adds the version stamp and
per-depth budget/timing:

```json
{
  "contract_version": "1",
  "variant": "dpz_pt_12m_act365_deposit",
  "pack": "pt.2026.1",
  "ok": false,
  "depths": [
    {"depth":1,"name":"syntactic","budget_ms":1000,"elapsed_ms":4,"ok":true,"over_budget":false}
  ],
  "diagnostics": [
    {"depth":4,"path":"day_count","kind":"forbidden_day_count","message":"day-count \"act_365\" is not regulatorily permitted for a PT deposit (permitted: act_360)"}
  ]
}
```

Adding a field is forward-compatible; removing or renaming one (or changing a
`kind` string) is breaking and requires a `contract_version` bump. Adding a new
`kind` value is additive **provided consumers treat unknown kinds tolerantly**
(a default branch, never a closed switch) — the same forward-only discipline the
durable bus enforces. The [`internal/diag` contract test](./internal/diag/contract_test.go)
pins the shape.

## Tests

`go test ./...`:
- accept/reject over [C.1's schema fixtures](../contracts/cue/testdata/term-deposit) (depths 1–2) plus this module's pack-aware fixtures in [`testdata/`](./testdata) (depths 2–4) — every reject fixture pinned to the depth+kind it must fail at;
- `TestPackValidateDepthBudgets` — the `PACK_VALIDATE_DEPTH_BUDGETS` fitness function (commitment-catalogue row 10);
- `TestContractShape` — the versioned JSON contract.

## Layout governance

[ADR-PC-019 §P1](../docs/product-management/product_concepts/adrs/ADR-PC-019-repository-strategy-monorepo.md) (path-scoped subtree). The CUE family schemas are the source of truth in [`/contracts/cue`](../contracts/cue) (C.1); a signed pack bundles a digest-pinned copy (C.4).
