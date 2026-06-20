---
name: bd-lint-fill
description: >-
  Groom the bd backlog by back-filling the template sections `bd lint` flags as
  missing — Acceptance Criteria on tasks/features (the structured field), and a
  Success Criteria decision on epics — drafting verifiable criteria from each
  issue's own title/description and any linked ADR or Test ID, proposing before
  writing. Use when the user wants to fix bd lint warnings, fill in missing
  acceptance/success criteria, or tidy the backlog. ONE job: fill missing
  sections — never re-rank, re-title, or restructure issues.
---

# bd-lint-fill — back-fill the sections `bd lint` flags

`bd lint` checks open issues for the template sections their type requires (task/feature →
Acceptance Criteria; epic → Success Criteria; bug → Steps to Reproduce + Acceptance Criteria).
As the backlog grows the warnings accrete because nobody owns clearing them. This skill clears
them **carefully** — drafting criteria grounded in each issue's own content, proposing before
writing, and touching nothing else.

> Scope discipline: this skill fills missing sections and **nothing else**. It does not change
> priority, title, type, dependencies, or restructure the description. Those are human calls.

## Step 1 — Survey

```bash
bd lint --json     # { total, results: [ { id, title, type, missing: [ ... ] } ] }
```

Group the results by `type` and by the missing section. Tasks and features missing
**Acceptance Criteria** are the mechanical, safe bulk. Epics missing **Success Criteria** are a
**policy decision** (Step 3), not a back-fill.

## Step 2 — Tasks & features: fill Acceptance Criteria (the structured field)

`bd` stores acceptance criteria in a **structured `acceptance_criteria` field** (not a
`## Acceptance Criteria` body section), and `bd lint` is satisfied once it is populated. Set it
with the native flag — no description surgery:

```bash
bd update <id> --acceptance "<drafted criteria>"
```

For each flagged task/feature:

1. Read the issue: `bd show <id> --json` (the array's `[0]` has `title`, `description`, `design`,
   `dependencies`). Note any ADR (`ADR-PC-0NN` / `ADR-IC-0NN`) or Test ID the description cites.
2. **Draft 2–5 verifiable, checkable criteria** from that content — outcomes a reviewer can
   confirm, in the house style (concrete and falsifiable, e.g. *"`make contracts-check` passes
   with the new fixture"*, *"the emitted event carries `product_config_version`; replay test
   asserts it"*, *"git grep confirms no remaining `Proposed` summary for an Accepted ADR"*).
   Anchor to the cited ADR/Test ID where one exists.
3. **Propose, then apply.** Show the drafted criteria and the exact `bd update … --acceptance`
   command(s). Apply only on the user's go-ahead (default to a dry run / batch preview).
4. **If the issue lacks enough substance to derive honest criteria, skip it** and list it for a
   human — never invent criteria the issue doesn't support. A fabricated checklist is worse than
   an honest warning.

## Step 3 — Epics: decide, don't bulk-fill

**All open epics currently lack Success Criteria** — the convention isn't in use here, so this is
a deliberate decision, not a mechanical gap. There is no structured `--success` flag and no
project-level `bd lint` override (`bd config` covers export/integration/status namespaces only),
so the choices are:

- **Adopt it** — for each epic that genuinely warrants it, append a `## Success Criteria` section
  to the **description**, preserving the existing body:
  ```bash
  bd show <id> --json   # capture the current description
  bd update <id> --body-file -   # pipe back: existing description + the new "## Success Criteria" section
  ```
  Draft epic-level success criteria as outcomes (the epic is done when …), not task checklists.
- **Accept as advisory** — `bd lint` is a local advisory, **not a CI gate** (no workflow runs it),
  so leaving the 18 epic warnings is a legitimate choice. If the team doesn't want Success
  Criteria on epics, say so and move on rather than back-filling 18 boilerplate sections.

Surface this choice to the user; don't silently pick one. Back-fill epics only with a go-ahead,
one batch, after the decision.

## Step 4 — Re-check & let sync happen

```bash
bd lint            # confirm the targeted warnings cleared
```

`bd` auto-exports `.beads/issues.jsonl` after writes; the normal session-close protocol
(`bd dolt push` + `git push`) carries the changes — this skill does not push on its own.

## Guardrails

- **One job only** — fill missing sections; never re-rank priority, re-title, change type, or
  edit dependencies.
- **Grounded, not invented** — criteria come from the issue's own title/description and cited
  ADR/Test ID; if you can't derive them honestly, skip and flag for a human.
- **Tasks/features use `--acceptance`** (the structured field lint reads); **epics** need a
  `## Success Criteria` description section — and a policy decision first.
- **Propose before writing** — default to a preview; apply on the user's go-ahead.
- **bd lint is advisory** — clearing it is hygiene, not a gate; don't manufacture content just to
  silence it.
