# Product Software DLC: A Methodology for Auditable, Specification-Led Product Engineering

*A white paper on the distinctive development lifecycle used in the babelstone project*

---

## Abstract

Most software projects drift. Decisions made at architecture time erode silently as the codebase grows. Tests prove coverage, not correctness. Documentation becomes aspirational rather than authoritative. Regulatory evidence is assembled after the fact.

This white paper describes a methodology—developed in the babelstone project—that treats this drift as the central enemy. Every layer of the lifecycle is designed to make commitments falsifiable, bind them to tests, and force divergence into the open rather than letting it accumulate silently. The result is a development lifecycle that serves not just engineers and product managers, but auditors, regulators, and future maintainers as primary audiences.

The methodology has a name for this ambition: **Product Software DLC** — Development Lifecycle as a product artefact in its own right.

---

## 1. The Core Insight: Silent Divergence is the Root Problem

In conventional software development, four types of divergence accumulate silently:

1. **Decision drift** — code moves ahead of the architecture decision that motivated it.
2. **Specification drift** — tests verify what the code does rather than what it was specified to do.
3. **Contract drift** — a schema or event changes on one side of a boundary without the other side knowing.
4. **Regulatory drift** — the system's behaviour diverges from its regulatory commitments between audits.

Each of these is individually well-understood. What is distinctive about this methodology is the recognition that they share a root cause: **commitments that are not falsifiable**. A decision recorded in prose cannot be automatically checked against code. A test that covers lines does not prove invariants. A contract not enforced by a consumer test can silently break. A regulatory claim not backed by a reproducible test corpus cannot be verified on demand.

The methodology responds to this with a single principle repeated at every scale: **every commitment must be falsifiable, and every falsifiable commitment must be bound to a test or gate that proves it**.

---

## 2. The Document Layer: ADRs as Load-Bearing Artefacts

### 2.1 Two Namespaces, Two Shapes

The project maintains two peer ADR namespaces:

- **ADR-IC** (Integration Concepts): shared integration estate — broker, schema format, saga pattern, observability, testing infrastructure, regulatory toolchain.
- **ADR-PC** (Product Concepts): the product engine's own concerns — event store, family configuration surface, pack format, boundary signal contracts, coexistence strategy.

The namespaces are governed independently with separate cadences. This prevents the mistake of conflating infrastructure decisions (which affect all teams) with domain decisions (which the product engine team owns alone).

More importantly, the two namespaces use **two different ADR shapes**:

**Tool-selection ADRs** (choosing concrete technology) reuse a consistent evaluation framework defined in ADR-IC-000:
- Hard filters: cost (zero-budget constraint for a 1–2 person team), regulatory fit (GDPR/DORA/PSD2 compliance).
- Soft criteria: operational complexity, ecosystem coherence, exit cost, community longevity.

Verdicts are `Pass`, `Pass (conditional)`, or `Fail`. A conditional pass requires a named mitigation in the same cell. This makes the evaluation auditable and reproducible: a different team applying the same framework to the same candidates should reach the same decision, or be able to identify precisely where they disagree.

**Contract-shape ADRs** (defining boundary surfaces) use a six-slot template instead of evaluation tables:

| Slot | Content |
|------|---------|
| Payload shape | Avro schema reference; field semantics |
| Semantics | What assertion does this event or signal make? |
| Ordering & delivery | At-least-once? Ordered per partition? |
| Idempotency | What is the stable key across redelivery and replay? |
| Error model | Who retries? Does a rejection gate the producer or merely flag? |
| Ownership & versioning | Which team owns which half? How do breaking changes proceed? |

The rigor here is different because contracts are evaluated by building against them, not by comparison-shopping. The six required slots ensure that two teams can implement independently and meet at the boundary without coordination overhead.

### 2.2 Verifiable Commitments as a Required Section

Every Accepted ADR carries a **Verifiable Commitments** table mapping each falsifiable claim to the test or gate that proves it, with a stable Test ID and a `live / planned / gap` status column.

This does several things at once:

- It makes the gap between "the decision says X" and "the code proves X" **visible rather than invisible**.
- It creates DORA/PSD2 demonstrable-conformance evidence: "here is the commitment; here is the test ID; here is the CI run that verified it."
- It forces the author of a decision to enumerate what would falsify it, which is itself a discipline that improves decision quality.

A commitment with a `gap` status is not a failure—it is an honest statement of technical debt with a named owner. A commitment with no Test ID at all is not permitted.

### 2.3 The Explicit-Drift Rule

Once an ADR reaches `Accepted` status, its `## Decision` section is **immutable**. The only permitted workflows for a decision that turns out to be wrong or incomplete are:

- **Amend**: append a dated `*Revised YYYY-MM-DD: …*` line with an additive clarification or refinement that does not reverse the decision.
- **Supersede**: author a new ADR with a back-link, flip the old ADR's status to `Superseded`, and record what was learned.

Genuine divergence between code and decision must become a recorded decision change. Code that contradicts an Accepted ADR without an amendment or supersession in the same change is called **silent drift**, and the methodology treats it as a defect more serious than a failing test.

The explicit-drift rule turns the ADR corpus into an **auditable ledger of decisions and learning**: what we thought, when, and what changed our minds.

---

## 3. The Conformance Layer: Three Guards, Three Drift Classes

Drift exists in three classes, and no single gate can catch all three. The methodology uses three guards working in composition:

### 3.1 Mechanical Always-Rules (CI-Authoritative)

These are shell hooks and CI jobs that enforce rules too simple to need judgement:

- **`adr-immutability.sh`** (pre-commit warning) + **`adr-immutability-check.sh`** (CI hard-fail): detects edits to an Accepted `## Decision` section in place.
- **PR body gate**: every PR must name the ADRs it implements, amends, or honours. Review starts from the decision, not the diff.
- **`spec-coverage-check.sh`**: ensures every Verifiable Commitment has a Test ID that the coverage auditor can resolve.

These gates are authoritative. The model cannot forget them because the harness enforces them.

### 3.2 Runtime Fitness Functions (Integration-Test Pyramid)

The project uses an adapted testing pyramid with disproportionate weight on the contract and integration levels:

```
              ┌──────────┐
              │   E2E    │  ← rare, selective, careful
              └──────────┘
           ┌────────────────┐
           │  Saga Tests    │  ← failure paths, compensation correctness, chaos injection
           └────────────────┘
        ┌──────────────────────┐
        │  Contract Tests      │  ← Pact CDC; no breaking schema change crosses a boundary silently
        └──────────────────────┘
     ┌──────────────────────────┐
     │  Integration Tests       │  ← event-store atomicity, replay determinism, projection correctness
     └──────────────────────────┘
  ┌────────────────────────────────┐
  │  Unit Tests (pure aggregates)  │  ← rich foundation; no I/O, no clock
  └────────────────────────────────┘
```

**Property-based testing** is used selectively rather than universally — reserved for invariants where the space of inputs is too large to enumerate (money conservation across saga compensation, accrual correctness across day-count conventions). The lean 1–2-person team posture means the investment must be justified by the failure mode it prevents.

**Mutation testing** (Stryker.NET) is applied to the financial-math kernel and the event-store spine, targeting 100% mutation score. These modules handle rounding, accrual, and atomicity—correctness, not coverage, is the bar. Mutation testing guards test *effectiveness* rather than test *existence*, catching the common failure where a test passes even with the implementation subtly broken.

### 3.3 Design-Time Conformance Agent (Judgement Layer)

The mechanical gates and runtime tests cannot catch all drift classes. A second non-atomic write at aggregate constitution violates an ADR but would not fail any contract test. A consumer rejection that gates a business flow is architecturally wrong but syntactically valid. A handler that reads the clock is non-deterministic but compiles.

For this class of **internal-design drift**, the project uses a domain-specialised subagent: the `adr-conformance` agent. Its role is to review diffs against the ADR corpus and, on a genuine contradiction, propose an amendment or supersession **in the same change** rather than let the divergence land silently.

The agent is explicitly not a gatekeeper—it is a judgement layer over the long tail of contradictions that mechanical gates cannot see. It works alongside the mechanical gates, not instead of them.

