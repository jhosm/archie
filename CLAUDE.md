# Project Instructions for AI Agents

This file provides instructions and context for AI coding agents working on this project.

## Project Nature

This is a **documentation-only repository** named **babelstone** (`github.com/jhosm/babelstone`) —
a reference library for a Portuguese banking ecosystem. There is no build system, no test
runner, and no deployable code. The deliverables are `.md` files organised into two series:

- `integration_concepts/` — integration architecture patterns (documents `00–10`)
  - `integration_concepts/adrs/` — Architectural Decision Records selecting concrete tools for each pattern (ADR-000 defines the shared evaluation framework; ADRs 001–008 currently filed)
- `financial_concepts/` — financial mathematics of banking products

Read `README.md` for the full document map.

<!-- BEGIN BEADS INTEGRATION v:1 profile:minimal hash:7510c1e2 -->
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
   git push
   bd dolt push    # sync beads issue state (Dolt refs) to remote
   git status      # MUST show "up to date with origin"
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

## Document Conventions

- `integration_concepts/` documents are numbered `00–10` and intended to be read in sequence
- `financial_concepts/` documents are standalone references, not sequenced
- The running example is a Portuguese term deposit system — patterns are general, the example is specific
- Cross-links use relative markdown links. Patterns by location:
  - Between sibling concept docs (in `integration_concepts/`): `./NN-name.md`
  - From an ADR to a concept doc (in `integration_concepts/adrs/`): `../NN-name.md`
  - From the top-level README: `./integration_concepts/NN-name.md`
- ADR verdict convention (defined in ADR-000): hard filters return `Pass` / `Pass (conditional)` / `Fail`. A conditional pass requires a named mitigation in the same cell and is restated in Consequences or Residual Risks
- `AGENTS.md` mirrors these instructions for non-Claude-Code agents (keep in sync)
