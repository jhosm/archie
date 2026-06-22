# Plugin skills — model-invoked procedures

Claude Code skills implementing [ADR-PC-020 §P2](../../../docs/product-management/product_concepts/adrs/ADR-PC-020-llm-toolchain-and-conformance-governance.md):
repeatable, judgement-bearing procedures (the existing `create_backlog` is the template). Most
are **authoring** procedures (scaffold an ADR, an event, a migration, a pack); `post-merge-cleanup`
is a repo-**hygiene** workflow and `parallel-backlog-orchestrator` an **orchestration** workflow, both in the
same shape. Each is a directory with a `SKILL.md` (YAML
frontmatter `name` + `description`, body = the procedure); Claude invokes one when a task matches
its `description`.

These are bundled in the **`babelstone-engine`** plugin (this `skills/` directory), so a
freshly enabled plugin makes every procedure model-invocable — the §P2 layer folded into the
same plugin as the §P3 agents under `bhq.8` (ADR-PC-020 §P4, as amended).

| Skill | Does | Status |
|---|---|---|
| [`new-adr`](./new-adr/SKILL.md) | Scaffold a conformant ADR — dual number-check, shape pick, skeleton, cross-links, Verifiable-commitments seed, README index row | **built (`archie-bhq.6`)** |
| [`amend-adr`](./amend-adr/SKILL.md) | Append a dated amendment to an Accepted ADR (additive; §D5) | **built (`archie-bhq.6`)** |
| [`supersede-adr`](./supersede-adr/SKILL.md) | Replace an Accepted decision with a new ADR + Status flip (§D5) | **built (`archie-bhq.6`)** |
| [`pack-author`](./pack-author/SKILL.md) | Scaffold + publish a pt.YYYY.N regulatory pack (YAML+CUE, depths 1–4, cosign, oras) | **built (`archie-bhq.6`)** |
| [`new-family-schema`](./new-family-schema/SKILL.md) | Scaffold a family's event records + folds + module + lifecycle table + projections + replay tests, modelled on `term_deposit` and wired into the host | **built (`babelstone-bhq.13`)** |
| [`new-event`](./new-event/SKILL.md) | `<Entity><PastParticipleVerb>` naming + C# record/handler + governed Avro + AsyncAPI EventCatalog + headers envelope + BACKWARD registry compat | **built (`babelstone-bhq.13`)** |
| [`new-store-migration`](./new-store-migration/SKILL.md) | Author a forward-only Postgres migration in the right series (engine event-store / saga / family read-model) with the series-specific invariants, numbering (gaps OK, no duplicates), and append-only role grants for new engine tables | **built (`babelstone-6gvs`)** |
| [`bd-lint-fill`](./bd-lint-fill/SKILL.md) | Back-fill the template sections `bd lint` flags — Acceptance Criteria (the structured field) on tasks/features, and a Success-Criteria *decision* on epics — drafting grounded, verifiable criteria; fills only, never re-ranks | **built (`babelstone-6gvs`)** |
| [`post-merge-cleanup`](./post-merge-cleanup/SKILL.md) | After PRs land, sweep every local branch + worktree and retire the merged ones (delete branch local+remote, remove worktree, close the linked bd issue) while **preserving** any lane whose PR is still open; merged/open decided by `gh` PR state, never `git branch --merged`; never force-removes a dirty worktree — the executable half of the `## Cleanup / Post-Merge` convention in `CLAUDE.md` | **built (`babelstone-ppa4`)** |
| [`parallel-backlog-orchestrator`](./parallel-backlog-orchestrator/SKILL.md) | Survey the ready bd backlog, carve it into the maximum set of **collision-free lanes**, dispatch one isolated-worktree agent per lane and keep the queue saturated — retrying crashed agents, nudging stale GitHub mergeability caches, rebasing on conflict — until no ready work remains; **one central Dolt writer** (the orchestrator) owns every bd write while lanes stay read-only on bd and never push to `main`/self-merge; opens lanes and hands them to `post-merge-cleanup` to close on merge | **built (`babelstone-cokv`)** |

## The two engine-coupled skills (formerly deferred)

`new-family-schema` and `new-event` scaffold engine/contract **code structure**, so they
were deferred until `/engine` + `/families` had a real .NET 10 layout to scaffold against —
building them earlier would have invented a layout the real engine build then contradicted,
the exact silent drift the `.5` conformance gate exists to prevent. That blocker is now
**resolved**: the reference family
[`term_deposit`](../../../families/term-deposit/src/Babelstone.Families.TermDeposit/) exists
(event records, pure folds, the `IFamilyModule`, the lifecycle table, four projections, the
governed Avro `.avsc` + AsyncAPI catalogue, replay/fold tests), so both skills now scaffold
against concrete, verified paths and base types (`DomainEvent`, `IFamilyModule`,
`IProjectionModule`, `Money`, `EventEnvelope`) rather than an invented structure. They were
filed as `babelstone-bhq.13`. The other four skills operate on the ADR corpus
(`new-adr`/`amend-adr`/`supersede-adr`) and the pack layout (`pack-author`, ADR-PC-007 §P1).

## How these tie into the explicit-drift gate

`amend-adr` and `supersede-adr` are the §P9 companions the [`adr-conformance` agent](../agents/adr-conformance.md)
recommends: when it finds a genuine contradiction, the remedy is to amend or supersede the
ADR **in the same change** rather than let the drift land silently. These skills make that
a one-command step, so the acknowledgment is cheap enough that nobody skips it. See
[the plugin README](../README.md) for the full gate.
The §P3 agents were packaged into the `babelstone-engine` plugin first (`archie-bhq.14`);
these skills folded into the same plugin as the full versioned bundle under `bhq.8`.
