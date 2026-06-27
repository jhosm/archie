---
name: review
description: >-
  Run the babelstone domain-review agents over the current change before a PR.
  Gets the diff, DISCOVERS the §P3 review subagents from disk (it does not
  hardcode the roster — drop a new agents/*.md and it is picked up
  automatically), routes the diff to the agents whose stated scope it touches,
  spawns those in parallel, and synthesizes one consolidated advisory report
  (per-agent PASS | CHANGES REQUESTED + a roll-up). Advisory and read-only — it
  never blocks, edits, commits, or pushes. Use when the user wants to review the
  current diff/branch with the babelstone reviewers, asks to "run the review
  agents", or is about to open a PR on engine/contract/docs work.
---

# review — fan the change across the babelstone domain reviewers, then synthesize

The model-invoked half of the §P3 review layer ([ADR-PC-020](../../../../docs/product-management/product_concepts/adrs/ADR-PC-020-llm-toolchain-and-conformance-governance.md)).
The [agents](../../agents/) each guard one judgement-class; this skill runs the *right* ones
over a change in a single pass and rolls their verdicts up, so "review before the PR" is one
command instead of hand-picking reviewers. It is a **dev-time judgement layer, advisory and
read-only** — like the agents themselves it is not a hard gate (§P3); it surfaces, you decide.

**Do not hardcode the agent roster.** This skill discovers agents from disk and routes by
their frontmatter `description`. That is the same field that drives each agent's in-session
`Use PROACTIVELY` auto-spawn, so the two firing paths share one source of truth and **adding a
reviewer is a single file drop** under [`agents/`](../../agents/) — no edit here, no edit to
the [B reminder hook](../../hooks/scripts/surface-review-reminder.sh).

## Step 1 — Get the change

Resolve the diff against the PR base (fall back as needed):

```bash
git -C "$(git rev-parse --show-toplevel)" diff --merge-base origin/main --stat
git -C "$(git rev-parse --show-toplevel)" diff --merge-base origin/main --name-only
```

If there is no diff vs `origin/main` (e.g. work is unpushed/uncommitted), fall back to
`git diff HEAD` then `git diff --staged`. List the changed files; if nothing is changed,
say so and stop.

## Step 2 — Discover the reviewers

Glob `plugins/babelstone-engine/agents/*.md` and read each file's YAML frontmatter `name`
and `description`. The `description` states **when that agent applies** (the "Use
PROACTIVELY when a change touches …" clause). Build the candidate roster from what is on
disk — never from a list written here, which would rot the moment a reviewer is added.

## Step 3 — Route the diff to the agents in scope

For each discovered agent, decide from its `description` whether the change is in its scope —
match the changed paths and the nature of the change against the agent's stated triggers.
Representative routing (illustrative, NOT the source of truth — the descriptions are):

- **`code-comment`** — any diff that adds/changes hand-written comments in `engine/`,
  `families/`, `orchestrator/`, `acl/`, `notification/`, `mcp-server/`, `pack-validate/`.
- **`adr-conformance`** — `engine/`, `families/`, `orchestrator/`, `acl/`, `notification/`,
  `mcp-server/`, `contracts/`, `pack-validate/`, or any `docs/**/adrs/` file.
- **`contract-reviewer`** — an Avro/CUE schema, an EventCatalog entry, an event shape/name,
  the envelope, or anything crossing a bounded context.
- **`financial-math-reviewer`** — interest/withholding/rate/day-count/`Money` math.
- **`replay-determinism-auditor`** — an event handler, projection, replay path, or lifecycle
  state machine.
- **`doc-consistency`** — `docs/**` prose/ADR/README or a C4 `.puml`/`.svg`.

Skip agents the diff doesn't touch (don't spend a financial-math review on a docs-only diff).
If nothing matches, report that and stop.

## Step 4 — Spawn the matched reviewers in parallel

Spawn each matched agent as `subagent_type: babelstone-engine:<name>` **in a single message**
(parallel). Give each the same base-diff command and the change summary, and ask for its
canonical `PASS | CHANGES REQUESTED` verdict plus findings. Each agent owns its own lane and
defers out-of-lane concerns, so overlap is minimal.

## Step 5 — Synthesize one report

Collect the verdicts and produce a consolidated, plain-English-first report:

- **Roll-up** — overall `PASS` only if every spawned agent passed; otherwise
  `CHANGES REQUESTED`, listing which agents and the headline of each.
- **Per agent** — its verdict and findings (`file:line`), de-duplicated where two agents
  flag the same line (note both lenses rather than repeating).
- **Remediation** — for an ADR contradiction an agent surfaces, point at the `amend-adr` /
  `supersede-adr` skills (the §P9 one-command acknowledgments); for code findings, the fix.

## Guardrails

- **Advisory, never gating.** This skill reports; it does not block, edit, commit, or push.
  An LLM reviewer can miss or invent (§P3 Residual risks) — the mechanical CI gates remain
  authoritative.
- **Discover, don't hardcode.** The roster comes from disk every run. If you find yourself
  editing a list of agents in this file, stop — add the agent under `agents/` instead.
- **Route, don't shotgun.** Only spawn agents whose stated scope the diff touches; an
  irrelevant reviewer is wasted tokens and noise.
- **Central spawn.** Run from the main session (it can spawn `babelstone-engine:*` agents);
  a Workflow lane agent cannot, so it defers to the orchestrator to run this centrally.
