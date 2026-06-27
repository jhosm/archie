# Code Comment Guidelines

**In plain English:** this codebase comments heavily — roughly a third of every source
line — and on purpose: the comments are a *rationale and traceability* layer that binds
each line back to the decision that governs it, which is what keeps a governance-heavy,
largely-LLM-authored codebase reviewable and drift-aware. That density is an asset worth
keeping, but only if every comment stays *true*. This guideline is how we keep it true:
comment the **why**, cite only anchors that can't silently go stale, and never let a
comment make a claim that nothing would catch if it became false.

This is a standalone reference (the `implementation_guidelines/` series is not sequenced).
It governs hand-written comments in `engine/`, `families/`, `orchestrator/`, `acl/`,
`notification/` (C# `///` and `//`), `mcp-server/` (Python docstrings and `#`), and
`pack-validate/` (Go `//`). It does **not** govern the generated reference under
`docs/product-docs/reference/` (that is machine-rendered — see
[ADR-PC-022](../product_concepts/adrs/ADR-PC-022-product-documentation-architecture.md))
or the concept/ADR prose under `docs/product-management/`.

There is **no mechanical gate** for this guideline. Comment quality is a judgement a
linter cannot make — it can police length and the presence of a citation, but not whether
a paragraph is *plausible-but-stale*, which is the failure that actually hurts. Enforcement
lives with reviewers and the `code-comment` / `adr-conformance` review agents at PR time.

## The litmus

One question decides whether a comment is safe to write:

> **If this comment became false, would anything catch it?**

If the answer is *no* and the comment makes a checkable claim, it is a **rot liability**.
Either make the claim vague enough that it can't drift, or move the claim next to the test
that already asserts it and let the comment point at the behaviour instead of re-specifying
it. Every rule below is this litmus applied to a specific case.

## Three tiers, by audience

A comment is not one thing. There are three audiences, and they want different things.

| Tier | Form | Audience | Rule of thumb |
|---|---|---|---|
| **1 — Contract** | `///` XML-doc on public types/members | Reader of the *generated* C# API reference ([ADR-PC-026](../product_concepts/adrs/ADR-PC-026-csharp-api-reference-docfx.md)) | The published surface. This is where length is earned. Keep it current — it ships. |
| **2 — Rationale** | inline `//` (and `#` in Python/Go) | The next maintainer reading *this line* | The local **why**. Short. Cite, don't restate. |
| **3 — Orientation** | file / module docstring | Someone opening the file cold | One paragraph: what this file owns, and the ADR(s) it answers to. |

Tier 1 is a *product* — DocFX ingests every `///` summary into a public API reference — so a
stale `<summary>` ships a stale fact to readers. Tier 2 and 3 are for the person editing the
code; they earn their keep only by saying something the code does not already say.

## The four rules

### 1. Cite the commitment, state the local fact, stop

A comment names **which** decision a line upholds and **the one thing true here that the
code does not already say** — then stops. The full rationale lives in the ADR; that is what
the ADR is *for*. Restating an ADR inline creates two sources for one truth, which is the
exact drift the project fights everywhere else: when the decision evolves, the paraphrase is
what goes stale.

### 2. A checkable claim needs a test, not prose

"Default ON", "returns `422 SCA_REQUIRED`", "the production host injects the Avro codec" — if
a behaviour is asserted in a comment, it should be asserted in a *test* somewhere, and the
comment can then point at the behaviour rather than re-specify it. A claim with no test
behind it is a claim nothing will catch when it drifts (the litmus).

### 3. No speculation in comments

Forward-looking "should a future product…" / "when we later add…" notes describe code that
does not exist. They can never be verified and are the first thing to rot. They belong in the
backlog as a tracked issue, not inline. (See **Citation discipline** for why the issue id
itself does not go in the comment.)

### 4. Length must be earned by irreducibility

A long comment is fine when every line is non-obvious *why* — a fail-soft rationale, an
invariant the type can't express, the reason a `null` means what it means. It is not fine
when half of it restates the type signature or paraphrases the ADR. Density is the asset;
*redundant* density is the cost. Cut the lines that the code, the type, or the cited ADR
already carry.

## Citation discipline

A comment may cite only anchors that are **durable** (they don't move) and ideally
**verifiable** (something fails if the comment's claim is wrong). Three concrete rules:

- **ADR references stop at the ADR id.** Write `ADR-PC-028`, never `ADR-PC-028 §P3`. Section
  numbers are internal structure that shifts when an ADR is amended or superseded; pinning to
  `§P3` couples the comment to the ADR's *layout*. The bare id stays valid across every
  amendment. (One exception: the ADR's own filename slug, which is the durable id.)

- **Keep the fitness-function / commitment names.** SHOUTING_CASE anchors like
  `ENGINE_FAMILY_AGNOSTIC` or `STORE_BUS_ENCODING_EQUIVALENCE`, drawn from the
  [commitment catalogue](../product_concepts/adrs/commitment-catalogue.md), are the
  *strongest* anchor in this codebase: each is backed by a fitness function, so if the
  comment's claim becomes false, CI fails. Cite the commitment name; do **not** restate what
  it guarantees. Pair it with the ADR id — the ADR is the rationale half, the commitment name
  is the testable half: `(ADR-PC-028 / STORE_BUS_ENCODING_EQUIVALENCE)`.

- **No `bd` issue ids in comments.** A `bd` issue is ephemeral internal tracking — it closes,
  it can be renumbered, and it renders as noise in the published API reference. The
  traceability is not lost: `git blame` → the commit → its PR → the `bd` issue is the durable
  chain, and the commit body is where the issue id belongs. The comment carries the *fact*;
  version control carries the *provenance*.

## When not to comment

- The type or name already says it (`/// <summary>The deposit id.</summary>` on a
  `DepositId` property earns nothing).
- The comment would only restate the cited ADR — link the ADR instead.
- The "comment" is really a TODO or a future-work note — file it in the backlog.

## Worked example

A `const string` whose original comment ran ~12 lines, carrying a `bd` id, an ADR section
ref, restated config mechanics, and a speculative future-product paragraph:

```csharp
// BEFORE — restates config, pins a section, carries a bd id and a hypothetical
/// <summary>The v1 default pricing role. For EVERY v1 launch product the engine's
/// product-config store resolves ProductConfig.DefaultRole == "standard" ... A renewal of a
/// deposit constituted BEFORE the per-deposit role was persisted (bd babelstone-mtto.5) ...
/// NOTE: should a future product carry a non-standard default role, this hardcoded fallback
/// would diverge ... [continues for several more lines]</summary>
```

```csharp
// AFTER — the one local fact + a durable citation; nothing that can silently drift
/// <summary>Fallback pricing role for a renewal whose closing deposit predates a persisted
/// <c>role</c> — such a deposit folds to <c>Role == ""</c>, and defaulting here keeps the
/// <c>(product, role)</c> rate re-resolution working. Matches the v1 product-config default
/// (ADR-PC-008).</summary>
```

What moved where: the `bd` id → the commit that added the line; the section ref → dropped to
the bare ADR id; the future-product hypothetical → a backlog issue; the restated config
mechanics → deleted (the cited ADR owns them). What stayed: the *why this fallback exists*,
which the code cannot say for itself.

## Relationship to the governance model

This guideline is the code-comment counterpart of the repo's explicit-drift posture
([ADR-PC-020](../product_concepts/adrs/ADR-PC-020-llm-toolchain-and-conformance-governance.md)):
divergence is allowed, *silent* divergence is not. A comment that cites a durable, verifiable
anchor cannot diverge silently — the gate that protects the anchor protects the comment with
it. A comment that paraphrases, pins a section, or asserts an untested behaviour can, which
is why those are the practices this guideline removes.
