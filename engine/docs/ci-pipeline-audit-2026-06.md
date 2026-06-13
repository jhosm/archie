# CI pipeline audit — 2026-06 (bd babelstone-2t16.12)

A read-only audit of every workflow under `.github/workflows/` plus
`engine/docs/mutation-testing.md`. No `.yml` is edited here; the deliverable is this
findings note and a drafted set of Epic-Q follow-up child issues (see the end). Every
claim below is grounded in the file it cites.

## Inventory of workflows audited

| Workflow | Trigger(s) | Role |
|---|---|---|
| `ci.yml` | PR + push-to-main | Path-scoped build/test, `CI gate` aggregator (ADR-PC-019 §P1) |
| `codeql.yml` | PR + push paths + Mon 04:00 | SAST, blocking on PRs, `CodeQL gate` aggregator (ADR-IC-014) |
| `sbom.yml` | PR/push paths + Mon 05:00 | CycloneDX SBOM + grype scan (report-only) over the .NET closure |
| `fuzz.yml` | Mon 06:00 + dispatch | Go `pack-validate` fuzzing (`FuzzLoad`, `FuzzRun`) |
| `mutation.yml` | Mon 03:00 + dispatch | Stryker.NET on EventStore, Engine, Money, FinancialMath |
| `dependency-review.yml` | PR | Cross-ecosystem blocking SCA gate (ADR-IC-014 Q.7c) |
| `spec-coverage.yml` | PR + nightly 03:17 | ADR↔catalogue↔code coverage (ADR-PC-020 §P6) |
| `adr-governance.yml` | PR | PR-body ADR section + ADR-immutability (ADR-PC-020 §D3/§D5) |
| `docs-site.yml` | PR/push paths + dispatch | DocFX site build/deploy (ADR-PC-026) |
| `claude.yml` | issue/PR comment events | `@claude` assistant action |

The blocking PR gates are `CI gate` (ci.yml), `CodeQL gate` (codeql.yml),
`dependency-review`, `spec-coverage`, and the two adr-governance jobs. fuzz/mutation/sbom
are scheduled or report-only and never sit on the PR critical path.

---

## 1. FUZZ coverage gaps

Today `fuzz.yml` fuzzes ONLY the Go `pack-validate` surface: matrix targets `FuzzLoad`
(`internal/pack` — `pack.Load` over each required source file) and `FuzzRun`
(`internal/validate` — the full depths 1→4 pipeline over a fuzzed pack variant). The
contract under test (`fuzz.yml` header, ADR-PC-007 §169) is "a malformed/garbage pack
yields a clean rejection, never a panic/crash/hang." That is the right contract; the gap
is that other untrusted-bytes ingress points have no equivalent.

Assessment of each untrusted-bytes ingress:

### 1a. Engine Avro decode path — WORTH A FUZZ TARGET (highest value)
`engine/src/Babelstone.Engine.Avro/AvroEventSerializer.cs` decodes bus/event-store bytes
through `Decode(ReadOnlyMemory<byte> payload, …)` → `ReadAvro` (single-schema fast path)
and the writer/reader-resolution overload `Decode(payload, payloadType, writerSchema)`.
Both feed attacker-influenced bytes (a poisoned inbox message, a corrupt event-store row,
a cross-context evolution payload) into Apache.Avro's `GenericDatumReader` and then into
`FromRecord`/`FromAvro`, which casts (`(long)avroValue`, `(DateTime)avroValue`) and invokes
the record constructor by reflection. A truncated/garbage payload, a wrong-tag union, or a
field-count mismatch must yield a clean typed rejection routed to the inbox poison sink
(`Babelstone.InboxConsumer/IInboxPoisonSink.cs`), never a hang or an unhandled cast/NRE.
This is the engine's direct analog of `FuzzLoad`/`FuzzRun` and the single most valuable
new target. .NET has no built-in libFuzzer; the pragmatic form is a randomized/property
"decode-never-crashes, always-clean-rejects" test over mutated valid payloads in
`Babelstone.Engine.Avro` (or `OutboxPublisher`/`InboxConsumer`) tests — a corpus-driven
xUnit `[Theory]` is acceptable as a first cut, with SharpFuzz as the stretch goal.

