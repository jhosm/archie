# Banking Ecosystem — Integration Architecture
## Document 07: Testing Strategy

Testing an event-driven system with sagas and compensations requires inverting some habits. The traditional test pyramid (many unit tests, some integration, rare e2e) still applies in form, but the **content** changes: some levels gain disproportionate weight, others lose.

We start with where the real complexity lives and what makes these systems hard to test — because the right strategy only makes sense against the right problems.

---

## What Makes These Systems Hard to Test

Four sources of difficulty, in increasing order of subtlety:

**1. Multiple systems involved.** A constitution flow touches Deposits, ACL, Core, Compliance, Workflow, Notifications. Testing this end-to-end with real systems is expensive, slow, and fragile.

**2. Asynchronous behaviour.** Commands return before effects materialize. Assertions have to wait (with timeouts, polling, events). "Eventually" replaces "immediately".

**3. Failure paths are where the bugs live.** The happy path is easy to test. Compensations, indeterminate states, retries, idempotency — that's where the real fragility lives, and it's the hardest to exercise.

**4. Distributed contracts.** When 6 systems depend on an event, changing the event without breaking any of them requires mechanical discipline, not visual inspection.

Each level of the pyramid attacks a different source. Unit tests cover (1) — business logic in isolation. Integration tests with testcontainers cover (2) — asynchronous behaviour and infrastructure-level guarantees. Contract tests cover (4) — distributed contracts between bounded contexts. Saga tests cover (3) — failure paths with fault injection, where the bugs actually live.

---

## The Adapted Pyramid

In event-driven systems, the test pyramid looks like this:

```
              ┌──────────┐
              │   E2E    │  ← rare, selective, careful
              └──────────┘
           ┌────────────────┐
           │  Saga Tests    │  ← new level, critical
           └────────────────┘
        ┌──────────────────────┐
        │  Contract Tests      │  ← disproportionate weight
        └──────────────────────┘
     ┌──────────────────────────┐
     │  Integration Tests       │  ← with testcontainers
     └──────────────────────────┘
  ┌────────────────────────────────┐
  │  Unit Tests (pure aggregates)  │  ← rich foundation
  └────────────────────────────────┘
```

Notice two inversions compared with the traditional pyramid:

- **Contract tests gain massive weight.** In monoliths they're unnecessary; here they are existential.
- **Saga tests are their own level.** They don't fit into "integration" nor "e2e" — they have their own nature.

Each level is covered below, from cheapest to most expensive, focusing on **what each one validates** and **what each one cannot validate** — because trying to cover everything at the wrong level is the most common trap.

---

## Level 1: Unit Tests of the Aggregates

Here you live the pure equivalent of the domain: the `Deposit` aggregate tested without network, without DB, without Kafka, without anything. Just objects, methods, and business rules.

Rich aggregates ([Primitive 3](./01-the-six-primitives.md)) are designed to be **trivially testable**: in-memory state, pure rules, no I/O. If your aggregate needs mocks to be tested, there is I/O contamination that should be outside it.

### What You Validate at This Level

- Aggregate invariants (an active deposit has a maturity_date in the future, the sum of interest paid never exceeds the computed amount, etc.)
- Valid and invalid state transitions (`Deposit.cancel()` fails if already mobilized, succeeds if in draft)
- Financial computations (interest, penalties, withholding tax)
- Domain events emitted by operations
- Preconditions of compensations

### Concrete Example, in Pseudo-Code

```
test "deposit cannot be cancelled after activation":
  deposit = Deposit.draft(client_id, amount, ...)
  deposit.activate(core_txn_id, ...)
  
  assertThrows DepositCannotBeCancelledException:
    deposit.cancel(reason="changed_mind")

test "early mobilization within first 30 days applies maximum penalty":
  deposit = aDeposit().activeSince(daysAgo=15).build()
  
  result = deposit.earlyMobilize(today)
  
  assert result.penalty == deposit.accruedInterest  // 100% loss
  assert result.events contains EarlyMobilizationApplied
  assert deposit.state == MOBILIZED
```

### Characteristics of These Tests

- Thousands of them, execute in seconds
- Always deterministic
- Refactoring the implementation doesn't break them (they test behaviour, not structure)
- Cover business rules exhaustively

**What these tests CANNOT validate:** anything involving infrastructure, events actually published, DB idempotency, contracts with other systems. For that, you climb the pyramid.

### Test Data Builders — Don't Neglect

In systems with rich aggregates, **fluent builders** for creating test fixtures are a critical investment:

