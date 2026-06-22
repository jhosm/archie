---
name: parallel-backlog-orchestrator
description: >-
  Survey the ready bd backlog, carve it into the maximum set of collision-free lanes, then
  dispatch one isolated worktree agent per lane and keep the queue saturated — retrying crashed
  agents, nudging stale GitHub mergeability caches, and rebasing on conflict — until no ready
  work remains. ONE central Dolt writer (the orchestrator) owns every bd write; lane agents
  never touch bd or push to `main`. Hands finished lanes to `post-merge-cleanup`. Use when the
  user wants to parallelise the backlog, run several ready issues at once, ship multiple PRs in
  one go, or "run ready work in lanes" — the orchestration companion to `post-merge-cleanup`.
---

# parallel-backlog-orchestrator — fan ready bd work into collision-free lanes, self-heal, and ship

This project's highest-throughput move is **parallel lanes**: take the issues that are ready *right
now*, split them into groups that can't step on each other, and run each group as its own agent in
its own `git worktree` off `origin/main` — every lane ending in a pushed branch and an open PR.
Done well, one orchestration run lands six or seven PRs at once. This skill is the supervisor that
plans those lanes, dispatches them, and keeps the wheel turning when individual agents crash or
GitHub misbehaves — so you delegate the *outcome* ("parallelise the ready backlog") and get a batch
of green PRs back, plus an honest report of what couldn't be done.

Two properties matter more than speed, and both come straight from `CLAUDE.md` and the bd memories:

> **One writer to bd.** bd's issue state lives in a single Dolt DB. Many writers corrupt it. So the
> **orchestrator is the only process that ever writes bd** — it claims and closes centrally and runs
> the single `bd dolt push` at the end. **Lane agents are forbidden from running any `bd …` write**
> (`update`, `claim`, `close`, `dolt push`); they do code + PR only and report their issue ID back.
> Read bd anywhere with `-C <primary-checkout>`; write it only from the orchestrator.

> **Never to `main`.** Each lane branches off `origin/main` and reaches `main` only via a merged PR
> (Branching & PR Policy). Lanes never commit to `main`, and **this skill never self-merges** —
> merging is the maintainer's call.

## Step 1 — Snapshot the ready work

Read the backlog from the **primary** checkout and freeze a snapshot so the plan can't drift mid-run:

```bash
bd -C <primary> ready --json          # ready = no open blockers; the candidate pool
bd -C <primary> dep tree <epic>       # sanity-check edges if working an epic
```

Drop anything already claimed/in-progress. What remains is the candidate pool for this run. If the
user named specific issues ("k6r8.10 and its upstream blockers"), intersect the pool with that set
and pull in any *ready* blockers they depend on.

## Step 2 — Design the maximum set of collision-free lanes

A lane is a set of issues that can be implemented without colliding with any other lane. Two issues
**collide** if they would edit the same files (or the same tightly-coupled module). Plan by
predicting each issue's likely file footprint from its title/description and a quick `grep`/`bd show`,
then group so footprints are disjoint:

- **Disjoint footprints → separate lanes** (run concurrently).
- **Overlapping footprints → same lane** (run sequentially inside one agent) *or* defer the loser to
  the saturation refill (Step 6) — never two concurrent lanes on the same files.
- **Dependency edge still open → not eligible.** `bd ready` already excludes blocked issues, but
  re-check: a blocker only *closes* at merge (Step 7 / `post-merge-cleanup`), so a dependent issue
  will not become ready until a previous run's PR actually merges. Don't dispatch it now.

Cap concurrency at a sane N (≈4–6) so the host and your attention aren't swamped. Aim for the
*maximum* number of conflict-free lanes up to N.

## Step 3 — Present the lane plan, then confirm before dispatching

Dispatching spawns agents that create worktrees, write code, and open PRs — so **show the plan and
get a go-ahead first** (propose-then-apply, like `post-merge-cleanup`). A compact table:

```
LANE PLAN (N=4 concurrent):
  lane 1  feat/money-cents      bd babelstone-7x2a   engine/src/Money.cs, tests        [no collisions]
  lane 2  docs/payload-notes    bd babelstone-9k4d   docs/.../03-payloads.md            [no collisions]
  lane 3  fix/outbox-retry      bd babelstone-3m8p   engine/src/Outbox/*                 [no collisions]
  lane 4  feat/acl-mapping (×2) bd babelstone-5q1e, babelstone-5q7f   acl/*  [sequential — shared files]
DEFERRED (collide with an in-flight lane, refill when it frees):
  bd babelstone-7x9z   engine/src/Money.cs   (waits on lane 1)
```

## Step 4 — Dispatch one isolated agent per lane

For each lane, the **orchestrator claims the issue centrally** (the only bd write a dispatch makes),
then launches an agent scoped to a fresh worktree:

```bash
bd -C <primary> update <id> --claim                    # orchestrator-only; marks the lane in-flight
git -C <primary> worktree add <abs-path> -b <type>/<short-name> origin/main
( cd <abs-path> && mise trust --yes )                  # new worktrees need this before `mise exec`
```