### 1b. Engine.Api JSON envelopes — WORTH A LIGHTER TARGET
`engine/src/Babelstone.Engine.Api/DepositsEndpoints.cs` binds request bodies
(`ConstituteDepositRequest`, `MatureDepositRequest`, `PayInterestRequest`) via ASP.NET
minimal-API `System.Text.Json` model binding, plus a `JsonEventStoreCodec` (see
`Babelstone.Engine.Api.Tests/JsonEventStoreCodecTests.cs`). The JSON *parser* itself is
System.Text.Json (already hardened upstream), so the residual risk is the binding +
validation layer: an out-of-range `principal_cents`, a malformed `start_date`, a NaN/huge
number, or a body that binds to a request whose handler then throws an uncaught type.
The endpoints already map domain failures to 422/409 (`DomainRejectedException`,
`ConcurrencyException`), and corrupt wiring is documented to propagate to a 500 — so the
fuzz contract is "no malformed envelope produces a 5xx that should have been a 4xx, and no
hang." Lower value than Avro (the parser is trusted; the surface is small), but a single
property test over mutated JSON envelopes per endpoint is cheap insurance.

### 1c. Python `mcp-server` request parsing — OUT OF SCOPE FOR NOW (rationale)
`mcp-server/src/babelstone_mcp/server.py` exposes typed FastMCP tools whose arguments are
Pydantic-validated (`ConstituteDepositResult`, `DepositPosition`, …) and whose outputs are
`DepositPosition(**await engine().deposit_position(...))` — i.e. the server is a thin typed
proxy onto the engine HTTP API, and the MCP SDK (`mcp==1.27.2`) owns JSON-RPC framing.
The untrusted bytes it parses are (a) the model's tool-call args, already schema-checked by
Pydantic/the SDK, and (b) the ENGINE's own JSON responses (trusted, server-to-server). The
genuinely untrusted ingress is fuzzed one layer down at the engine (1b). Defer with
rationale; revisit if/when mcp-server gains its own untrusted parser (it currently has no
fuzz/property tooling — `pyproject.toml` dev deps are only `pytest` + `pytest-asyncio`).
NOTE: this is the same surface CodeQL flags as a "Planned hole" until `mcp-server/` gains
`.py` SAST source — keep the two notes consistent.

### 1d. `acl` / `orchestrator` / `notification` boundary parsers
- **`acl/` and `notification/`** are STUBS — each contains only a `Dockerfile` + `README.md`,
  no source (confirmed: `ls acl notification`). Their CI jobs are `TODO echo` placeholders.
  EXPLICITLY OUT OF SCOPE until a real boundary parser lands; file the fuzz target alongside
  the build task that introduces it (ci.yml already flags these as TODO lanes).
- **`orchestrator/`** has real code (`Babelstone.Orchestrator`) and a state machine, but its
  ingress today is internal saga commands, not an untrusted external wire parser; ci.yml's
  own comment notes "a contract-test project … does not yet exist for the orchestrator; add a
  leg here when one lands." DEFER the fuzz target to that same milestone.

### 1e. `contracts/` CUE + Avro — OUT OF SCOPE (rationale)
`contracts/` is data: `avro/`, `catalog/`, `cue/` schemas plus fixtures — no first-party Go
module of its own. Parsing is done by the pinned third-party `cue` toolchain and Apache Avro
library, both fuzzed upstream; first-party CUE handlers do not exist (the pack-author skill
forbids hand-rolled CUE). The first-party logic that consumes these — `pack-validate` — IS
already fuzzed (`FuzzLoad`/`FuzzRun`). No new target warranted.

**Fuzz verdict:** the one clear gap is the engine Avro decode path (1a); the JSON envelope
layer (1b) is a cheaper secondary. Everything else is correctly deferred to the milestone
that introduces its parser, or is upstream-owned.

---

## 2. MUTATION scope

`mutation.yml` runs Stryker.NET on exactly four projects: `EventStore` + `Engine`
(spine, `stryker-config.json`, break 60) and `FinancialTypes`(Money) + `FinancialMath`
(kernel, `stryker-config.kernel.json`, break 90) — per the matrix and the score-floor table
in `engine/docs/mutation-testing.md` §"Score floors". The doc's framing — "mutation testing
measures test *effectiveness*… where a one-character slip is a correctness or data-integrity
incident" — is the lens for extending it. Assessment of the unmutated test-bearing projects:

