# Agent Instructions

## Project Nature

This is a **documentation-only repository** named **babelstone** (`github.com/jhosm/babelstone`) —
a reference library for a Portuguese banking ecosystem. There is no build system, no test
runner, and no deployable code. The deliverables are `.md` files organised into three series, all under `docs/product-management/`:

- `docs/product-management/integration_concepts/` — integration architecture patterns (documents `00–11`)
  - `docs/product-management/integration_concepts/adrs/` — Architectural Decision Records selecting concrete tools for each pattern (ADR-000 defines the shared evaluation framework; ADRs 001–012 currently filed)
- `docs/product-management/financial_concepts/` — financial mathematics of banking products
- `docs/product-management/product_concepts/` — core banking product engine: brief (`00–04`), feature-design companions, and the open-questions register

Read `README.md` for the full document map.

## Issue Tracking

This project uses **bd** (beads) for issue tracking. Run `bd prime` for full workflow context.

## Quick Reference

```bash
bd ready              # Find available work
bd show <id>          # View issue details
bd update <id> --claim  # Claim work atomically
bd close <id>         # Complete work
bd dolt push          # Push beads data to remote
```

## Non-Interactive Shell Commands

**ALWAYS use non-interactive flags** with file operations to avoid hanging on confirmation prompts.

Shell commands like `cp`, `mv`, and `rm` may be aliased to include `-i` (interactive) mode on some systems, causing the agent to hang indefinitely waiting for y/n input.

**Use these forms instead:**
```bash
# Force overwrite without prompting
cp -f source dest           # NOT: cp source dest
mv -f source dest           # NOT: mv source dest
rm -f file                  # NOT: rm file

# For recursive operations
rm -rf directory            # NOT: rm -r directory
cp -rf source dest          # NOT: cp -r source dest
```

**Other commands that may prompt:**
- `scp` - use `-o BatchMode=yes` for non-interactive
- `ssh` - use `-o BatchMode=yes` to fail instead of prompting
- `apt-get` - use `-y` flag
- `brew` - use `HOMEBREW_NO_AUTO_UPDATE=1` env var

## Document Conventions

- `docs/product-management/integration_concepts/` documents are numbered `00–11` and intended to be read in sequence
- `docs/product-management/financial_concepts/` documents are standalone references, not sequenced
- `docs/product-management/product_concepts/` documents are numbered `00–04` for the core brief, plus `feature-design-*.md` companions for each architectural sub-topic
- The running example is a Portuguese term deposit system — patterns are general, the example is specific
- Cross-links use relative markdown links. Patterns by location:
  - Between sibling concept docs in the same folder: `./NN-name.md`
  - Between concept docs in different sibling folders (same `docs/product-management/` parent): `../OTHER_FOLDER/NN-name.md`
  - From an ADR to a concept doc (in `integration_concepts/adrs/`): `../NN-name.md`
  - From the top-level README: `./docs/product-management/FOLDER/NN-name.md`
- ADR verdict convention (defined in ADR-000): hard filters return `Pass` / `Pass (conditional)` / `Fail`. A conditional pass requires a named mitigation in the same cell and is restated in Consequences or Residual Risks
- `CLAUDE.md` is the Claude-Code-specific mirror of this file (keep in sync)

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
