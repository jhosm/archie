# Project skills — model-invoked authoring procedures

Claude Code skills implementing [ADR-PC-020 §P2](../../docs/product-management/product_concepts/adrs/ADR-PC-020-llm-toolchain-and-conformance-governance.md):
repeatable, judgement-bearing authoring procedures (the existing `create_backlog` is the
template). Each is a directory with a `SKILL.md` (YAML frontmatter `name` + `description`,
body = the procedure); Claude invokes one when a task matches its `description`.

| Skill | Does | Status |
|---|---|---|
| [`new-adr`](./new-adr/SKILL.md) | Scaffold a conformant ADR — dual number-check, shape pick, skeleton, cross-links, Verifiable-commitments seed, README index row | **built (`archie-bhq.6`)** |
| [`amend-adr`](./amend-adr/SKILL.md) | Append a dated amendment to an Accepted ADR (additive; §D5) | **built (`archie-bhq.6`)** |
| [`supersede-adr`](./supersede-adr/SKILL.md) | Replace an Accepted decision with a new ADR + Status flip (§D5) | **built (`archie-bhq.6`)** |
| [`pack-author`](./pack-author/SKILL.md) | Scaffold + publish a pt.YYYY.N regulatory pack (YAML+CUE, depths 1–4, cosign, oras) | **built (`archie-bhq.6`)** |
| `new-family-schema` | Scaffold a family's event types + handlers + projections + lifecycle + fixtures | deferred → `archie-bhq.13` |
| `new-event` | `<Entity><PastParticipleVerb>` naming + Avro + EventCatalog + envelope + backward-compat | deferred → `archie-bhq.13` |

## Why two are deferred

`new-family-schema` and `new-event` scaffold engine/contract **code structure that does
not exist yet** — no base types, namespaces, `.csproj`, no `term_deposit` reference family
(the engine is a skeleton; its build, ADR-PC-010's implementation, is outside the current
toolchain epic). Building them now would invent a layout the real engine build then
contradicts — the exact silent drift the `.5` conformance gate exists to prevent. They are
filed as `archie-bhq.13`, gated on `/engine` + `/families` having a real .NET 9 layout to
scaffold against. The four shipped skills operate on substrate that **does** exist: the
rich ADR corpus (`new-adr`/`amend-adr`/`supersede-adr`) and the fully-specified pack
layout (`pack-author`, ADR-PC-007 §P1).

## How these tie into the explicit-drift gate

`amend-adr` and `supersede-adr` are the §P9 companions the [`adr-conformance` agent](../agents/adr-conformance.md)
recommends: when it finds a genuine contradiction, the remedy is to amend or supersede the
ADR **in the same change** rather than let the drift land silently. These skills make that
a one-command step, so the acknowledgment is cheap enough that nobody skips it. See
[`../agents/README.md`](../agents/README.md) for the full gate. Once stable, the skills
fold into the `babelstone-engine` plugin (`archie-bhq.8`).