| Project (tests exist) | Recommendation | Floor / rationale |
|---|---|---|
| `Babelstone.OutboxPublisher` | **ADD (high)** | The outbox drainer ordering + at-least-once guards are a data-integrity seam (mirrors the spine's `ES_ATOMIC_APPEND_OUTBOX`). Start break **60** like the spine; ratchet up. Has Avro round-trip tests already (per CLAUDE.md), so killable mutants exist. |
| `Babelstone.InboxConsumer` | **ADD (high)** | At-least-once dedupe + poison-sink routing are exactly-once-ish guarantees a surviving mutant would silently break. Break **60**, Docker-backed like the spine (Testcontainers tier). |
| `families/term-deposit` (`.TermDeposit` + `.Application`) | **ADD (high)** | The decider/fold is where money is computed (accrual, withholding, coupon math). A surviving mutant here is a wrong cent in a regulated figure — the same class the kernel floor protects. Pure-ish; aim break **70**, kernel-style. This is the most user-visible gap. |
| `Babelstone.RateSheets` | **ADD (medium)** | TAN resolution / effective-dating selects the price; a flipped comparison picks the wrong rate sheet. Break **60**. |
| `Babelstone.Packs` | **ADD (medium)** | Strict pack parsing (no `IgnoreUnmatchedProperties`) is the engine's trust boundary on pack YAML; a weakened guard lets a malformed pack load. Break **60**. |
| `Babelstone.Engine.Avro` | **ADD (medium)** | The family-agnostic codec's field-binding + Money/Guid/DateOnly conversions; pairs naturally with the 1a fuzz work. Break **60**. |
| `Babelstone.Engine.Api` projection runtime | **DEFER w/ rationale** | The projection relay / read-model UPSERT monotonicity guard is integration-shaped (real PG); most of its mutants are killed by the Testcontainers tier, which makes a Stryker run very slow and the marginal mutant-kill low at v1. Revisit once the projection logic has more pure branches. |
| Go `pack-validate` (no tool wired) | **DEFER w/ rationale** | Go has no first-class mutation tool in the pinned toolchain; `pack-validate` is already the BEST-tested first-party surface (fuzz + depth-budget fitness + JSON-contract test). Lowest marginal value; reconsider only if a Go mutation tool is adopted estate-wide. |
| Python `mcp-server` (no tool wired) | **DEFER w/ rationale** | Thin typed proxy (see 1c); little behavioural logic to mutate, and no Python mutation tool is pinned. Defer until the server grows real logic. |

**Mutation verdict:** the four-leg matrix under-covers the money path. Highest-value
additions are `families/term-deposit` (regulated cent math), `OutboxPublisher` +
`InboxConsumer` (data-integrity seams), then `RateSheets`/`Packs`/`Engine.Avro`. Each new
leg needs its own `--config-file` (reuse `stryker-config.kernel.json` for pure projects,
`stryker-config.json` for Docker-backed ones) and a floor that "starts at the achievable
score and ratchets up" (mutation-testing.md §"Score floors") — never lower a `break` to go
green.

---

## 3. DB-LEVEL engine↔family independence

`EngineFamilyAgnosticTests` (`engine/tests/Babelstone.Engine.Tests/EngineFamilyAgnosticTests.cs`,
ADR-PC-021 §P2/§D2) guards the family→engine arrow at the **`.csproj`-reference level only**:
it parses the eight enumerated spine projects' `.csproj` off disk and fails if any carries a
`<ProjectReference>` into `families/**`. A second `[Fact]` keeps the allowlist in lockstep
with the ADR text. This is sound for compile-time coupling — but it is blind to the database
schema, and there IS schema-level coupling today:

- The append-only **`events`** table (`Sql/0001_events_and_outbox.sql`) is correctly
  family-AGNOSTIC: the body is an opaque `payload BYTEA`, keyed by a generic `family VARCHAR`
  + `event_type VARCHAR` + structural columns. This is the model the fitness function should
  protect.
- But **`read_model.deposits`** (`Sql/0013_read_model.sql`) is a FAMILY-TYPED table that ships
  in the engine's OWN migration set (`Babelstone.EventStore.Migrations`): columns like
  `maturity_date`, `tan_basis_points`, `interest_variant`, `product_code`, `coupons_paid` are
  term-deposit shape, not generic. The `.csproj` guard cannot see this — a family's read-model
  DDL has leaked into the engine's migrations directory with no gate noticing.

**Recommendation: add a schema-level fitness function** complementing the `.csproj` one. The
rule: the engine's *write-side / source-of-truth* schema (the `public` event-store tables:
`events`, `outbox`, `snapshots`, `projections`, `pack_versions`, the projection-runtime and
inbox plumbing) MUST carry NO family-typed columns/tables/FKs — events stay opaque payloads
keyed by `family`/`event_type`. The check parses the migration `.sql` files in
`Babelstone.EventStore.Migrations/Sql/` and fails if a write-side table introduces a
family-domain column name (a denylist seeded from family vocabulary, or — cleaner — an
allowlist of the generic event-store column contract from ADR-PC-001 §P1). The denormalized
CQRS read model (`read_model.deposits`) is legitimately family-shaped per ADR-IC-005, so it
is either (a) explicitly excluded from the write-side rule, OR (b) the audit's real finding is
that family read-model migrations should not live in the *engine* migration set at all and
should move to a family-owned migration project — that is an ADR-PC-021 design question worth
raising, not silently deciding here.

