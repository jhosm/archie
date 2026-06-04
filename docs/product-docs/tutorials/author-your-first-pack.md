# Tutorial: Author your first pack

In this tutorial we create a brand-new regulatory pack and validate it locally
until it goes green. We start from the worked PT term-deposit pack
(`pt.2026.1`), make it our own (`pt.2026.2`), change one parameter, and watch
the validator confirm the change is sound — all on our own machine, fully
offline.

By the end we will have run:

```sh
make pack-validate PACK=pt.2026.2
```

and seen it finish with `OK` — depths 1–4 passing and the depth-5 corpus step
logged as a skip. That green run is our destination.

This is a learning path: one route, no detours. We will not explain *why* packs
exist or *why* config is split into packs and rate sheets here — those live in
the explanation pages we link to at the end.

---

## Before we start

Work from the repository root for every command:

```sh
cd babelstone
```

If this is a fresh checkout, install the pinned toolchain once. This brings in
the `cue` validator and the rest of the stack the commands below rely on:

```sh
make bootstrap
```

That is the only setup step. We are ready.

---

## Step 1 — Copy the worked example

We start from the one pack that already exists and passes validation. Copy its
whole directory to a new name:

```sh
cp -rf packs/pt.2026.1 packs/pt.2026.2
```

> On Windows PowerShell, the equivalent is
> `Copy-Item -Recurse -Force packs/pt.2026.1 packs/pt.2026.2`.

We now have a complete, well-formed pack at `packs/pt.2026.2/` — manifest,
primitives, parameters, rate-sheet refs, and the sealed test corpus. It is a
copy, so it does not yet describe a real `2026.2` pack. We fix that next.

---

## Step 2 — Make it our own pack

The directory name **must** equal `<pack_id>.<pack_version>` — we named the
folder `pt.2026.2`, so the manifest's version has to read `2026.2` to match.
Leave `pack_id: pt` exactly as it is — only the version moves; changing
`pack_id` would break the version-key check against the folder name.

Open `packs/pt.2026.2/pack.yaml` and make exactly three edits:

```yaml
pack_version: "2026.2"            # was "2026.1"
based_on_pack_version: "2026.1"   # was null — this pack is descended from 2026.1
delta_summary: |
  Lengthen the auto-renewal opt-out window from 14 to 21 days.
```

We changed the version, recorded which pack we built on, and wrote a one-line
summary of our change. (For what each manifest field means and which values are
legal, see the generated
[pack-format reference](../../product-management/reference/pack-format/README.md)
and its CUE source,
[`pack.cue`](../../../contracts/cue/pack/pack.cue) — we link to those rather
than restate them, so they can never go stale against this page.)

---

## Step 3 — Make one meaningful change

A copy with a new label is not interesting on its own. Let us change something
the engine actually enforces, so the validator has something real to react to.

Open `packs/pt.2026.2/parameters/constants.yaml` and change the opt-out window
from 14 to 21 days:

```yaml
auto_renewal_optout_window_days: 21   # was 14
```

That is the substantive content of our new pack: a 21-day pre-maturity opt-out
window instead of 14. It matches the `delta_summary` we wrote in Step 2.

---

## Step 4 — Validate locally

Now we ask the validator to check our pack:

```sh
make pack-validate PACK=pt.2026.2
```

This runs the offline validation depths 1–4 (`cue vet` over the manifest and
data files) and checks that the version key matches the directory name. We
expect a green run that ends in `OK`. The output looks like this:

```
== validate pt.2026.2 ==
  ok (#Manifest)  pack.yaml
  ok (#DayCounts)  primitives/day-count.yaml
  ...
  ok (#Parameters)  parameters/constants.yaml
  ...
  ok            version key pt.2026.2 matches directory
  skip          depth-5 corpus: expected-events.yaml empty (generation pending, C.3)
  ok            no-silent-gap sweep: all data .yaml covered
OK
```

Two lines deserve a note:

- **`version key pt.2026.2 matches directory`** — this is the check that would
  have failed if we had forgotten to update `pack_version` in Step 2.
- **`skip … depth-5 corpus`** — this is **not** a failure. Depth-5 is the
  engine simulation over the sealed test corpus, and the corpus
  (`expected-events.yaml`) is an intentional empty placeholder for now, so the
  validator logs it as a skip and moves on. A green depths-1–4 run with this
  skip is exactly the expected, passing outcome.

If the final line is `OK`, we are done.

---

## You did it

We created a new pack from scratch, gave it its own identity, made a real
parameter change, and validated it locally to green. That is the full authoring
loop for a pack.

What we deliberately did **not** do yet:

- **Sign and publish.** Signing (cosign) and pushing the OCI artefact need a
  registry and signing credentials that are not wired up locally yet, so we
  stopped at validation. That is the right place to stop on your own machine.
- **Run the depth-5 engine simulation.** The sealed-corpus simulation is
  engine-generated and still pending, which is why we saw it logged as a skip
  rather than a pass.

### Where to go next

- [How to validate a pack locally](../how-to/validate-a-pack-locally.md) — the
  task-focused version of Step 4, including how to read a failing diagnostic.
- [How to add a rate band](../how-to/add-a-rate-band.md) — the next real change
  most config authors make.
- [Why packs and rate sheets are separate](../explanation/why-packs-and-rate-sheets-are-separate.md)
  — the reasoning behind the split we touched on (a pack carries only a
  *ref* to rate numbers, which are deployed separately).