Then dispatch the agent (the established mechanism is the **Agent tool with `run_in_background: true`
and `isolation: "worktree"`**, or — when the user has opted into orchestration — a **Workflow** whose
stages fan out with `isolation: 'worktree'` and a loop-until-dry control flow). Give every lane agent
the same standing brief:

- Work **only** inside your assigned worktree path; use `git -C <abs-path>` and absolute paths so
  edits can't leak into the primary checkout. Leave the primary checkout untouched.
- Build/test with `mise exec --` (e.g. `mise exec -- dotnet test <one-project-path>` — one path per
  run). Get to green before opening the PR.
- Open exactly one PR. Its body MUST carry all three CI-enforced sections: the `## In plain English`
  lead, the `## ADRs touched/honoured` section, and the `## bd issues closed on merge` section —
  one `- Closes babelstone-<id>` line per issue the lane resolves (or `- None — no bd issue is
  resolved by this PR.`). That last section is the authoritative hand-off `post-merge-cleanup` reads
  to know which `bd close` to run, so the lane's own issue ID **must** appear there. On any diff
  touching `engine/ families/ orchestrator/ acl/ notification/ mcp-server/ contracts/ pack-validate/`
  or `docs/**/adrs/`, run the `babelstone-engine:adr-conformance` agent before pushing.
- **Do not run any `bd …` command, do not push to `main`, do not merge.** Report back: issue ID,
  branch, PR URL, CI state, and anything you couldn't finish.

## Step 5 — Self-heal while the lanes run

Supervise the in-flight set and recover the known, transient failures automatically rather than
aborting the batch:

- **Agent crashed** (API error, `PATH` glitch, git failure) → re-dispatch the same lane once or
  twice; a fresh agent on the same worktree usually proceeds. Surface a lane that crashes repeatedly
  instead of looping forever.
- **GitHub reports a merge conflict that doesn't reproduce locally** (stale mergeability cache) →
  push an empty commit to force recomputation: `git -C <abs-path> commit --allow-empty -m "nudge mergeability" && git -C <abs-path> push`.
- **Real conflict with `main`** (another lane merged first) → rebase the lane:
  `git -C <abs-path> fetch origin && git -C <abs-path> rebase origin/main`, re-run gates, force-push
  the branch (never `main`).

## Step 6 — Keep the queue saturated

As each lane reaches "PR open" or dies, free its slot and refill it from the **DEFERRED** set — the
ready issues that were held back only because their files collided with a now-finished lane (Step 2).
Re-claim, worktree, dispatch (Steps 4–5). Keep N agents busy until the ready, collision-free,
not-yet-dispatched set is empty. (Dependency-blocked issues won't appear here — they unblock only
when a blocker *merges*, which is a later run's input, not this one's.)

## Step 7 — Land, record centrally, hand off

When the queue is drained, the **orchestrator** (never the lanes) reconciles bd and reports:

- Leave each shipped issue **claimed / in-progress** while its PR is open — do **not** close it here.
  Closing happens at merge, and that's `post-merge-cleanup`'s job; closing on PR-open would orphan
  the issue from its still-unmerged branch.
- Run the single `bd dolt push` for all the claims this run made, then the normal session-close
  `git push` of your own (orchestrator) working branch if any.
- Verify the **primary checkout is still clean** (`git -C <primary> status`) — a lane that leaked
  edits into it is a bug to flag, per the worktree-cwd-leak lesson.
- Report: PRs opened (with URLs + CI state), lanes recovered from crashes/nudges, lanes deferred or
  blocked and why. Point the user at `post-merge-cleanup` to retire lanes **after** the maintainer
  merges — this skill opens lanes (each PR naming its issue under `## bd issues closed on merge`);
  that one reads that section to `bd close` and retire the branch/worktree.

## Guardrails

- **One writer to bd.** Only the orchestrator runs `bd` writes (`--claim`, `close`, `dolt push`).
  Lane agents are read-only on bd, via `-C <primary>`. This is the single property that keeps the
  Dolt DB from corrupting under parallelism — never relax it.
- **Collision-free is non-negotiable.** Two concurrent lanes must never touch the same files. When
  unsure whether footprints overlap, serialise them in one lane or defer one — correctness over
  concurrency.
- **One worktree per lane, off `origin/main`, with `mise trust --yes`.** Use `git -C` + absolute
  paths; never let a lane edit the primary checkout. Confirm the primary is clean at the end.
- **Never to `main`, never self-merge.** Lanes branch and PR; merging is the maintainer's call.
- **Propose-then-apply.** Show the lane plan (Step 3) and dispatch on the user's go-ahead.
- **`--parent` takes the full `babelstone-` prefix** if a lane files sub-issues (a bare short ID
  mis-parents). And lanes report deferred work back for the orchestrator to file — they don't write bd.
- **Closing is `post-merge-cleanup`'s job.** This skill opens lanes and leaves issues claimed; each
  lane's PR names its issue under `## bd issues closed on merge`, and the cleanup skill reads that
  section to close them once the PR merges. Don't double-own the lifecycle.
