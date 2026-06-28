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

## Step 1 — Snapshot the ready work *and* the repo's in-flight state

Two snapshots, frozen together so the plan can't drift mid-run. The first is bd's view of what's
ready; the second is the repo's *actual* working state, because the collisions that bite hardest are
the ones bd never sees.

**1a — the bd candidate pool.** Read the backlog from the **primary** checkout:

```bash
bd -C <primary> ready --json          # ready = no open blockers; the candidate pool
bd -C <primary> dep tree <epic>       # sanity-check edges if working an epic
```

Drop anything already claimed/in-progress. **Drop every epic.** `bd ready` returns epics
(containers) mixed in with leaf issues — one real run had 17 epics among 53 rows — and an epic is
not a dispatchable unit of work. Dispatch only **leaf issues** (`type: task` / `type: feature`);
never dispatch an epic. **Also drop the issues that are not *agent-dispatchable* at all** — pure
human / account-bound work an autonomous lane can neither do nor PR: "create a cloud account",
"register a domain", "mint a billable API token", "run the promote against a real cluster". But
apply the inverse carefully: an issue gated on an account that doesn't exist yet is often still
dispatchable, because **artifact authoring is account-independent** — the Helm/kustomize/Terraform
manifests, the CI gate, the migration can be written and statically validated (`kustomize build |
kubeconform`, a unit test) on a laptop, even though the live *apply* waits on the account. Authoring
≠ applying: dispatch the authoring slice; leave the apply for the human. What remains is the bd
candidate pool. If the user named specific issues ("k6r8.10 and its upstream blockers"), intersect
the pool with that set and pull in any *ready* blockers they depend on.

**1b — the repo's in-flight no-go regions (bd-independent).** bd status is *not* the full picture of
what's already being worked on. The no-go regions that bite hardest are often invisible to bd: a
worktree with **uncommitted edits** (no claim, no PR) and a branch **pushed with no open PR** (no
claim). Both collide with a fresh lane exactly as hard as an in-progress issue would, and neither
shows up in `bd ready`. So enumerate the working state too, and treat every file it touches as a
no-go region **regardless of PR or bd status**:

```bash
git -C <primary> fetch origin                            # so origin/* refs are current first
git -C <primary> worktree list                           # every checked-out tree
git -C <primary> branch -a                                # local + remote branches
# per branch — the files it AUTHORED vs main (footprint command; see Step 2). Use THREE dots:
git -C <primary> diff --name-only origin/main...<branch>
# per worktree — its uncommitted edits (staged + unstaged + untracked):
git -C <worktree-path> status --porcelain
```

The union of those file sets is the run's **no-go regions**. Any candidate whose predicted footprint
(Step 2) overlaps a no-go region is deferred, exactly as if it collided with an in-flight lane.
Collision-freedom is defined against the **whole repo's working state**, not against bd.

## Step 2 — Design the maximum set of collision-free lanes

A lane is a set of issues that can be implemented without colliding with any other lane. Two issues
**collide** if they would edit the same files (or the same tightly-coupled module). Plan by
predicting each issue's likely file footprint from its title/description and a quick `grep`/`bd show`,
then group so footprints are disjoint:

- **Disjoint footprints → separate lanes** (run concurrently).
- **Overlapping footprints between two *ready* issues → PACK them into one sequential lane, this
  run.** This is the default, not a fallback: one agent does both issues in order on one branch and
  opens a **combined PR** that names both under `## bd issues closed on merge` (one
  `- Closes babelstone-<id>` line each). Sequential packing is exactly how you ship file-colliding
  work *in the same run* instead of losing it. (Two issues that share files but are large/unrelated
  may instead open two stacked PRs — the second branched off the first — but a combined PR is simpler
  and the default.) What you must NOT do is run two **concurrent** lanes on the same files.
- **A footprint that overlaps a Step 1b no-go region → defer** it just as you would an inter-lane
  collision. An uncommitted worktree or a pushed-but-unmerged branch owns those files now.