```
aDeposit()
  .forClient("CLI-007842")
  .ofProduct("TD-TRAD-12M")
  .ofAmount(1000000)
  .activeSince(daysAgo=15)
  .withInterestModality(AT_MATURITY)
  .build()
```

Without builders, each test has 30 lines of setup. With builders, 2 lines. The difference determines how many cases you can actually cover.

---

## Level 2: Integration Tests With Real Infrastructure

Here you climb up to test with **real Kafka, real DB, but inside a single process/context**. You validate that the integration between the aggregate and its infrastructure works correctly.

**Testcontainers is the tool of choice.** You spin up a Postgres in a container, a Kafka in a container, possibly a WireMock to simulate the Core, all programmatically, isolated per test, disposable.

### What You Validate at This Level

- The outbox pattern works: aggregate persists + event appears in Kafka, or nothing is persisted
- The inbox pattern deduplicates correctly under concurrent load
- The projector consumes events and updates the read model correctly
- API gateway idempotency works in real conditions
- The ACL talks to a Core mock correctly, maintains its state store

### Example

```
test "constitution command persists deposit and publishes event atomically":
  // Given
  given(coreMock).willRespond200to("/holds").with(hold_id)
  
  // When  
  api.post("/deposits/constitute", validPayload, idempotencyKey)
  
  // Then  
  eventually(timeout=5s):
    assert deposit exists in DB
    assert event "ConstitutionRequested" appears in Kafka topic
    assert event has correlation_id == request.correlation_id

test "outbox publisher recovers after Kafka outage":
  // Given
  given(kafkaContainer).isStopped()
  api.post("/deposits/constitute", payload)
  
  // When
  given(kafkaContainer).isStarted()
  
  // Then
  eventually(timeout=10s):
    assert event appears in Kafka topic
    assert outbox row status == PUBLISHED

test "duplicate command with same idempotency key produces single deposit":
  key = "idem-test-123"
  
  api.post("/deposits/constitute", payload, idempotencyKey=key)
  api.post("/deposits/constitute", payload, idempotencyKey=key)
  
  assert count(deposits) == 1
  assert count(events("ConstitutionRequested")) == 1
```

### Essential Characteristic: Assertions With `eventually`

In tests of asynchronous systems, forget immediate `assert`. Everything is "eventually, within N seconds". Libraries like **Awaitility** (Java) or explicit polling are the foundation. Without this, you either have flaky tests, or you're hiding asynchrony.

**What these tests CANNOT validate:** behaviour of multiple services collaborating (a saga crosses process boundaries), contracts with real external systems, behaviour under real distributed failures.

---

## Level 3: Contract Tests — the Level With Disproportionate Weight

In event-driven systems, **the contract is the product**. Each integration event is a public API for multiple consumers. Silently breaking a contract is the fast track to production incidents.

Contract testing is what makes it mechanically impossible to change an event without the system screaming.

**Two complementary flavours: schema contracts and consumer-driven contracts.**

### Schema Contracts

Validated by the schema registry. Before publishing a new schema version, the registry checks compatibility (backward/forward, as seen in Plumbing). If incompatible, the build fails. In continuous production, this is a CI/CD gate.

But schema alone isn't enough. Schema says "the field exists and has type X". It doesn't say "consumer X expects this field never to be null in production". For that:

### Consumer-Driven Contracts (CDC)

Typically with Pact. Each consumer declares its expectations about the event, in the form of a test. The producer runs those tests against its output. If they fail, it knows it will break that consumer.

Conceptual example:

```
// In the consumer (Notifications):
"when I consume DepositConstituted, I expect:
  - field client_id is a non-null string
  - field amount is positive integer (cents)
  - field maturity_date is ISO date in future
  - metadata.correlation_id is present"

// The producer (Deposits) runs the Pacts of ALL known consumers
// in CI. Fails the build if any one breaks.
```

The value of this: **incompatible changes are detected in the producer's CI, not in production**. The feedback cycle is minutes, not days.

**Pact broker** centralizes these contracts: each consumer publishes its own, each producer downloads them to validate. In large ecosystems (10+ contexts), this becomes mandatory infrastructure.

### Where to Apply Contract Tests in Your System

- All published integration events (DepositConstituted, DepositCancelled, etc.)
- All synchronous APIs between bounded contexts
- The ACL interface to the Core (even if the "consumer" is internal)

### Where NOT to Apply Contract Tests

- Internal domain events (Primitive 2) — volatile, change freely, no external consumers
- Internal implementation details

The rule is simple: **if it crosses a bounded context boundary, it has a contract and it has a contract test**.

