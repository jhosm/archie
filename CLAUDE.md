# Project Instructions for AI Agents

This file provides instructions and context for AI coding agents working on this project.

## Project Nature

This is a **hybrid docs + code monorepo** named **babelstone** (`github.com/jhosm/babelstone`) —
a Portuguese banking ecosystem reference combining architecture docs with a working product-engine
implementation. The docs deliverables are `.md` files organised into three series, all under `docs/product-management/`:

- `docs/product-management/integration_concepts/` — integration architecture patterns (documents `00–11`)
  - `docs/product-management/integration_concepts/adrs/` — Architectural Decision Records selecting concrete tools for each pattern (ADR-IC-000 defines the shared evaluation framework; latest filed: ADR-IC-014 — check the `adrs/` directory for the current ceiling)
- `docs/product-management/financial_concepts/` — financial mathematics of banking products
- `docs/product-management/product_concepts/` — core banking product engine: brief (`00–04`), feature-design companions, and the open-questions register
  - `docs/product-management/product_concepts/adrs/` — Architectural Decision Records for the product engine's own concerns: source-of-truth, configuration surface, runtime, boundary signal contracts, coexistence (ADR-PC-000 defines namespace conventions and the contract-shape template; ADR-PC numbers are independent of ADR-IC numbers)

**Code components** (all under the repo root):
- `engine/` — C# (.NET 10) event-sourced product kernel
- `families/` — domain family handlers
- `orchestrator/`, `acl/`, `notification/` — .NET boundary services
- `mcp-server/` — Python MCP server (ADR-IC-010)
- `contracts/` — Avro/CUE schemas and fixtures
- `pack-validate/` — Go binary for pack validation (ADR-PC-006)
- `infra/` — Docker Compose dev stack

Read `README.md` for the full document map.

<!-- BEGIN BEADS INTEGRATION v:1 profile:minimal hash:ccf33ec3 -->
## Beads Issue Tracker

This project uses **bd (beads)** for issue tracking. Run `bd prime` to see full workflow context and commands.

### Quick Reference

```bash
bd ready              # Find available work
bd show <id>          # View issue details
bd update <id> --claim  # Claim work
bd close <id>         # Complete work
```

### Rules

- Use `bd` for ALL task tracking — do NOT use TodoWrite, TaskCreate, or markdown TODO lists
- Run `bd prime` for detailed command reference and session close protocol
- Use `bd remember` for persistent knowledge — do NOT use MEMORY.md files

**Architecture in one line:** issues live in a local Dolt DB; sync uses `refs/dolt/data` on your git remote; `.beads/issues.jsonl` is a passive export. See https://github.com/gastownhall/beads/blob/main/docs/SYNC_CONCEPTS.md for details and anti-patterns.

## Session Completion

**When ending a work session**, you MUST complete ALL steps below. Work is NOT complete until `git push` succeeds.

**MANDATORY WORKFLOW:**

1. **File issues for remaining work** - Create issues for anything that needs follow-up
2. **Run quality gates** (if code changed) - Tests, linters, builds
3. **Update issue status** - Close finished work, update in-progress items
4. **PUSH TO REMOTE** - This is MANDATORY:
   ```bash
   git pull --rebase
   bd dolt push
   git push
   git status  # MUST show "up to date with origin"
   ```
5. **Clean up** - Clear stashes, prune remote branches
6. **Verify** - All changes committed AND pushed
7. **Hand off** - Provide context for next session

**CRITICAL RULES:**
- Work is NOT complete until `git push` succeeds
- NEVER stop before pushing - that leaves work stranded locally
- NEVER say "ready to push when you are" - YOU must push
- If push fails, resolve and retry until it succeeds
<!-- END BEADS INTEGRATION -->


## Branching & PR Policy

**All changes — especially LLM-authored ones — reach `main` only by merging a pull request. Never commit or push directly to `main`.**

At the **start** of any work that will change files:

1. Branch off the latest `main`:
   ```bash
   git switch main && git pull --rebase
   git switch -c <type>/<short-name>   # e.g. docs/payload-shape-notes, feat/money-cents
   ```
2. Commit on that branch.
3. At session end, push the **branch** (not `main`) and open a PR — this is the "PUSH TO REMOTE" step of the Session Completion workflow above, where `git push` means *push your working branch*:
   ```bash
   git push -u origin <branch>
   gh pr create --fill   # body MUST carry the "ADRs touched/honoured" section (CI-enforced)
   ```
4. **Merging the PR is the maintainer's call**, not the agent's — do not self-merge unless explicitly told to.

A local `.githooks/pre-push` hook blocks pushes to `main` as a backstop (override a deliberate maintainer push with `ALLOW_PUSH_MAIN=1`). It is a *local* guard, not a hard gate: `--no-verify` bypasses it, and true enforcement needs GitHub branch protection (a Pro plan or a public repo).

## Shell Commands

Always use non-interactive flags to avoid hanging on confirmation prompts:

```bash
cp -f source dest       # not: cp source dest
mv -f source dest       # not: mv source dest
rm -f file              # not: rm file
rm -rf directory        # not: rm -r directory
```

## Dev Stack & Toolchain

Toolchain is pinned in `mise.toml` (.NET 10, Go, Python). Run once per machine:
```bash
make bootstrap   # brew prereqs + mise install
make doctor      # verify pinned versions are active
```