Additional specialised agents cover narrower domains:
- **financial-math-reviewer**: rate scaling, withholding flows, day-count correctness.
- **contract-reviewer**: Avro schema evolution, PII on the bus, event naming conventions.
- **replay-determinism-auditor**: handler purity, projection folds, fixture replay identity.
- **doc-consistency**: cross-link integrity, C4 diagram freshness, claims against cited sources.

---

## 4. The Configuration Layer: One Engine, Many Families

A distinctive architectural commitment underlies the entire domain model: **zero engine code per product variant**. All product variation is captured in:

1. **Variant YAML**: the instance-level configuration (principal, rate, maturity date, interest capitalisation, etc.).
2. **Family schemas** (CUE): the type-level configuration surface — what fields a variant may carry, what constraints the engine enforces, what primitives the pack resolves.
3. **Packs**: regulatory-era data that resolves pack-bound primitives (e.g., `day_count: pt.act_360`, `max_consumer_rate_bps: 450`).

This means the engine handles all families through a single, family-agnostic lifecycle state machine. A new product type enters the system by authoring a new CUE schema and YAML configuration, never by touching engine code.

### 4.1 The Pack as Regulatory-Evidence Artefact

The pack is the most distinctive output of this layer. It is:

- **Declarative, not executable**: plain YAML + CUE constraints in an OCI artefact, signed with cosign.
- **Auditor-readable**: a Portuguese banking regulator can read a pack without tooling.
- **Versioned and pinned per instance for life**: the pack that governed a deposit at constitution is the pack that governs it forever. Regulatory environments change; past commitments do not retroactively change with them.
- **Validated at four deterministic depths** (< 30 s aggregate, enforced by the `pack-validate` Go binary):
  - *Syntactic*: YAML parses and matches schema shape.
  - *Type-check*: types, ranges, pack-bound primitive resolution.
  - *Pack compliance*: variant respects pack bounds (e.g., rate does not exceed the era's regulatory cap).
  - *Regulatory coherence*: cross-field invariants (ascending stepped rates, PT Act/360 rule for deposits).
  - *Simulation*: engine runs variant against sealed test corpus; expected event sequences are regressor-facing compliance evidence.

The same binary serves three contexts: the author's pre-commit hook, the PR CI gate, and the engine at pack-load time. Diagnostics and validation budgets never drift between environments.

The sealed test corpus shipped inside the pack is particularly significant: it allows a regulator to run the engine themselves against canonical instances and verify that the engine's behaviour matches the documented expectations. The pack is not just configuration — it is **regulatory evidence**.

---

## 5. The Event-Sourcing Discipline

The event store is PostgreSQL, not an exotic event database. The decision is deliberate: co-location with the outbox is more valuable than specialised event-store features for a 1–2-person team operating under DORA constraints.

### 5.1 Atomic Append + Outbox

The `events` table append and the `outbox` table write commit together in a single local transaction. There is no distributed transaction, no two-phase commit, no saga for the outbox. This is load-bearing: it makes idempotency tractable and ensures the read model never diverges from the event log.

### 5.2 Append-Only by Role Privilege

The runtime PostgreSQL role cannot UPDATE or DELETE from the `events` table. The migration role can. This is not enforced by the database engine — it is enforced by database role grants and code-review discipline. The gap is honest: the methodology acknowledges where mechanical enforcement ends and human discipline begins.

### 5.3 Handler Purity as a First-Class Constraint

Event handlers must be pure functions of their inputs. No clock reads, no I/O, no randomness. Time enters the system as a field on the event, not as a `DateTime.UtcNow()` call inside a handler. This is enforced by the replay-determinism-auditor agent and caught by the determinism gate in CI.

The practical consequence: replaying the event log from any snapshot must reproduce identical state. Cold replay with budget constraints (≤ 5 s for v1 at ~24–260 events) is a Verifiable Commitment, not an aspiration.

### 5.4 Bitemporal Projections

Every projection table carries `valid_from`, `valid_to`, `recorded_at`, `superseded_at`. The engine maintains these explicitly, not through a framework. This enables two queries that matter for banking compliance:

- *"What did we know at time T?"* — audit query, retroactive correction.
- *"What is true now?"* — operational query.

### 5.5 Field-Granular PII Crypto-Shredding

Structural fields (principal, rate, dates) remain cleartext and queryable. PII fields (holder name, address, tax ID) are ciphertext under per-subject OpenBao keys. Key destruction equals GDPR Article 17 erasure. After shredding, structural queries continue to work; PII fields return null.

No PII appears on the durable event bus — ever. Outbound boundary signals carry references, not cleartext or ciphertext. The communications system that sends a notification calls back to the engine's PII-resolve surface, which decrypts internally. This means the bus is never a PII liability regardless of who has access to it.

---

## 6. The Boundary Contract Discipline

### 6.1 Post-Flag, Never Gated

Every outbound signal from the engine follows a single architectural rule: **a rejection downstream never gates or unwinds the engine**. The GL system can reject a posting signal; the engine does not roll back. The notification service can fail to deliver; the engine does not retry on behalf of the notification service.

This means the engine's event log is authoritative for what happened. Downstream systems are consumers of facts, not co-owners of the engine's state. Compensation, if required, is an explicit domain event — not a rollback.

The sole exception is the pre-contractual financial information (FIN) flow required by EU/BdP rules before a deposit contract is concluded. Here, proof of disclosure is a legal precondition for the contract, so the flow is a saga step with an explicit `FINAcknowledged` event, not a fire-and-forget emit.

### 6.2 The Six-Slot Contract Surface

Each boundary contract specifies:

1. **Payload shape**: Avro schema with field semantics.
2. **Semantics**: what assertion does this signal make? ("The engine has accrued interest due to the GL" is different from "the engine requests the GL to book an entry.")
3. **Ordering & delivery**: at-least-once per partition; ordering guarantees within a subject.
4. **Idempotency key**: stable across outbox redelivery and event-log replay.
5. **Error model**: who retries? Is a rejection a flag or a gate?
6. **Ownership & versioning**: which team owns which half? How does a breaking change proceed?

Contracts are enforced by Pact CDC in the test suite — the consumer generates a pact; the provider verifies against it. A schema-registry compatibility gate prevents breaking changes from landing without a version bump.

---

## 7. The LLM-First Authoring Model

### 7.1 Specification Before Code

The engine is authored primarily by a large language model working from explicit specifications. This is not accidental — it is a deliberate design choice that requires the specification to be complete enough that a capable generative model can implement faithfully. Gaps in the specification surface as ambiguities in the output; this is valuable signal.

The three-layer conformance regime exists in part because LLM-authored code introduces a new class of drift: the model may implement a plausible but incorrect interpretation of an ADR. The design-time conformance agent catches this class of error before it lands.

### 7.2 Skills as Encoded Procedures

**Skills** encode repeatable authoring tasks as procedural prompts that the model invokes when the task context matches. They are not workflow automation — they are **the right process, made easy**:

- `new-adr`: scaffolds an ADR with the correct shape (tool-selection or contract-shape), performs the dual number-check (disk + issue tracker), and seeds the Verifiable Commitments section.
- `amend-adr` / `supersede-adr`: the one-command drift acknowledgment from §P9 of the explicit-drift workflow.
- `pack-author`: scaffolds a new regulatory pack with the correct OCI structure and `.cue` constraints.

The principle is: **the conformant path is the easy path**. If authoring a correct ADR requires reading a style guide and remembering four constraints, most authors will not do it correctly. If invoking `new-adr` produces a correct scaffold automatically, the barrier disappears.

### 7.3 Hooks as Deterministic Always-Rules

**Hooks** enforce mechanical rules that the model cannot be trusted to remember consistently:
- Pre-commit: re-render PlantUML diagrams on staged `.puml` changes; warn on Accepted ADR edits.
- CI: hard-fail on ADR immutability violations; enforce PR body ADR section; check Verifiable Commitment Test IDs.

The harness enforces hooks; the model advises but cannot override them. This separates *judgement* (the model's domain) from *enforcement* (the harness's domain).

---

## 8. The Monorepo Commitment

The project uses a single monorepo for a specific reason: **atomic change**. A schema change, every consumer of that schema, and the commitment test that verifies the contract must land in a single commit. The monorepo makes this the default; a polyrepo makes it an exception requiring cross-repository coordination.

This means:
- Drift becomes a PR discussion, not a surprise at deploy time.
- The pack's contract surface, the engine code that reads it, and the CLI tool that validates it all share a git history — `git blame` reaches all three.
- A breaking contract change without a consumer update is a failing CI gate, not a production incident.

The cost is repository size and build time. At the 1–2-person team scale, this cost is paid once at setup and is dominated by the cost of the alternative: coordinating schema changes across repositories manually.

---

## 9. The Regulatory-Evidence Paradigm

The deepest layer of the methodology is a shift in audience. Most software is built for its operators. This methodology builds for **four audiences simultaneously**:

1. **Engineers**: the codebase, the test suite, the ADR decisions.
2. **Product managers**: the concept documents, the pack YAML, the feature-design companions.
3. **Future maintainers**: the ADR ledger of decisions and learning; the explicit-drift rule ensures history is never overwritten.
4. **Regulators and auditors**: the pack as auditor-readable YAML, the sealed test corpus as reproducible compliance evidence, the Verifiable Commitments as demonstrable-conformance claims.

This fourth audience changes the design of every artefact. A pack that is binary or DSL-encoded is not auditor-readable. A test suite that achieves coverage but not mutation score does not demonstrate correctness. A decision log that silently drifts from code does not serve as evidence.

The methodology asks: **would a regulator running this engine against the sealed test corpus in the pack reach the same event sequences we documented?** If the answer is yes, the system is conformant. If the answer is no, there is drift — and the drift is visible, because the test corpus is pinned in the signed OCI artefact.

---

## 10. Synthesis: The Through-Line

Ten sections describe many practices. The through-line is one:

> **Make commitments falsifiable. Bind every falsifiable commitment to a test or gate. Force divergence into the open rather than letting it accumulate silently.**

This principle repeats at every scale:

| Scale | Commitment | Gate |
|-------|-----------|------|
| ADR | A decision about technology or contract shape | Verifiable Commitments section with Test IDs |
| Event handler | Handler is a pure function of its inputs | Determinism gate in CI; replay-determinism-auditor |
| Contract | Schema does not break consumers | Pact CDC + schema-registry compatibility gate |
| Pack | Variant respects regulatory bounds | `pack-validate` depth budgets (< 30 s, all four depths) |
| Financial math | Rounding and accrual are correct | 100% mutation score on the financial-math kernel |
| Architecture | Code does not contradict an Accepted ADR | `adr-conformance` agent + immutability hook + PR body gate |

A methodology that holds this principle consistently produces a different kind of software: software whose behaviour can be explained, justified, and proved at any point in time — not just at the moment of initial deployment.

That is the ambition of Product Software DLC.

---

## Appendix: Glossary

| Term | Definition |
|------|-----------|
| **ADR** | Architectural Decision Record — a structured document recording a decision, its alternatives, its evaluation, and its Verifiable Commitments. |
| **ADR-IC** | ADR namespace for Integration Concepts (shared estate). |
| **ADR-PC** | ADR namespace for Product Concepts (engine's own concerns). |
| **Explicit-drift rule** | Divergence between code and an Accepted ADR must be recorded as an amendment or supersession; silent drift is forbidden. |
| **Pack** | A signed OCI artefact containing YAML configuration and CUE constraints for a regulatory era; pinned per instance at constitution. |
| **Post-flag, never gated** | An outbound boundary signal is a fact, not a request; a downstream rejection cannot block or unwind the engine. |
| **Sealed test corpus** | Canonical variant instances + expected event sequences shipped inside a pack as reproducible compliance evidence. |
| **Verifiable Commitment** | A falsifiable claim in an ADR table, mapped to a Test ID and a `live / planned / gap` status. |
| **Family schema** | A CUE schema defining the configuration surface for a product family (e.g., term deposit). |
| **Handler purity** | The constraint that event handlers are pure functions: no clock, no I/O, no randomness. |
| **Bitemporal projection** | A projection table carrying both valid-time and recorded-time columns, enabling "what did we know then" queries. |
| **PII crypto-shredding** | Erasure of personal data by key destruction rather than record deletion; structural fields remain queryable. |