---

## Level 4: Saga Tests — the Level That Deserves to Exist

Distributed sagas have their own nature that doesn't fit into the previous levels. Here you validate that **the choreography/orchestration works end-to-end**, including compensations.

Typical setup: isolated environment with all services of the context running (or stubbed), real Kafka, ACL pointed at a controllable Core mock. **It's not production in miniature; it's the environment where you manipulate failures deliberately.**

### Three Families of Saga Tests, All Critical

#### 4.1 — Saga Happy Path

The whole constitution, from click to DepositConstituted on the backbone. Validate that:
- All steps execute in the right order
- All expected events appear
- The final state is correct in all participants
- Timing is within expected range (not strict SLA, but order of magnitude)

These tests are expensive but rare. You have **one** per main saga (Constitution, Early Mobilization, Maturity with renewal), not hundreds.

#### 4.2 — Saga Tests With Fault Injection

This is where the real value lies, and where 90% of bugs live in distributed systems.

You simulate failures at specific points in the flow and validate that the correct compensation executes:

```
test "if Compliance fails after Core debit confirmed, capital is reversed":
  // Given - configure failure point
  given(complianceMock).willFailOn("ConfirmRegistration").afterCalls(1)
  
  // When
  api.constituteDeposit(largeAmountPayload)
  
  // Then - happy path failed mid-way
  eventually:
    assert deposit.state == CANCELLED_AFTER_DEBIT
    assert core received reversal credit with reference to original debit
    assert event "DepositCancelled" published with reason="compliance_failure"
    assert notifications event "ConstitutionFailed" was published
```

**Scenarios to cover explicitly for each critical saga:**

| Scenario | What it validates |
|---|---|
| Failure in initial validation | Saga fails early, no external effects |
| Failure after reservations (holds) | Releases of the holds execute |
| Failure after confirmed debit | Reversal credit in the Core |
| Timeout on call to the Core | ACL enters INDETERMINATE, clearance job executes |
| Compensation fails 3x | HUMAN_INTERVENTION_REQUIRED state, alert fires |
| Duplicate message in Kafka | Inbox deduplicates, idempotency preserved |
| Out-of-order event | System converges to correct state or rejects explicitly |
| Service restart mid-flow | Saga resumes from persisted state, no loss |

This matrix **is the heart of the testing strategy** for your system. Each scenario is a test; each test protects against a class of real production incident.

#### 4.3 — Chaos / Property-Based Saga Tests

The more advanced level: instead of specific scenarios, you generate **random failures** (network drops, messages arriving out of order, duplicates, delays) and verify **invariant properties** that must always hold true:

- "For any sequence of operations, money is never duplicated nor lost"
- "For any sequence of failures, the system converges to a consistent state within N minutes"
- "An executed compensation leaves the system in a valid domain state"

Property-based testing (with libraries like Jqwik, ScalaCheck, fast-check) generates hundreds of random sequences and searches for counter-examples. It finds bugs you would never think to test explicitly.

**It's not where you start**, but it's where you end up if you want maximum confidence. For banking systems, it's worth the investment in at least one or two critical scenarios.

---

## Level 5: E2E Tests — Rare, Selective, Careful

End-to-end with real systems (including a real Core in a shared environment) should be **exceptional**. Reasons:

- Expensive (require a shared environment, coordinated test data, aligned teams)
- Slow (seconds to minutes per test)
- Fragile (a thousand things can fail outside your control)
- Poorly diagnostic (they fail but you don't know where)

### Where They Make Sense

- Pre-production "smoke test": 1-2 critical scenarios validate that the environment is globally healthy
- Specific regulatory validations that require the real system
- Performance/load tests

### Where They Do NOT Make Sense

- Validating business logic (do it at Level 1)
- Validating contracts (do it at Level 3)
- Validating compensations (do it at Level 4)

The rule: **if you can validate at lower levels, do it**. E2E is the last resort, not the first.

---

## Testing Idempotency Specifically

[Idempotency (Primitive 5)](./01-the-six-primitives.md) deserves explicit treatment because it is the primitive most frequently poorly tested.

### The Wrong Test

```
test "API is idempotent":
  api.post("/deposits", payload, key="x")
  api.post("/deposits", payload, key="x")
  assert count(deposits) == 1  // ok, this is the basic
```

### The Right Test, More Rigorous

```
test "idempotency holds across full saga execution":
  // first call
  result1 = api.post("/deposits", payload, key="x")
  // wait for saga to progress mid-flight
  waitUntil(processState == VALIDATIONS_COMPLETE)
  
  // duplicate during saga in flight
  result2 = api.post("/deposits", payload, key="x")
  
  // duplicate after saga complete
  waitUntil(processState == COMPLETED)
  result3 = api.post("/deposits", payload, key="x")
  
  assert result1 == result2 == result3  // same deposit_id, same status
  assert exactly_one(deposit in DB)
  assert exactly_one(core debit happened)
  assert exactly_one(DepositConstituted in Kafka)
```

Idempotency must be robust across **three distinct time windows**: before the saga begins, during the saga in progress, after the saga completes. Naive tests only cover the first.

---

## Test Environments — a Layered Strategy

Different test levels live in different environments:

| Level | Environment | Cost |
|---|---|---|
| Unit | Local, in IDE | ~0 |
| Integration | Testcontainers in CI | Low |
| Contract | Pact broker in CI | Low |
| Saga (happy + fault injection) | Dedicated saga-testing environment | Medium |
| Saga (chaos/property) | Dedicated, long-running environment | Medium-high |
| E2E | Shared pre-production environment | High |
| Performance | Production-like environment | High |

**Critical principle:** lower environments run on every commit (fast CI). Higher ones run in slower cycles (nightly, pre-release, on-demand). Never everything in the critical path, never anything skipped for time reasons.

---

## Test Data — the Problem Nobody Anticipates

In banking systems, test data is a problem of its own:

- Real client data cannot be used (GDPR, ethics, common sense)
- Synthetic data has to be realistic enough to exercise edge cases
- Coordination between multiple systems: client "X" must exist in CRM, have an account in the Core, KYC OK in Compliance

### Solutions

1. **Test data factories** that generate coherent synthetic data across systems (same `client_id`, consistently propagated)
2. **Reset-table snapshots** of saga/E2E environments between executions
3. **Stable test personas** (`PERSONA_CLIENT_VIP`, `PERSONA_CLIENT_NEW`, `PERSONA_CLIENT_INSUFFICIENT_FUNDS`) with well-defined profiles

**Don't neglect this.** In real banking projects, test data management is usually one of the biggest QA time consumers, and it's where poor testing strategies collapse.

---

## The Relationship With Observability ([Document 06](./06-observability-and-tracing.md))

The two documents reinforce each other:

- **Observability facilitates testing**: when a saga test fails, structured traces and logs tell you immediately where. Without this, debugging saga tests is torture.
- **Testing instruments observability**: each saga test can validate that the right spans were created, that the right attributes are present, that the right metrics incremented. **Observability is a product that also needs to be tested**, not just configured.

Concretely:

```
test "constitution saga produces expected telemetry":
  api.constituteDeposit(payload)
  
  eventually:
    assert trace exists with correlation_id == X
    assert trace contains spans: [
      "api.gateway", "aggregate.deposit.create",
      "outbox.publish", "orchestrator.consume", ...
    ]
    assert business metric "deposits_constituted_total" incremented by 1
    assert log line with level=INFO and message containing "transitioned to APPROVED"
```

This is especially valuable because **dashboards and alerts depend on these signals**. If a refactoring accidentally removes a metric that an alert depends on, without this kind of test you discover in production.

---

## Where to Invest First — Pragmatic Recommendation

In greenfield, with a learning team, the order of investment I recommend:

1. **Unit tests of aggregates** from day zero. Culture, not option.
2. **Integration tests with testcontainers** for critical patterns (outbox, inbox, idempotency). Before the first feature goes to production.
3. **Contract tests with Pact** as soon as the second consumer of an event appears. Don't wait for the third.
4. **Happy path saga tests** for the two or three central sagas (Constitution, Mobilization, Maturity) before production.
5. **Saga tests with fault injection** for each failure scenario identified in design exercises (see matrix above). This is the most neglected front and the one that gives the most return.
6. **Property-based tests** only if the team has maturity and the first 5 levels are consolidated.
7. **E2E** only what is strictly necessary, and always as a complement, never as a substitute for lower levels.

---

## The Principle That Unites Everything: Trust the Pyramid, but Invert the Proportions

In event-driven systems with distributed sagas, the traditional pyramid **in form holds** (more cheap tests, fewer expensive ones). But the **content shifts**: contract tests and saga tests with fault injection take disproportionate weight, because that's where the bugs actually live in this kind of architecture.

Teams that directly import the test pyramid from monoliths into event-driven systems discover in production that they had 90% unit coverage and 0% coverage in the zones where incidents happen. The adjustment is deliberate and necessary.