This is NOT a case where the `.csproj` guard alone suffices: it provably misses the
`read_model.deposits` leak. A schema-level gate is warranted; whether the read-model exclusion
is "allowed" or "should be relocated" is the design call to escalate.

> **RESOLVED 2026-06-13 (bd babelstone-2t16.18).** The maintainer settled the escalation on
> option (b): `read_model.deposits` was **relocated** out of the engine migration set into a
> term-deposit family-owned set (`Babelstone.Families.TermDeposit.Application/Migrations/`,
> `0001_read_model.sql`), so the engine event-store migrations now carry **zero** family-named
> tables. ADR-PC-021 was amended (2026-06-13) to extend the family-agnostic boundary from CODE
> coupling (§P2) to migration-owned SCHEMA. The schema-level fitness function recommended above is
> now `Live` as `EventStoreSchemaFamilyAgnosticTests` (commitment-catalogue row 12a,
> `EVENT_STORE_SCHEMA_FAMILY_AGNOSTIC`): it scans the **entire** engine `MigrationSet.All` with no
> read-side carve-out and adds an inverse positive guard that RED-fails if a `read_model` schema or
> `deposits`-named object ever re-appears in the engine set.

---

## 4. FRESHNESS sweep

### 4a. Action-pin currency
Pins across all workflows (grouped):
- `actions/checkout@v6` (×25), `actions/cache@v5` (×5), `actions/upload-artifact@v7` (×2),
  `jdx/mise-action@v4` (×~12), `github/codeql-action/*@v4`, `dorny/paths-filter@v4`,
  `actions/dependency-review-action@v5`, `lycheeverse/lychee-action@v2`,
  `actions/upload-pages-artifact@v5`, `actions/deploy-pages@v5`, `actions/configure-pages@v6`,
  `anchore/sbom-action@v0`, `anchore/scan-action@v7`, `anthropics/claude-code-action@v1`.
  These are all current major-version tags for mid-2026 — no advisory'd/abandoned major.
- **FINDING (consistency drift):** `claude.yml` step "Checkout repository" pins
  `actions/checkout@v4` while EVERY other workflow uses `@v6`. Not a vulnerability (v4 is
  maintained), but it is a stale outlier that should be bumped to `@v6` for uniformity. The
  audit does not edit `.yml`; file as a one-line follow-up.
- ADVISORY: all pins are floating major tags (`@v6`), not SHA-pinned. Acceptable under the
  repo's current posture (and dependency-review/Dependabot cover the supply chain), but if the
  estate later moves to SHA-pinned actions for actions-supply-chain hardening, that is a
  separate deliberate decision — flagged, not recommended here.

### 4b. `ci.yml` paths-filter / `changes` matrix vs newer areas
The filter (`ci.yml` lines 41–55) keys on top-level subtrees. Checking the newer areas:
- **`Engine.Api` + projection runtime** — live under `engine/src/Babelstone.Engine.Api`, so
  they match `engine: ['engine/**']` and correctly trigger the `engine` job (which builds the
  whole `Babelstone.slnx` incl. `Engine.Api`, and runs `Engine.Api.Tests` integration leg).
  Confirmed: `Engine.Api` is in `Babelstone.slnx`. CORRECT.
- **`RateSheets` runtime** — the actual code is `engine/src/Babelstone.RateSheets*`
  (in `Babelstone.slnx`), so a rate-sheet *runtime* change triggers the `engine` job. CORRECT.
  BUT the dedicated `rate-sheets` CI job filters on the TOP-LEVEL `rate-sheets/**` dir, which
  today contains only `README.md` (a stub) and whose job body is a `TODO echo`. So the
  `rate-sheets` job fires only on the doc stub, never on the engine-side rate-sheet code.
  **FINDING:** this is fine TODAY (the job is a placeholder) but is a latent mismatch — when
  real rate-sheet schema tooling lands, confirm its source location is covered by the filter
  that triggers the job that validates it. Worth a tracking note so the placeholder doesn't
  ossify into a silent no-op.