- **Distinguish "defer to a future run" from "pack now".** Only one situation forces deferral to a
  *later* run: a dependency on a PR that has **not merged yet** — `bd ready` excludes open-blocker
  issues, but re-check, because a blocker only *closes* at merge (Step 7 / `post-merge-cleanup`), so a
  dependent won't become ready until a previous run's PR actually merges. A plain **file-collision
  with another ready issue is NOT a deferral** — it packs into a sequential lane (previous bullet). If
  you find yourself deferring a ready, dispatchable issue purely because its files collide with a lane
  in *this* run, you are losing throughput: pack it instead.

**Compute a branch's footprint with three-dot `git diff --name-only`, never `git log` and never
two-dot.** When you need the files a branch changed (Step 1b's no-go enumeration, or sizing a
candidate's overlap against an existing branch), use the **three-dot** form:

```bash
git -C <primary> diff --name-only origin/main...<branch>   # what the branch AUTHORED (merge-base diff)
```

Two pitfalls, both seen in real runs:

- `git log origin/main..<branch>` over-counts: commits already merged via another path still show up,
  making a branch look far larger than the files it actually changes.
- **Two-dot `git diff origin/main..<branch>` inflates when the branch is *behind* main.** Two-dot
  compares the two tips, so it reports every file *main* changed since the branch forked as if the
  branch touched it. In one run this made stale comment-pass branches falsely appear to edit the ADR
  ledger (`commitment-catalogue.md`, the `adr-index`), which would have wrongly marked those files
  no-go and blocked an unrelated lane.

The footprint you want is *what the branch authored* — the diff from the **merge-base** — which is
exactly what the three-dot `origin/main...<branch>` reports. Default to three dots for every
footprint computation; the two-dot form only answers "how does the branch tip differ from main's
tip", which is not the footprint.

**Verify every asserted blocker before honouring or dismissing it.** A "BLOCKED" in a title or a
"hard precondition" in a description is a *claim*, not a fact — it may be stale (the blocker already
cleared) or real (the dependency genuinely isn't there), and you can't tell which without checking
current repo + bd state. Both modes show up in practice: one issue's prose precondition was already
satisfied because its named dependency had since closed (stale — dispatch it), while another's
"BLOCKED on `acl/` source" was real because `acl/` held only a Dockerfile (real — keep it blocked).
So for each asserted blocker, check it against ground truth before acting:

```bash
bd -C <primary> show <named-dependency>   # is the blocker issue actually still open?
ls <asserted-missing-path>                # does the prose-required source/dir actually exist yet?
```

Honour a blocker only if the check confirms it; dismiss it only if the check refutes it. Never take
the asserted state on trust.

**Verify your *own* asserted collisions too — an inferred collision is also a claim.** The rule above
guards against trusting a blocker *someone else* asserted; the symmetric trap is trusting a collision
*you* inferred. Before excluding a candidate as "collides with a no-go region / another lane", pull
its **real** footprint (the three-dot diff for a branch; a scoped read-only investigator for an
issue) and check **file-level**, not folder- or assembly-level, overlap. In one run an issue was
excluded by inference — its title and ADR put a new interface "beside" a file in an active no-go
assembly — but its actual footprint was a *new* file plus new projector/store/migration, fully
disjoint from the in-flight edits, and it was high-leverage (it unblocked two downstream issues).
New-file work inside a busy assembly is usually clean: .NET globs `.cs`, so a new file needs no
`.csproj` edit. Exclude a candidate only on a *verified* file overlap, never on a title-or-ADR hunch.

Cap concurrency at a sane N (≈4–6) so the host and your attention aren't swamped. Aim for the
*maximum* number of conflict-free lanes up to N.

**Adding lanes to an already-committed plan — check against the *reserved set*.** Planning is not
always one pass. The user may, after you present the priority lanes, ask you to add more (e.g. "fit
in the `zla1` staging work too"). When you do a second pass, the committed lanes already **own** a set
of files — call it the *reserved set* (the union of every committed lane's footprint, plus the
sequential-packed issues' footprints, plus the shared-ledger rows already claimed). A new candidate
is admissible only if its footprint is disjoint from the **reserved set** *and* the Step 1b no-go
regions — not merely disjoint from the other new candidates. Run the same footprint investigation for
the new candidates, but check each one's overlap against the reserved set explicitly; an issue that
shares even one file (e.g. `DepositsEndpoints.cs`, a shared `kong.yml`, or a committed lane's
catalogue row) with a committed lane is not a new concurrent lane — pack it sequentially into that
lane, or defer it. This keeps the second pass as collision-free as the first.

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

In a **default (non-ultracode) run**, for each lane the **orchestrator claims the issue centrally**
(the only bd write a dispatch makes), then provisions a fresh worktree off `origin/main`:

```bash
bd -C <primary> update <id> --claim                    # orchestrator-only; marks the lane in-flight
git -C <primary> worktree add <abs-path> -b <type>/<short-name> origin/main
( cd <abs-path> && mise trust --yes )                  # new worktrees need this before `mise exec`
```

(**Under ultracode, skip this block's per-lane form**: the Workflow's `isolation: 'worktree'`
provisions the tree, and the `--claim`s are batched up front rather than run per lane — see the
ultracode bullet below.)

**Then launch the lanes — the mechanism depends on the session mode:**

- **Default (no ultracode) — hand-driven Agent tool, pointed at the worktree you just made.** The
  orchestrator owns the worktree in this mode: the `git worktree add <abs-path>` above already
  created it at a known path, so launch each lane with the **Agent tool** (`run_in_background: true`)
  pointed at that **`<abs-path>` as its working directory — do *not* also pass `isolation:
  "worktree"`.** Passing both is contradictory: `isolation: "worktree"` makes the Agent spin up its
  *own* uncontrolled tree, which would orphan the `<abs-path>` you created and break every
  `git -C <abs-path>` self-heal form in Steps 5–6 (those forms are only valid because the
  orchestrator knows the path). One worktree per lane, created by the orchestrator, driven by the
  Agent at that path. Then supervise the in-flight set yourself: Steps 5–6 (self-heal, saturation
  refill) are moves *you* make turn by turn as lanes report back. The claim + worktree + `mise trust`
  above run per lane, as each is dispatched.

  (The **ultracode** path inverts this ownership — there `isolation: 'worktree'` *is* the single
  mechanism: the Workflow's `agent()` provisions and owns each lane's tree, so the orchestrator does
  **not** `git worktree add`, and self-heal runs inside the `agent()`'s own cwd with no external
  `<abs-path>` — see the ultracode bullet and Step 5. The two modes pick *one* worktree owner each;
  what's forbidden is mixing them in a single lane.)

- **Ultracode session — a dynamic Workflow owns the whole lane lifecycle.** When a system-reminder
  confirms the session is running at **ultracode** effort, or the user launched this run with the
  `ultracode` keyword, do **not** hand-drive lanes. Encode the lane lifecycle as a single **dynamic
  Workflow** (the `Workflow` tool) and let its control flow run the wheel:
    - **Understand phase first — Workflow the planning, don't carve lanes solo.** Step 2's footprint
      prediction and Step 1b's no-go enumeration / Step 2's blocker verification are read-only
      investigations, and on a large backlog they're too much to do by hand reliably. So before any
      lane-carving, run an **Understand-phase Workflow**: fan out one read-only investigator
      `agent()` per candidate (each scoped to *that* issue) that returns its predicted file footprint
      (three-dot `git diff --name-only origin/main...<branch>` where a branch already exists) **and** its
      verified-blocker verdict (`bd show` the named dependency, `ls` the asserted-missing path — fix 2
      / fix 4). Collect those into a footprint + verified-blocker map, *then* carve the
      collision-free lanes (Step 2) from that map. Only after the lanes are carved does the lane-
      lifecycle Workflow below fan them out. (These read-only investigators write neither bd nor code,
      so the one-writer-to-bd rule is untouched.)
    - Fan lanes out with one `agent(..., { isolation: 'worktree' })` call per lane, capped at the
      concurrency N from Step 2. `isolation: 'worktree'` provisions each lane's worktree, so the
      per-lane `git worktree add` above is unnecessary — but the lane still runs `mise trust --yes`
      as its first act in the fresh tree (a new worktree needs it before `mise exec`).
    - **Steps 5 and 6 become the script's *dynamic* control flow, not your turn-by-turn supervision:**
      a loop-until-dry that refills a freed slot from the DEFERRED set, retries a crashed lane once or
      twice, nudges a stale mergeability cache, and rebases on a real conflict — all decided at runtime
      inside the script. "Dynamic" = the lane count and refill order emerge as the run unfolds, not a
      fixed up-front fan-out.
    - **The one-writer-to-bd rule survives the move.** A Workflow script has no shell or bd access, and
      its `agent()` lanes *are* lane agents — so neither writes bd. The orchestrator (you, the main
      loop) claims every issue this run will touch — the planned lanes *and* the DEFERRED refill set,
      against the frozen Step 1 snapshot — **centrally up front**, *before* launching the Workflow; the
      Workflow ships code + PRs only; and you reconcile bd **centrally after** it returns (the single
      `bd dolt push`, plus un-claiming anything that never shipped — Step 7). Lane `agent()`s still
      never push `main` and never merge.
    - Still **propose-then-apply** (Step 3): show the lane plan and launch the Workflow only on the
      user's go-ahead.

Either way, give every lane agent the same standing brief:

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
  or `docs/**/adrs/`, the `babelstone-engine:adr-conformance` agent must run — but **in the
  ultracode/Workflow path a lane `agent()` cannot spawn it** (a Workflow agent has no sub-agent
  spawn), so the **orchestrator runs adr-conformance centrally** on each such diff after the Workflow
  returns, before finalising. In the default hand-driven Agent-tool path, the lane runs it before
  pushing if it can; otherwise the orchestrator runs it centrally. Either way it is never skipped —
  only relocated to whoever can actually spawn it. Do **not** tell Workflow lanes to run it themselves.
- **Respect the two shared ledgers (Guardrails has the full rule).** The generated
  `reference/adr-index` is **single-writer** — never regenerate it in a lane unless you are the one
  ADR-touching lane the orchestrator nominated to own the `make docs-gen` regen this run. The
  `commitment-catalogue.md` is **row-addressable**: you may flip **only the one row your issue owns**,
  and only when the orchestrator has confirmed no other concurrent lane touches that same or an
  *adjacent* row — edit that single row's cells, never reformat the table. If you are unsure, or your
  row neighbours another lane's, *describe* the exact row + Planned→Live change and report it back for
  the orchestrator to apply centrally instead.
- **Do not run any `bd …` command, do not push to `main`, do not merge.** Report back: issue ID,
  branch, PR URL, CI state, the catalogue flip you need (if any), and anything you couldn't finish.

## Step 5 — Self-heal while the lanes run

Supervise the in-flight set and recover the known, transient failures automatically rather than
aborting the batch. (In an **ultracode** run the script *decides* these recoveries at runtime — which
lane to retry, when to nudge, when to rebase — but it has no shell of its own: every `git`/`gh`
command below, and the read that detects the conflict, runs inside an `agent()` subagent the script
spawns into the lane's worktree. Only re-dispatching a crashed lane is pure script control flow. The
rules below are what that decide-then-delegate logic implements.)

- **Agent crashed** (API error, `PATH` glitch, git failure) → re-dispatch the same lane once or
  twice; a fresh agent on the same worktree usually proceeds. Surface a lane that crashes repeatedly
  instead of looping forever.
- **GitHub reports a merge conflict that doesn't reproduce locally** (stale mergeability cache) →
  push an empty commit to force recomputation: `git -C <abs-path> commit --allow-empty -m "nudge mergeability" && git -C <abs-path> push`.
- **Real conflict with `main`** (another lane merged first) → rebase the lane:
  `git -C <abs-path> fetch origin && git -C <abs-path> rebase origin/main`, re-run gates, force-push
  the branch (never `main`).

The `git -C <abs-path>` forms above are the **default path** (the orchestrator created the worktree at
a known `<abs-path>`). Under a dynamic Workflow the same recovery runs inside an `agent()` in its own
`isolation: 'worktree'` cwd — so it's plain `git commit --allow-empty … && git push` /
`git fetch && git rebase origin/main`, with no external `<abs-path>` for the script to pass to `-C`.

## Step 6 — Keep the queue saturated

As each lane reaches "PR open" or dies, free its slot and refill it from the **DEFERRED** set — the
ready, **collision-free** lanes that didn't fit because there were more of them than N slots (Step 2),
plus any lane whose files freed because a concurrent lane *died* with no PR. Re-claim, worktree,
dispatch (Steps 4–5). Keep N agents busy until the ready, collision-free, not-yet-dispatched set is
empty. (Note: a ready issue that merely *shares files* with a still-in-flight lane is **not** a refill
candidate — its files stay owned until that lane's PR merges, a later run's input. Such issues should
already have been **packed** into the colliding lane as sequential work in Step 2, not parked here.) Under a dynamic Workflow this saturation *is* the loop-until-dry: the
script keeps N lane `agent()`s busy, refilling each freed slot from DEFERRED until the set is empty —
you don't drive it slot by slot, and because the orchestrator already claimed DEFERRED up front
(Step 4), a refill needs no fresh bd write. (Dependency-blocked issues won't appear here — they
unblock only when a blocker *merges*, a later run's input, not this one's.)

## Step 7 — Land, record centrally, hand off

When the queue is drained, the **orchestrator** (never the lanes) reconciles bd and reports:

- Leave each shipped issue **claimed / in-progress** while its PR is open — do **not** close it here.
  Closing happens at merge, and that's `post-merge-cleanup`'s job; closing on PR-open would orphan
  the issue from its still-unmerged branch.
- **In ultracode mode, reconcile strictly from the Workflow's returned per-lane report.** Because the
  script never wrote bd, the orchestrator only learns each lane's fate when the Workflow returns. For
  any issue claimed up front that the report shows as **never dispatched** (a DEFERRED slot the
  loop-until-dry never refilled before going dry) or **permanently crashed with no open PR**, un-claim
  it (`bd -C <primary> update <id> --status open`) before the push — so nothing is left claimed
  without a live branch/PR, the same invariant the first bullet states.
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
  Dolt DB from corrupting under parallelism — never relax it. A **dynamic Workflow does not change
  this**: the script itself has no shell/bd access, and its `agent()` lanes are lane agents (read-only
  on bd), so every bd write stays with the orchestrator — claimed centrally up front, the single
  `bd dolt push` after the Workflow returns.
- **Shared-ledger files need a single writer too — but the discipline splits by ledger *type*.** bd's
  Dolt DB is not the only mutable thing many lanes can race on. Some repo files are **shared ledgers**:
  a change appends to or regenerates a whole-repo artefact rather than editing a lane-local file. The
  two that bite here behave differently, and conflating them either corrupts the ledger or wastes
  throughput:
    - **Wholesale-regenerated ledger → strict single writer.** The **generated `reference/adr-index`**
      is rebuilt in full by `make docs-gen` whenever *any* lane touches an ADR source, so two lanes
      regenerating it produce conflicting whole-file rewrites. Either keep ADR-touching lanes out of
      the same run, or nominate exactly **one** ADR-touching lane (or the orchestrator, centrally) to
      run the single `make docs-gen` and commit the regen — never two concurrent regenerators.
    - **Row-addressable ledger → disjoint-row writers are safe.** The
      `docs/product-management/product_concepts/adrs/commitment-catalogue.md` is a table; a lane that
      flips its *own* row Planned→Live edits a different line from a lane flipping another row. Two
      such edits to **non-adjacent** rows do not git-conflict (verify the rows are well separated
      first, e.g. with `grep -n`). So a lane **may** own its single row *iff* the orchestrator has
      confirmed (a) no other concurrent lane touches that same or an adjacent row, and (b) the lane
      edits only that row's cells and never reformats the table. When rows are adjacent, two lanes
      need the same row, or you are unsure — fall back to the safe default: lanes *describe* the exact
      row + change and the **orchestrator applies the flips centrally**, serialised, one at a time.
  The point is to reserve the expensive single-writer/central-application discipline for *genuine*
  contention (whole-file regen, same/adjacent rows) and not pay it where edits are provably disjoint.
  Collision-freedom (the next guardrail) covers ordinary lane-local files; shared ledgers need this
  type-aware discipline on top.
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