**IMPORTANT — always prefix builds and tests with `mise exec --`:**
The shell Claude runs in may not have the mise-activated environment loaded, so `dotnet`,
`go`, and `python` will resolve to system versions rather than the pinned ones. This causes
cryptic Roslyn analyser version-mismatch errors (e.g. "references version '5.3.0.0' of the
compiler, which is newer than the currently running version '5.0.0.0'"). Always run:
```bash
mise exec -- dotnet build ...
mise exec -- dotnet test ...
mise exec -- go build ...
```

**New git worktrees need `mise trust --yes` before `mise exec` will work in them:**
```bash
git worktree add ../babelstone-foo -b feat/foo
cd ../babelstone-foo && mise trust --yes
```

**`dotnet test` takes exactly one path — not multiple directories.** Run per-project:
```bash
mise exec -- dotnet test engine/tests/Babelstone.OutboxPublisher.Tests/ --nologo -v q
```
Key test locations: `Babelstone.OutboxPublisher.Tests/` has Avro round-trip tests;
`Babelstone.Engine.Tests/` has the `EngineFamilyAgnosticTests` fitness function.

Local dev stack (Redpanda, Postgres, Schema Registry, Kong, OpenBao, Grafana):
```bash
make up          # start stack, wait until healthy
make down        # stop, keep volumes
make reset       # destroy volumes + restart fresh
make verify      # smoke-test: Postgres + Redpanda + SR reachable
make logs        # follow all service logs
```

Other common targets:
```bash
make contracts-check   # CUE schema fmt + fixture validation (ADR-PC-006)
make pack-validate     # validate a regulatory pack (PACK=pt.2026.1)
```

## Diagrams

- C4 architecture diagrams use **PlantUML** (C4-PlantUML macros). GitHub renders Mermaid but **not** PlantUML, so each `.puml` source under `docs/**/diagrams/` is pre-rendered to a committed `.svg` that the Markdown embeds.
- **After editing a `.puml`, re-render it** so you can check the result: `plantuml -tsvg <file>.puml`. Requires `brew install graphviz plantuml` (full setup in `INSTALL.md`).
- The `.githooks/pre-commit` hook re-renders any staged `.puml` and stages the SVG at commit time (the safety net); activate with `git config core.hooksPath .githooks` (a shim in `.git/hooks/` may already delegate to it).
- Convention: `@startuml <id>` MUST match the `.puml` filename, so output lands at `<filename>.svg` (the hook relies on this).

## ADR governance & conformance

The **explicit-drift gate** (ADR-PC-020 §D3): *no change may contradict an Accepted ADR without an amendment or supersession in the same change.* Divergence is allowed; silent divergence is not. Three layers enforce it — see `plugins/babelstone-engine/README.md` for the full composition and the §P9 drift workflow:

- **§D5 immutability** — don't edit an Accepted ADR's `## Decision` in place; append a dated `*Revised YYYY-MM-DD: …*` line or supersede with a new ADR (the `adr-immutability.sh` hook warns; CI hard-fails).
- **PR-body gate** — every PR body MUST carry an "ADRs touched/honoured" section naming the ADRs it implements, amends, or honours (CI-enforced). Review starts from the decision, not the diff.
- **`adr-conformance` subagent** (`plugins/babelstone-engine/agents/adr-conformance.md`, spawnable as `babelstone-engine:adr-conformance`) — run it before committing/PR on any diff touching engine/contract code or `docs/**/adrs/`. It catches internal-design drift no mechanical gate sees and, on a genuine contradiction, proposes the amend/supersede rather than letting it land silently. It is a dev-time judgement *layer* — the mechanical gates + the `commitment-catalogue.md` fitness functions remain authoritative.

**Authoring skills** (`.claude/skills/`, see its README) make the conformant path the easy one: `new-adr` (scaffold with the right shape + dual number-check + Verifiable-commitments seed), `amend-adr` / `supersede-adr` (the §P9 one-command drift acknowledgment), `pack-author` (a `pt.YYYY.N` regulatory pack). Prefer them over hand-rolling these artefacts.

## Document Conventions

- `docs/product-management/integration_concepts/` documents are numbered `00–11` and intended to be read in sequence
- `docs/product-management/financial_concepts/` documents are standalone references, not sequenced
- `docs/product-management/product_concepts/` documents are numbered `00–04` for the core brief, plus `feature-design-*.md` companions for each architectural sub-topic
- The running example is a Portuguese term deposit system — patterns are general, the example is specific
- Cross-links use relative markdown links. Patterns by location:
  - Between sibling concept docs in the same folder: `./NN-name.md`
  - Between concept docs in different sibling folders (same `docs/product-management/` parent): `../OTHER_FOLDER/NN-name.md`
  - From an ADR to a concept doc in its own series (e.g. `integration_concepts/adrs/` → `integration_concepts/NN-name.md`): `../NN-name.md`
  - From an ADR-PC to an ADR-IC (cross-namespace): `../../integration_concepts/adrs/ADR-IC-NNN-…md`
  - From the top-level README: `./docs/product-management/FOLDER/NN-name.md`
- ADR namespaces: **ADR-IC** for shared integration infrastructure (under `integration_concepts/adrs/`); **ADR-PC** for the product engine's own concerns (under `product_concepts/adrs/`). Number spaces are independent. Within each namespace, picking a new number requires a disk + bd dual-check (per bd memory `adr-numbering-check-disk-and-bd`)
- ADR verdict convention (defined in ADR-IC-000; reused by ADR-PC tool-selection ADRs): hard filters return `Pass` / `Pass (conditional)` / `Fail`. A conditional pass requires a named mitigation in the same cell and is restated in Consequences or Residual Risks
- ADR-PC contract-shape ADRs (defined in ADR-PC-000) drop the F1/F2 evaluation table for boundary-contract decisions; rigor comes from six required slots (payload, semantics, ordering, idempotency, error model, ownership/versioning)
- `AGENTS.md` mirrors these instructions for non-Claude-Code agents (keep in sync)
