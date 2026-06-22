---
name: post-merge-cleanup
description: >-
  Tidy the repo after PRs land — sweep every local branch and git worktree, find the
  ones whose PR is merged, and retire them: close the linked bd issue, delete the
  branch (local + remote), and remove its worktree. Crucially, PRESERVE any lane whose
  PR is still open, so in-flight sibling work is never destroyed. Use when the user says
  a PR merged, asks for post-merge cleanup, or wants to tidy up stale branches/worktrees
  after merging — the executable half of the `## Cleanup / Post-Merge` convention in
  CLAUDE.md.
---

# post-merge-cleanup — retire merged lanes, preserve open ones

This project does parallel work in **lanes**: each PR gets its own branch, usually in its own
`git worktree` off `origin/main` (see the Branching & PR Policy and `## Cleanup / Post-Merge`
sections of `CLAUDE.md`). After a PR merges, its lane is dead weight — the branch lingers locally,
the worktree directory sits on disk, and the bd issue it closed is still marked open. Left
unattended these accrete until `git worktree list` and `git branch` are a graveyard nobody trusts.

This skill clears that graveyard **safely**. The one rule that matters more than tidiness: a
worktree or branch whose PR is **still open** is live work — never touch it. The whole point of
lanes is that they're independent, so cleaning one must never disturb another.

> Authority for the merged/open decision is **GitHub PR state via `gh`**, not `git branch --merged`.
> The repo squash-merges some PRs, and a squash-merged branch's commits are *not* ancestors of
> `main` — so `git branch --merged` would wrongly report it as unmerged. Ask GitHub who merged.

## Step 1 — Sync the view of the world

```bash
git fetch --prune                 # drop remote-tracking refs GitHub already deleted on merge
git worktree list                 # every lane on disk (path, HEAD, branch)
git branch --format='%(refname:short)'   # every local branch
```

Note the **primary** worktree (the main checkout) and the **current** branch — these are never
candidates. Build the candidate set: every local branch except `main` and the branch currently
checked out in the primary worktree, plus every non-primary worktree.

## Step 2 — Classify each candidate by PR state

For each candidate branch, ask GitHub whether its PR merged:

```bash
gh pr list --head <branch> --state all --json number,state,title,url
```

- **Merged** → a cleanup target (Step 3).
- **Open** (or `draft`) → **preserve**. This is live work; leave the branch, the worktree, and
  any bd issue exactly as they are. Say so explicitly in the summary so the user sees it was a
  deliberate skip, not an oversight.
- **No PR at all / Closed-unmerged** → **preserve and flag** for the user. A branch with no PR
  might be unpushed local work; a closed-unmerged PR was abandoned. Either way, deleting it could
  lose commits — surface it, don't auto-delete.

## Step 3 — Plan the sweep, then confirm before destroying

Cleanup is irreversible (deleted branches, removed worktrees, closed issues), so **present the
full plan first** and apply only on the user's go-ahead. For each merged target, the plan line
shows: the branch, its worktree path (if any), the bd issue it'll close (Step 4), and any blocker
(e.g. a dirty worktree). A compact table is ideal:

```
MERGED — will retire:
  feat/foo   PR #281  wt ../babelstone-foo   bd babelstone-1a2b   [clean]
  fix/bar    PR #279  (no worktree)          bd —  (none found)   [clean]
PRESERVED — still open / no PR:
  feat/baz   PR #284 OPEN                     (live lane, untouched)
  spike/qux  (no PR)                          (flagged — your call)
BLOCKED:
  feat/wip   PR #277  wt ../babelstone-wip    [DIRTY — uncommitted changes; branch + bd kept]
```

Legend: a **BLOCKED** (dirty) target is skipped *whole* — worktree kept, branch kept, bd issue
**not** closed — because its uncommitted work has nowhere else to live.

## Step 4 — Find the bd issue to close

