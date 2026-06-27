# `babelstone-engine` plugin — the engine dev toolchain (hooks + skills + review subagents)

**In plain English:** this one plugin bundles the whole developer toolchain for the
babelstone engine — the always-on guard rails that run as you edit (hooks), the
step-by-step authoring procedures the model can invoke (skills), and the specialised
reviewers you spawn before a PR (subagents). Enabling the plugin in a fresh clone gives you
all three in one step, instead of wiring each loose file by hand.

The plugin implements [ADR-PC-020](../../docs/product-management/product_concepts/adrs/ADR-PC-020-llm-toolchain-and-conformance-governance.md)
§P1–§P4 — the §P4 packaging step that folds the §P1 hooks, §P2 skills, and §P3 review
subagents into **one** project plugin, versioned with the repo. It is declared by the
repo-root [`.claude-plugin/marketplace.json`](../../.claude-plugin/marketplace.json).

## What the plugin bundles

### §P1 — Hooks (Claude Code harness hooks) — [`hooks/hooks.json`](./hooks/hooks.json) + [`hooks/scripts/`](./hooks/scripts/)

Deterministic always-rules the harness enforces, each a **fast mirror** of an authoritative
CI gate (never a second source of truth). The plugin's `hooks/hooks.json` wires them via
`${CLAUDE_PLUGIN_ROOT}`, so enabling the plugin installs them — no `.claude/settings.json`
hook entry needed.

| Hook script | Event | Surfaces |
|---|---|---|
| [`adr-immutability.sh`](./hooks/scripts/adr-immutability.sh) | PreToolUse (Edit/Write) | An in-place edit to an Accepted ADR's `## Decision` (§D5 / §D3) — warns; CI hard-fails |
| [`surface-review-reminder.sh`](./hooks/scripts/surface-review-reminder.sh) | PreToolUse (Bash) | On `gh pr create` / `git push -u` whose diff touches governed code/contract/adr paths — reminds to run the [`review`](./skills/review/SKILL.md) skill first (advisory, never gates) |
| [`surface-engine-analysers.sh`](./hooks/scripts/surface-engine-analysers.sh) | PostToolUse | The determinism + `Money`/`decimal` Roslyn gate after an engine/family `.cs` edit (ADR-PC-010 §P1–§P2,§P5) |
| [`surface-pii-on-bus.sh`](./hooks/scripts/surface-pii-on-bus.sh) | PostToolUse | PII-shaped field names in a contract schema — no-PII-on-the-durable-bus (ADR-PC-004/025) |
| [`surface-spec-coverage.sh`](./hooks/scripts/surface-spec-coverage.sh) | PostToolUse | The §P6 ADR↔code↔test coverage checker after an ADR / commitment-catalogue edit |
| [`render-plantuml.sh`](./hooks/scripts/render-plantuml.sh) | PostToolUse | Re-renders an edited `*.puml` to SVG (faster feedback; `.githooks/pre-commit` is authoritative) |
| [`session-push-protocol.sh`](./hooks/scripts/session-push-protocol.sh) | Stop | The mandatory session-close push protocol when work is pending |
| `bd prime` | SessionStart | Loads the bd workflow context at session start |

> **Git hooks vs harness hooks.** The repo also ships **git** hooks at
> [`.githooks/`](../../.githooks/) — `pre-commit` (re-render staged `*.puml`, regenerate the
> generated `reference/` tree) and `pre-push` (block direct pushes to `main`). A Claude Code
> plugin cannot register a *git* hook (git reads `core.hooksPath`, not plugins), so those
> stay in `.githooks/` and are activated once per clone with
> `git config core.hooksPath .githooks` (see [`INSTALL.md`](../../INSTALL.md) / `CLAUDE.md`).
> They are the authoritative renderer/gate that the plugin's `render-plantuml.sh` mirrors.

### §P2 — Skills (model-invoked authoring procedures) — [`skills/`](./skills/)

Repeatable, judgement-bearing procedures Claude invokes when a task matches the skill
`description` — mostly authoring, plus the `post-merge-cleanup` repo-hygiene workflow and the
`parallel-backlog-orchestrator` orchestration workflow. See
[`skills/README.md`](./skills/README.md) for the full table: `new-adr`, `amend-adr`,
`supersede-adr`, `pack-author`, `new-family-schema`, `new-event`, `new-store-migration`,
`bd-lint-fill`, `post-merge-cleanup`, `parallel-backlog-orchestrator`.

### §P3 — Subagents (domain-specialised review) — [`agents/`](./agents/)

Context-isolated review the generic toolkit does not cover. Each is a markdown file under
[`agents/`](./agents/) with YAML frontmatter (`name`, `description`, `tools`) whose body is
the agent's system prompt. Packaging is what gives a project subagent a spawn handle — a
loose `.claude/agents/*.md` file has none, which is why these moved here first
(`archie-bhq.14`).

