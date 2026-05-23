# Project Instructions for AI Agents

This file provides instructions and context for AI coding agents working on this project.

## Project Nature

This is a **documentation-only repository** named **babelstone** (`github.com/jhosm/babelstone`) —
a reference library for a Portuguese banking ecosystem. There is no build system, no test
runner, and no deployable code. The deliverables are `.md` files organised into three series, all under `docs/product-management/`:

- `docs/product-management/integration_concepts/` — integration architecture patterns (documents `00–11`)
  - `docs/product-management/integration_concepts/adrs/` — Architectural Decision Records selecting concrete tools for each pattern (ADR-IC-000 defines the shared evaluation framework; ADRs 001–012 currently filed)
- `docs/product-management/financial_concepts/` — financial mathematics of banking products
- `docs/product-management/product_concepts/` — core banking product engine: brief (`00–04`), feature-design companions, and the open-questions register
  - `docs/product-management/product_concepts/adrs/` — Architectural Decision Records for the product engine's own concerns: source-of-truth, configuration surface, runtime, boundary signal contracts, coexistence (ADR-PC-000 defines namespace conventions and the contract-shape template; ADR-PC numbers are independent of ADR-IC numbers)

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


## Shell Commands

Always use non-interactive flags to avoid hanging on confirmation prompts:

```bash
cp -f source dest       # not: cp source dest
mv -f source dest       # not: mv source dest
rm -f file              # not: rm file
rm -rf directory        # not: rm -r directory
```

## Diagrams

- C4 architecture diagrams use **PlantUML** (C4-PlantUML macros). GitHub renders Mermaid but **not** PlantUML, so each `.puml` source under `docs/**/diagrams/` is pre-rendered to a committed `.svg` that the Markdown embeds.
- **After editing a `.puml`, re-render it** so you can check the result: `plantuml -tsvg <file>.puml`. Requires `brew install graphviz plantuml` (full setup in `INSTALL.md`).
- The `.githooks/pre-commit` hook re-renders any staged `.puml` and stages the SVG at commit time (the safety net); activate with `git config core.hooksPath .githooks` (a shim in `.git/hooks/` may already delegate to it).
- Convention: `@startuml <id>` MUST match the `.puml` filename, so output lands at `<filename>.svg` (the hook relies on this).

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
