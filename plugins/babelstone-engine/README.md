# `babelstone-engine` plugin — domain-specialised review subagents

Claude Code subagents implementing [ADR-PC-020](../../docs/product-management/product_concepts/adrs/ADR-PC-020-llm-toolchain-and-conformance-governance.md)
§P3: *context-isolated review the generic review toolkit does not cover.* Each is a
markdown file under [`agents/`](./agents/) with YAML frontmatter (`name`, `description`,
`tools`) whose body is the agent's system prompt.

They are packaged as the **`babelstone-engine`** plugin (this directory + the repo-root
[`.claude-plugin/marketplace.json`](../../.claude-plugin/marketplace.json)) so each is
**explicitly spawnable** by its namespaced type — `subagent_type: babelstone-engine:<name>`,
or `@babelstone-engine:<name>` to invoke by mention. Packaging is what gives a project
subagent a spawn handle: a loose `.claude/agents/*.md` file has none, which is why these
moved here (`archie-bhq.14`).

| Agent | Guards | Spawn as |
|---|---|---|
| [`adr-conformance`](./agents/adr-conformance.md) | Internal-design drift against the governing ADRs (PC + IC); the explicit-drift gate's judgement layer | `babelstone-engine:adr-conformance` |
| [`financial-math-reviewer`](./agents/financial-math-reviewer.md) | Act/360, TANB/TANL, flow-by-flow withholding, TAE, round-once-at-`Money` | `babelstone-engine:financial-math-reviewer` |
| [`contract-reviewer`](./agents/contract-reviewer.md) | Schema evolution, naming, no-PII-on-bus | `babelstone-engine:contract-reviewer` |
| [`replay-determinism-auditor`](./agents/replay-determinism-auditor.md) | Handler purity, projection rebuildability, fixture replay | `babelstone-engine:replay-determinism-auditor` |
| [`doc-consistency`](./agents/doc-consistency.md) | Cross-linked docs + C4 vs cited source ("the source wins") | `babelstone-engine:doc-consistency` |

These compose *with*, not instead of, the generic `code-review` / `pr-review-toolkit`
skills. The plugin currently bundles only the §P3 agents (pulled forward by `archie-bhq.14`
because a loose subagent cannot be spawned by type); the §P1 hooks and §P2 skills fold in
as the full versioned bundle under `archie-bhq.8` (ADR-PC-020 §P4, as amended).

## Enabling the plugin

The repo declares this marketplace and enables the plugin in [`.claude/settings.json`](../../.claude/settings.json)
(`extraKnownMarketplaces` + `enabledPlugins`), so a freshly cloned + trusted repo is
prompted to install it — no manual step. To wire it up by hand instead:

```
/plugin marketplace add .
/plugin install babelstone-engine@babelstone-engine
```

Plugins register at session start, so the namespaced `subagent_type` becomes spawnable in
the **next** session after install.

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
[`amend-adr`](../../.claude/skills/amend-adr/SKILL.md) / [`supersede-adr`](../../.claude/skills/supersede-adr/SKILL.md)
skills (`archie-bhq.6`) make step 2 a one-command step.

## When to run the conformance agent

Before committing or opening a PR whose diff touches engine/contract code or any
`docs/**/adrs/` file. The agent reads `git diff`, maps the change to its governing
ADRs, and returns a `PASS | CHANGES REQUESTED` verdict. It is dev-time discipline: run
it as part of pre-PR review, alongside the mechanical gates that CI enforces anyway.