| Agent | Guards | Spawn as |
|---|---|---|
| [`adr-conformance`](./agents/adr-conformance.md) | Internal-design drift against the governing ADRs (PC + IC); the explicit-drift gate's judgement layer | `babelstone-engine:adr-conformance` |
| [`financial-math-reviewer`](./agents/financial-math-reviewer.md) | Act/360, TANB/TANL, flow-by-flow withholding, TAE, round-once-at-`Money` | `babelstone-engine:financial-math-reviewer` |
| [`contract-reviewer`](./agents/contract-reviewer.md) | Schema evolution, naming, no-PII-on-bus | `babelstone-engine:contract-reviewer` |
| [`replay-determinism-auditor`](./agents/replay-determinism-auditor.md) | Handler purity, projection rebuildability, fixture replay | `babelstone-engine:replay-determinism-auditor` |
| [`doc-consistency`](./agents/doc-consistency.md) | Cross-linked docs + C4 vs cited source ("the source wins") | `babelstone-engine:doc-consistency` |

Spawn one as `subagent_type: babelstone-engine:<name>`, or `@babelstone-engine:<name>` to
invoke by mention. These compose *with*, not instead of, the generic `code-review` /
`pr-review-toolkit` skills.

## Enabling the plugin

The repo declares this marketplace and enables the plugin in [`.claude/settings.json`](../../.claude/settings.json)
(`extraKnownMarketplaces` + `enabledPlugins`), so a freshly cloned + trusted repo is
prompted to install it — no manual step. To wire it up by hand instead:

```
/plugin marketplace add .
/plugin install babelstone-engine@babelstone-engine
```

Plugins register at session start, so the bundled hooks fire and the namespaced
`subagent_type` becomes spawnable in the **next** session after install.

> **One-time settings cleanup when adopting the packaged hooks.** Because plugin hooks
> *merge with* (don't replace) any `.claude/settings.json` hooks and run in parallel, the
> repo-level `.claude/settings.json` must **not** also declare the §P1 hooks — otherwise
> each fires twice. The hook block was removed from `.claude/settings.json` when these
> scripts moved into the plugin (`bhq.8`); `settings.json` now only declares the marketplace
> + `enabledPlugins`. The git hooks under `.githooks/` are unaffected (separate mechanism).

## The explicit-drift gate (ADR-PC-020 §D3)

> **No change may contradict an Accepted ADR without an amendment or supersession in
> the same change.**

Drift is layered, so the gate is three layers — two mechanical and CI-authoritative,
one judgement and dev-time. The `adr-conformance` agent is the third; it does **not**
replace or re-implement the first two:

| Layer | Mechanism | Catches | Authority |
|---|---|---|---|
| 1. §D5 immutability | `adr-immutability.sh` (PreToolUse warn) → `adr-immutability-check.sh` (CI hard-fail) | An Accepted `## Decision` edited in place with no `*Revised …*`/supersession riding along | **CI** (hook is a fast mirror) |
| 2. PR-body gate | `adr-governance.yml` job | A PR body that doesn't name the ADRs it touches/honours | **CI** |
| 3. Conformance agent | [`adr-conformance`](./agents/adr-conformance.md) | Code that compiles and passes contract tests yet **contradicts a decision** — the internal-design class no mechanical gate or boundary test sees | dev-time judgement (a *layer*, not the sole guard — §Residual risks) |

Layer 3 is deliberately **not** a hard CI gate: an LLM reviewer can miss or invent a
contradiction, so the mechanical gates (analysers, determinism gate, Pact, the
[coverage checker](../../.github/scripts/spec-coverage-check.sh)) carry the
load-bearing invariants from the [commitment catalogue](../../docs/product-management/product_concepts/adrs/commitment-catalogue.md);
the agent covers the long tail and *proposes* the fix.

## The drift workflow (§P9) — same shape as the ADR lifecycle

When implementation reveals a decision is wrong or incomplete, the code change and the
decision change land **together**:

1. The agent (or the §D5 hook) flags that the diff contradicts an Accepted ADR.
2. Resolve it one of two ways — **fix the code** to conform, or **amend/supersede the
   ADR** in the same PR (a dated `*Revised YYYY-MM-DD: …*` line, or a new superseding
   ADR with the back-link and Status flip — per [ADR-PC-000 §D5](../../docs/product-management/product_concepts/adrs/ADR-PC-000-namespace-and-contract-shape-framework.md)).
   A deliberate, time-bounded gap is recorded in [04-open-questions](../../docs/product-management/product_concepts/04-open-questions.md).
3. The PR-body "ADRs touched/honoured" section names what the change implements or
   amends, so review starts from the decision, not the diff.

This extends the project's established order — ADR before code, bd issue before code —
to: **no contradiction without a recorded decision.** The
[`amend-adr`](./skills/amend-adr/SKILL.md) / [`supersede-adr`](./skills/supersede-adr/SKILL.md)
skills (`archie-bhq.6`, now bundled in this plugin) make step 2 a one-command step.

## When to run the conformance agent

Before committing or opening a PR whose diff touches engine/contract code or any
`docs/**/adrs/` file. The agent reads `git diff`, maps the change to its governing
ADRs, and returns a `PASS | CHANGES REQUESTED` verdict. It is dev-time discipline: run
it as part of pre-PR review, alongside the mechanical gates that CI enforces anyway.
