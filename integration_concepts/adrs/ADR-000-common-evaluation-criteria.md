# ADR-000: Common Evaluation Criteria for All Tool Selections

| Field | Value |
|---|---|
| Status | Accepted |
| Date | 2026-05-17 |
| Applies to | ADR-001 through ADR-010 |

---

## Context

The integration concepts series (documents 00–10) describes an event-driven banking integration architecture for a Portuguese term deposit system. Moving those concepts into working software requires selecting a concrete tool for every major infrastructure concern: message broker, schema registry, saga orchestrator, outbox mechanism, read model storage, API gateway, observability stack, event catalog, testing infrastructure, and anti-corruption layer.

Rather than evaluating each tool in isolation, a shared set of criteria ensures consistency and makes the reasoning behind each decision auditable. This ADR defines that shared framework.

### Constraints that shape the criteria

**Budget:** Zero. This is a proof-of-concept / learning exercise, not a production system with infrastructure spend. All tool selections must be achievable with self-hosted open-source software or genuinely usable free-tier cloud services. "Genuinely usable" means: no credit-card-required production cliff, and the free tier covers the traffic and feature set needed for a realistic POC.

**Team:** 1–2 people. No dedicated ops engineer. Tools that require specialist operational knowledge to stand up and keep running are a significant liability at this scale.

**Purpose:** A realistic architecture exercise — patterns and decisions should reflect what a real small bank or fintech would choose, not a toy demo. The POC should be honest about trade-offs even where it cannot resolve them with money.

---

## Decision

Every ADR in this series applies the following two-stage evaluation framework.

---

### Stage 1 — Hard Filters (pass/fail)

These are evaluated before any functional comparison. A tool that fails a hard filter is disqualified. A waiver requires explicit justification in the ADR.

#### F1 · Cost

**Pass:** self-hosted open-source under an OSI-approved permissive or copyleft licence (e.g., Apache 2.0, MIT, GPL, LGPL, AGPL, MPL 2.0) that does not restrict use in financial services; or a cloud-managed service with a free tier that covers POC-scale usage without a payment method.

**Fail:** paid-only tiers; open-core tools where the features required by this architecture are paywalled; tools with a licence that restricts use in a financial services context (Commons Clause, BSL, SSPLv1 — flag even if currently free, because the licence constrains future use).

#### F2 · Regulatory fit

The Portuguese banking context imposes three regulatory frameworks that affect architectural choices. Each candidate must be assessed against all three:

| Regulation | Architectural obligation |
|---|---|
| **GDPR** | Data residency in the EU; a credible mechanism for right-to-erasure (events are immutable — the tool must either support compaction/tombstoning or the ADR must explain the alternative). |
| **DORA** | Operational resilience testing must be possible (chaos injection, failover drills); RTO/RPO must be documentable from the tool's own guarantees. |
| **PSD2** | All state changes on financial operations must produce an auditable trail; SCA outcomes are first-class saga results, not technical errors. |

At POC scale, full regulatory compliance is not the goal — but the tool must not structurally prevent compliance in a future production system.

---

### Stage 2 — Soft Criteria (prose verdict)

Tools that pass both hard filters are compared on four soft criteria. Each ADR writes a short paragraph per candidate, not a numerical score. The goal is honest prose, not false precision.

#### S1 · Operational complexity for 1–2 people

Can one person set this up in a weekend and keep it running without becoming a specialist? Preference for: managed free tiers that eliminate operational surface; tools with strong defaults and good documentation; tools that fail loudly rather than silently. Penalise: complex cluster topologies required even for a single-node POC; tooling that requires a separate management plane; steep learning curves with sparse documentation.

#### S2 · Ecosystem coherence

Does this tool compose naturally with the rest of the stack without bespoke glue? Positive signals: native OpenTelemetry instrumentation; standard connectors (Kafka Connect, JDBC, HTTP); widely-used wire protocols; integration with common CI toolchains. Negative signals: proprietary SDKs as the only integration path; connectors that require a commercial tier; instrumentation that only works with the vendor's own observability product.

#### S3 · Exit cost

How painful is migration away from this tool in 3–5 years? Key questions: does the tool own a proprietary data format or wire protocol that other tools cannot read? Is the data exportable in a standard format? How much application code would need to change on replacement? A tool with high exit cost requires a stronger justification.

#### S4 · Community and longevity

Is this tool likely to be actively maintained in 10 years? Positive signals: large contributor base beyond the founding company; foundation governance (Apache, CNCF); commercial ecosystem creating aligned incentives. Red flags: recent licence change to a restrictive model (signals monetisation pressure); single-vendor control with a history of breaking API changes; stagnant commit activity (operational threshold: fewer than ~25 commits in the trailing 12 months from contributors outside the founding company, or no minor release in the trailing 9 months) or declining community.

---

### Verdict format

Hard filter verdicts use three values:

- **Pass** — the candidate satisfies the filter without qualification.
- **Pass (conditional)** — the candidate satisfies the filter only if a specific mitigation is documented and verified at implementation time. The mitigation appears in the same table cell, in the form `**Pass (conditional)** — [mitigation]`, and is restated in the ADR's Consequences or Residual Risks. A conditional pass is a hard filter result, not a soft criterion: it proceeds to Stage 2 only on the condition that the mitigation is committed.
- **Fail** — the candidate is disqualified by this filter. A waiver requires explicit justification.

Each ADR (001–010) produces a decision structured as follows:

```
## Evaluation

### Hard filter results

| Candidate | F1 · Cost | F2 · Regulatory fit | Proceeds? |
|---|---|---|---|
| Tool A | Pass | Pass | Yes |
| Tool B | Fail — [reason] | — | No |

### Soft criteria

**Tool A** — [one paragraph covering S1–S4]

**Tool C** — [one paragraph covering S1–S4]

## Decision

**Chosen: Tool A**
[1–2 sentences: the decisive reason, not a list of everything good about it.]

**Rejected: Tool C**
[1–2 sentences: the decisive reason for rejection.]

## Consequences

[What this choice makes easier. What it makes harder or impossible. Any residual risks.]
```

---

## Consequences

**What this framework makes easier:**
- Decisions are comparable across ADRs — the same criteria applied consistently.
- The zero-budget and 1–2 person constraints are explicit, so future readers understand why apparently "obvious" enterprise tools were not chosen.
- Regulatory obligations are checked for every tool, not just the ones that feel security-relevant.

**What this framework trades away:**
- No numerical scores means the final verdict requires judgement. Two people reading the same prose could reach different conclusions.
- The hard filters are calibrated for a POC. A production system would tighten F1 (cost is no longer zero) and F2 (full compliance, not just structural compatibility). ADRs will need revisiting before any production hardening.

**Residual risk:**
- Free-tier limits and licences change. An ADR that passes F1 today may fail it in 12 months if a vendor changes its pricing. Each ADR should note the date of the free-tier assessment.