For each merged target, recover the bd ID from the PR title/body (the repo's `(bd <id>)`
convention — IDs look like `babelstone-45c4`, `babelstone-60n8.5`, `sfnt.26`):

```bash
gh pr view <number> --json title,body -q '.title + "\n" + .body'
```

Extract IDs matching `bd <id>` (regex roughly `bd ([A-Za-z0-9._-]+)`). If one is found, the plan
proposes `bd close <id>`. **If none is found, skip the bd step silently** for that target —
plenty of PRs (docs typos, dep bumps) have no issue, and a missing ID is not an error. Never
invent one.

## Step 5 — Execute (on go-ahead), per target

Order matters: remove the worktree before deleting its branch (a branch checked out in a worktree
can't be deleted), and verify the worktree is clean first.

```bash
# a) Worktree — refuse to destroy uncommitted work.
git -C <worktree-path> status --porcelain      # non-empty → DIRTY: skip this whole target, report it
git worktree remove <worktree-path>            # plain remove only — NEVER --force

# b) Local branch — try the SAFE delete first. For a squash-merged branch, `-d` prints
#    "error: the branch '<branch>' is not fully merged" and exits non-zero. That error is
#    EXPECTED and benign here — Step 2 already confirmed via `gh` that the PR merged — so
#    fall back to `-D`. The `-D` is safe ONLY because of that confirmation; never reach
#    this line for a branch whose PR you have not confirmed merged.
git branch -d <branch> || git branch -D <branch>

# c) Remote branch — GitHub usually auto-deletes it on merge, so "the ref is already gone"
#    is the common, benign case. But do NOT blanket-swallow every error: an auth/network
#    failure must not masquerade as "already deleted". Probe first, then delete or report.
if git ls-remote --exit-code --heads origin <branch> >/dev/null 2>&1; then
  git push origin --delete <branch>          # ref still there → delete it
else
  echo "  (remote branch already gone — GitHub auto-delete on merge)"
fi

# d) bd issue — only if Step 4 found an ID.
bd close <id>
```

If a worktree is **dirty**, abandon that target entirely — do **not** delete its branch and do
**not** close its bd issue, even though the PR merged. The branch is the only handle on those
uncommitted changes, and a half-cleaned lane (issue closed, work still on disk) is worse than an
untouched one. Report it under BLOCKED and let the user resolve it.

## Step 6 — Report and let sync happen

Summarize what was retired, what was preserved (and why), and what was blocked. `bd close` writes
to the local Dolt DB and auto-exports `.beads/issues.jsonl`; this skill does **not** push commits
on its own — the normal session-close protocol (`bd dolt push` + `git push`) carries the closes.
Deleted local branches and removed worktrees are local git state and need no push.

> The one push this skill *does* make is `git push origin --delete <branch>` in Step 5(c) — a
> remote **ref deletion**, retiring an already-merged branch. That is categorically different from
> pushing commits or pushing to `main` (which the Branching policy forbids), so it does not
> conflict with the "does not push commits" rule above.

## Guardrails

- **Open PR ⇒ untouchable.** The merged/open call comes from `gh`, never `git branch --merged`
  (squash merges defeat it). When in doubt, preserve.
- **Never `git worktree remove --force`.** A dirty worktree is skipped and reported, branch and
  all — uncommitted work is the user's, not yours to discard.
- **Never delete the primary worktree or the current/`main` branch.** They're excluded from the
  candidate set in Step 1.
- **No-PR / closed-unmerged branches are flagged, not deleted** — they may hold unpushed commits.
- **bd close is best-effort and propose-first** — close only the ID parsed from the PR; if there's
  no `(bd <id>)`, skip silently rather than guess.
- **Destructive steps are propose-then-apply** — show the full plan (Step 3) and act on the user's
  go-ahead; default to the dry-run summary.
- **This skill cleans up; it does not push** — `bd dolt push` / `git push` stay with session close.