- **`notification`** — has its own `notification: ['notification/**']` filter and job, but the
  dir is a STUB (Dockerfile + README only) and the job is a `TODO echo`. Filter is correctly
  wired ahead of the source; no action beyond the existing TODO.
- The engine job's `if:` correctly ALSO fires on `families`/`packs`/`contracts` changes (the
  documented `babelstone-fk7m.2` cross-coupling rationale), so a pack/contract change that
  could break the strict engine parse is caught. SOUND.

### 4c. Scheduled-scan cadence sanity
All four heavy scheduled scans run Monday, staggered one hour apart with documented ordering:

| Scan | Cron | Notes |
|---|---|---|
| mutation | `0 3 * * 1` (Mon 03:00) | first; slow Stryker legs |
| CodeQL | `0 4 * * 1` (Mon 04:00) | "after the 03:00 mutation run" |
| SBOM | `0 5 * * 1` (Mon 05:00) | "after the 04:00 CodeQL scan" |
| fuzz | `0 6 * * 1` (Mon 06:00) | "after the 05:00 SBOM and 04:00 CodeQL" |
| spec-coverage audit | `17 3 * * *` (nightly 03:17) | report-only sweep, off the per-push path |

The staggering is sane (no two heavy scans contend for the shared runner pool at the same
minute; each header documents its slot). One observation, not a defect: **all four heavy
scans land in a single Monday-morning window** — if Monday's runner capacity is ever
contended, a slow mutation run (03:00, can run hours) could still overlap CodeQL (04:00).
`concurrency:` groups protect against a *new* scheduled run piling onto an in-flight one of
the SAME workflow (fuzz, docs-site have it; mutation/codeql/sbom rely on the 1h stagger).
Cadence is otherwise healthy: weekly is appropriate for effectiveness/supply-chain scans that
are too slow for the PR gate, and the nightly spec-coverage audit is correctly separate.

---

## Prioritized recommendations

| # | Gap | Action / Defer | Priority |
|---|---|---|---|
| 1 | Engine Avro decode path has no fuzz/property "decode-never-crashes, always-clean-rejects" coverage | ACTION — add a corpus/property fuzz target in `Engine.Avro`/`InboxConsumer` tests | **High** |
| 2 | `families/term-deposit` (regulated cent math) is not mutation-tested | ACTION — add Stryker leg, kernel-style floor ~70 | **High** |
| 3 | `OutboxPublisher` + `InboxConsumer` (data-integrity seams) unmutated | ACTION — add Stryker legs, break 60, Docker-backed | **High** |
| 4 | Engine write-side SQL schema has no family-typing fitness function; `read_model.deposits` leak unnoticed | ACTION — add schema-level fitness fn; ESCALATE the read-model-location design call to ADR-PC-021 | **High** |
| 5 | `RateSheets` / `Packs` / `Engine.Avro` unmutated | ACTION — add Stryker legs, break 60 | Medium |
| 6 | Engine.Api JSON envelope binding has no fuzz coverage | ACTION — light per-endpoint property test over mutated JSON | Medium |
| 7 | `claude.yml` pins `actions/checkout@v4`; rest use `@v6` | ACTION — bump to `@v6` for consistency | Low |
| 8 | `rate-sheets` CI job filters the stub `rate-sheets/**` dir, not the engine-side rate-sheet code | TRACK — verify coverage when real rate-sheet tooling lands | Low |
| 9 | `mcp-server` (Python) / Go `pack-validate` have no mutation tool wired | DEFER — no pinned tool; low marginal value; `pack-validate` already best-tested | Defer (rationale) |
| 10 | `acl`/`notification`/`orchestrator` boundary parsers unfuzzed | DEFER — stubs/internal; file fuzz target with the build task that adds the parser | Defer (rationale) |
| 11 | All four heavy scheduled scans share the Monday-morning window | MONITOR — sane today via 1h stagger; revisit if runner contention appears | Info |

## ADRs honoured

This audit references and is grounded in: ADR-PC-021 (engine↔family independence, §P2/§D2),
ADR-PC-001 (event-store column contract), ADR-IC-005 (CQRS read model), ADR-PC-007
(pack-validate fuzz contract), ADR-IC-014 (SAST/SCA supply-chain posture), ADR-PC-019 §P1
(path-scoped CI + aggregator gates), ADR-PC-020 §P6/§D3 (spec-coverage / explicit-drift),
ADR-IC-002 (Avro evolution). No ADR is amended or superseded — this is a read-only audit and
recommends follow-up issues only.
