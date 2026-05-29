# Engine Event-Store Skeleton — Implementation Companion

> Companion to the binding sources for the engine's source-of-truth core. This
> document describes the **C# expression** of decisions the ADRs and the
> feature-design concept doc have already pinned; it does not decide anything.
> The contracts here trace one-for-one to specific ADR sections — when a
> contract drifts from its anchor, the ADR wins and this document is updated
> to match.
>
> **Binding sources:** [ADR-PC-001 §P1–§P5](../../docs/product-management/product_concepts/adrs/ADR-PC-001-event-store-technology.md) (PostgreSQL event store, the `events` table contract, atomic append + outbox, append-only by role privilege, indices, major-version upgrade drill); [ADR-IC-004 §P1–§P7](../../docs/product-management/integration_concepts/adrs/ADR-IC-004-outbox-pattern-mechanism.md) (outbox table shape, polling publisher contract, lag SLI, backpressure semantics); [ADR-PC-010 §P3–§P5](../../docs/product-management/product_concepts/adrs/ADR-PC-010-dotnet-hand-rolled-engine.md) (the hand-rolled module implements §P1 directly, no library-internal columns, determinism + forward-only schemas as CI-enforced engine code); [ADR-PC-004 §P1–§P5](../../docs/product-management/product_concepts/adrs/ADR-PC-004-pii-crypto-shredding.md) (OpenBao per-subject envelope, PII annotation contract, post-erasure semantics); [feature-design-event-store-projections §4–§8](../../docs/product-management/product_concepts/feature-design-event-store-projections.md) (envelope, handler discipline, projections, replay reconciliation, snapshots).
>
> **Scope of this document.** Epic A — `archie-e6fr` — and only that. Bitemporal projections (ADR-PC-002), saga orchestration (ADR-PC-010 §P4, ADR-IC-003), Avro codec specifics (ADR-IC-002), Kong / edge surface (ADR-IC-006), and the v4-scale load harness (Epic L) are deliberately out of scope.
>
> Reading order: §1 frame · §2 project layout · §3 dependency direction · §4 headline types · §5 the three answered design questions · §6 story-to-artifact map · §7 build order · §8 what this document does not decide · §9 cross-references.

---

## 1. Frame

The engine's source of truth is a thin, hand-rolled event-sourcing module on PostgreSQL ([ADR-PC-010 §Decision](../../docs/product-management/product_concepts/adrs/ADR-PC-010-dotnet-hand-rolled-engine.md)). The `events` and `outbox` tables, the atomic append, the load + rehydrate path, the snapshot machinery, the handler dispatcher, the OpenBao encrypt seam, and the CI determinism gate are all engine-team code against a fully-specified contract ([ADR-PC-001 §P1–§P5](../../docs/product-management/product_concepts/adrs/ADR-PC-001-event-store-technology.md), [ADR-IC-004 §P1–§P7](../../docs/product-management/integration_concepts/adrs/ADR-IC-004-outbox-pattern-mechanism.md)). Marten and Wolverine are studied as **working reference implementations of the patterns the engine reproduces, not as runtime dependencies** ([ADR-PC-010 §Decision](../../docs/product-management/product_concepts/adrs/ADR-PC-010-dotnet-hand-rolled-engine.md) — the reference-implementation paragraph after the numbered reasons).

This document records the shape the implementation lands in:

- which C# projects appear under `engine/src/` and `engine/tests/`,
- which contracts they expose,
- where the three external dependencies (PostgreSQL via Npgsql, OpenBao via its transit HTTP API, Redpanda + Confluent SR via Confluent.Kafka) cross the assembly boundary,
- and which story in Epic A produces which artifact.

The goal of capturing it ahead of code is straightforward: the three structural choices in §5 below (assembly split, family-module binding shape, PII-encrypt seam location) are decisions that are cheap to make in a doc and expensive to undo in code once seven implementation PRs cite them.

---

## 2. Project layout

Post-Epic-A, `engine/Babelstone.slnx` carries the existing financial-math kernel plus five new source projects and four new test projects:

```
engine/
├── Babelstone.slnx
├── Directory.Build.props
├── src/
│   ├── Babelstone.FinancialTypes/         (exists — Money, financial primitives)
│   ├── Babelstone.FinancialMath/          (exists — day-count, accrual, withholding, rates)
│   ├── Babelstone.Money.Analyzers/        (exists — BMNY001/002/003)
│   ├── Babelstone.EventStore/             ★ A.1, A.2, A.3, A.4
│   ├── Babelstone.EventStore.Migrations/  ★ A.1 — SQL DDL + a thin migration runner
│   ├── Babelstone.Pii/                    ★ A.5 — OpenBao transit client + field envelope
│   ├── Babelstone.Engine/                 ★ A.6 — handler registry, dispatch, aggregate runtime, snapshot policy, family-module loader
│   └── Babelstone.Engine.Analyzers/       ★ A.7 — BENG001/002/003 (no clock / no I/O / no rng in handlers)
└── tests/
    ├── Babelstone.FinancialTypes.Tests/   (exists)
    ├── Babelstone.FinancialMath.Tests/    (exists)
    ├── Babelstone.Money.Analyzers.Tests/  (exists)
    ├── Babelstone.EventStore.Tests/       ★ Testcontainers PG; per-A.x test fixtures
    ├── Babelstone.Pii.Tests/              ★ Testcontainers OpenBao
    ├── Babelstone.Engine.Tests/           ★ in-process + fixture-replay determinism
    └── Babelstone.Engine.Analyzers.Tests/ ★ Roslyn analyser harness
```

The polling publisher itself ([ADR-IC-004 §P1–§P7](../../docs/product-management/integration_concepts/adrs/ADR-IC-004-outbox-pattern-mechanism.md)) is **not** in Epic A — it depends on Redpanda + the schema registry, which the walking-skeleton epic (Epic E) is the first to bring online. The publisher lands in its own assembly (`Babelstone.OutboxPublisher`) hosted in-process as an `IHostedService` inside `Babelstone.Engine`'s ASP.NET Core host (§5.1). Filing it as a bd story is a follow-up.

### 2.1 Why this split and not others

Three boundaries earn their separate assemblies:

- **`EventStore` is Npgsql-only.** No OpenBao client, no Confluent.Kafka client, no domain types. Its contract is the §P1 `events` table plus the §P1 outbox table shape from IC-004. A future Go re-implementation of this assembly is what the [ADR-PC-001 §S3](../../docs/product-management/product_concepts/adrs/ADR-PC-001-event-store-technology.md) "lowest exit cost" claim cashes out as.
- **`Pii` isolates the deliberate exception.** [ADR-PC-004 §Decision](../../docs/product-management/product_concepts/adrs/ADR-PC-004-pii-crypto-shredding.md) names OpenBao as "the deliberate exception to the engine's hand-rolled-core posture." Keeping the OpenBao SDK in its own assembly makes the exception visible in the project graph rather than scattered.
- **`Engine` is the orchestration layer.** Handler registry, dispatch, snapshot policy, family-module loader. It composes `EventStore` and `Pii` but does not leak their types upward — handlers see only `Babelstone.Engine` abstractions.

The `EventStore.Migrations` project exists separately because its DDL runs under a **different PostgreSQL role** than the engine's runtime role ([ADR-PC-001 §P3](../../docs/product-management/product_concepts/adrs/ADR-PC-001-event-store-technology.md)). The runtime role lacks `UPDATE` / `DELETE` on `events`; the migration role has them. Keeping the migration runner in its own assembly makes "which role is running?" answerable by reading the project reference.

---

## 3. Dependency direction

The dependency graph is one-directional. The arrows below are project references in the `.csproj` files:

```
Babelstone.Engine            ──→  Babelstone.EventStore   ──→  Npgsql
       │
       ├──→  Babelstone.Pii   ──→  OpenBao client (HTTP)
       │
       └──→  Babelstone.FinancialTypes
              (Money, financial primitives)

Family module assemblies      ──→  Babelstone.Engine
(e.g. a future Babelstone.Families.TermDeposit)
                                   ↑
                                   │  references only this — never EventStore, never Pii.
                                   │  Enforced at PR review by inspecting <ProjectReference>.

Babelstone.OutboxPublisher    ──→  Babelstone.EventStore  (reads the outbox table)
                              ──→  Confluent.Kafka + Confluent.SchemaRegistry
```

Three invariants this graph enforces:

1. **`Babelstone.EventStore` does not depend on `Babelstone.Pii`.** Encryption is applied *above* the storage layer, in `AggregateRuntime.AppendAsync` (§5.3). Storage sees ciphertext-in-Avro and does not know it is encrypted.
2. **Family modules cannot reach `Babelstone.EventStore` or `Babelstone.Pii`.** They reference `Babelstone.Engine` only. This is the project-level enforcement of the [event-store §5.1](../../docs/product-management/product_concepts/feature-design-event-store-projections.md) "no I/O in handlers" rule: a handler that wanted to read the database or hit OpenBao would have to add a `<ProjectReference>` that does not exist in any merged family module.
3. **`Babelstone.OutboxPublisher` is the only assembly that touches both PostgreSQL and Redpanda.** This contains the IC-004 reader half to a single bounded place; the engine's write half ([ADR-IC-004 §P6](../../docs/product-management/integration_concepts/adrs/ADR-IC-004-outbox-pattern-mechanism.md), [ADR-PC-001 §P2](../../docs/product-management/product_concepts/adrs/ADR-PC-001-event-store-technology.md)) does not know Redpanda exists.

---

## 4. Headline types

Signatures only. Bodies land in implementation PRs.

### 4.1 The envelope — `Babelstone.EventStore`

This record carries the [ADR-PC-001 §P1](../../docs/product-management/product_concepts/adrs/ADR-PC-001-event-store-technology.md) column contract one-for-one. No `mt_*` fields, no library-internal additions — the §P3 invariant of ADR-PC-010 forbids them.

```csharp
namespace Babelstone.EventStore;

public sealed record EventEnvelope(
    Guid                EventId,
    Guid                StreamId,
    long                SequenceNumber,
    string              EventType,            // "term_deposit.DepositConstituted"
    int                 EventSchemaVersion,
    string              Family,
    Guid                PartitionKey,         // v1 = StreamId; v4 may differ
    string              PackVersion,           // "pt.2026.1"
    string              SchemaVersion,         // "term_deposit@2026.1"
    DateTimeOffset      ValidTime,
    DateTimeOffset      TransactionTime,
    Guid?               CausationId,
    Guid?               CorrelationId,
    string              Actor,
    ReadOnlyMemory<byte> Payload,             // Avro-serialized, PII fields ciphertext
    int                 PayloadSchemaId);     // Confluent SR id, embedded at write
```

The outbox row mirrors [ADR-IC-004 §P1](../../docs/product-management/integration_concepts/adrs/ADR-IC-004-outbox-pattern-mechanism.md) column-for-column:

```csharp
public sealed record OutboxRow(
    Guid                EventId,
    string              AggregateType,
    Guid                AggregateId,
    string              EventType,
    ReadOnlyMemory<byte> Payload,
    int                 SchemaId,
    OutboxStatus        Status,                // PENDING | PUBLISHED
    DateTimeOffset      CreatedAt,
    DateTimeOffset?     PublishedAt);
```

### 4.2 The store contract — `Babelstone.EventStore`

```csharp
public interface IEventStore
{
    // The ES_ATOMIC_APPEND_OUTBOX fitness function: one local PG transaction
    // writes both tables. expectedVersion=-1 means "stream must not exist."
    // Throws ConcurrencyException when the current head differs from expected.
    Task AppendAsync(
        Guid                          streamId,
        long                          expectedVersion,
        IReadOnlyList<EventEnvelope>  events,
        IReadOnlyList<OutboxRow>      outboxRows,
        CancellationToken             ct);

    // Ordered read; caller folds. Snapshot-aware callers pass fromSequence.
    IAsyncEnumerable<EventEnvelope> LoadAsync(
        Guid              streamId,
        long              fromSequence = 0,
        CancellationToken ct = default);
}

public sealed class ConcurrencyException : Exception
{
    public long ExpectedVersion { get; }
    public long ActualVersion   { get; }
}
```

`AppendAsync` is the **only** place in the engine that touches the `events` and `outbox` tables. The data-access layer below it is internal to `Babelstone.EventStore` — no other assembly can construct an `INSERT INTO events` from a leaked helper.

### 4.3 The handler discipline — `Babelstone.Engine`

```csharp
namespace Babelstone.Engine;

// (state, event) → state. Pure. The [feature-design §5.1] signature.
public interface IEventHandler<TState, in TEvent>
{
    HandlerResult<TState> Apply(TState state, TEvent @event);
}

// Side effects come back as scheduled events the runtime routes to outbox rows.
// The handler itself stays pure: no I/O, no clock, no rng (BENG001/002/003).
public sealed record HandlerResult<TState>(
    TState                        NewState,
    IReadOnlyList<ScheduledEffect> PendingEffects);

public sealed record ScheduledEffect(
    string  EventType,            // e.g. "engine.NotificationScheduled"
    object  Payload);

// Marker for the dispatch path that does not need the closed generic type.
public interface IDispatchableHandler
{
    HandlerResult<object> ApplyBoxed(object state, object @event);
}

public interface IHandlerRegistry
{
    bool TryResolve(string eventType, out IDispatchableHandler handler);
}
```

### 4.4 The family-module contract — `Babelstone.Engine`

```csharp
public interface IFamilyModule
{
    string FamilyName     { get; }   // "term_deposit"
    string SchemaVersion  { get; }   // "term_deposit@2026.1"
    IReadOnlyList<HandlerRegistration> Handlers          { get; }
    IReadOnlyList<Type>                EventPayloadTypes { get; }
}

public sealed record HandlerRegistration(
    string                EventType,
    IDispatchableHandler  Handler);

public sealed class FamilyModuleLoader
{
    // At engine startup: walk loaded assemblies, find every IFamilyModule,
    // cross-check its manifest against the CUE schema for that family, fail
    // startup loudly on mismatch (handler registered for an event type the
    // CUE schema does not declare, or a CUE event type with no handler).
    public IReadOnlyList<IFamilyModule> LoadAll(IReadOnlyList<Assembly> sources);
}
```

The CUE cross-check is the binding shape between [ADR-PC-006](../../docs/product-management/product_concepts/adrs/ADR-PC-006-cue-schema-language.md) family schemas and the C# handler code. A schema declares the event taxonomy; a module declares which C# function handles which event type; the loader proves they are the same set.

### 4.5 The aggregate runtime — `Babelstone.Engine`

This is where the encrypt seam lives (§5.3) and where the one-transaction guarantee meets the handler path.

```csharp
public sealed class AggregateRuntime<TState>
{
    private readonly IEventStore             _store;
    private readonly ISnapshotStore<TState>  _snapshots;
    private readonly IHandlerRegistry        _handlers;
    private readonly IPiiEnvelope            _pii;       // from Babelstone.Pii
    private readonly IAvroCodec              _avro;

    // Snapshot-then-tail load.
    public async Task<TState> LoadAsync(Guid streamId, CancellationToken ct);

    // Commits handler-returned events + their outbox rows in one PG transaction.
    // Inside this method, in order:
    //   1. Walk each DomainEvent; encrypt PII-annotated fields via IPiiEnvelope
    //      (the only OpenBao seam) → ciphertext payload.
    //   2. Serialize to Avro; build the EventEnvelope (§4.1) with the
    //      pack/schema/valid-time/transaction-time/actor envelope fields.
    //   3. Materialize the matching OutboxRow per event (the IC-004 §P1 row
    //      shape, status=PENDING, schema_id embedded at write time per §P3).
    //   4. Call IEventStore.AppendAsync(streamId, expectedVersion, envelopes,
    //      outboxRows, ct) — one local PG transaction commits both tables.
    // Handlers never touch OpenBao; EventStore sees only ciphertext-in-Avro;
    // the OpenBao dependency lives in exactly one method.
    public async Task AppendAsync(
        Guid                       streamId,
        long                       expectedVersion,
        IReadOnlyList<DomainEvent> events,            // cleartext PII inside
        CancellationToken          ct);
}
```

### 4.6 Snapshots — storage in `Babelstone.EventStore`, typed layer in `Babelstone.Engine`

Snapshots split across the §3 boundary: the §3 invariant is that only
`Babelstone.EventStore` touches Npgsql, and the `snapshots` table is a row in the
same PostgreSQL database, so its **persistence is a storage primitive in
`Babelstone.EventStore`** (byte-oriented, domain-agnostic). The **typed,
state-aware layer is in `Babelstone.Engine`** (it serializes `TState` and knows
lifecycle/calendar boundaries). A.4 delivers the storage half; the typed wrapper,
the take-snapshot policy, and the discard-rebuild *executable* ride with A.6, where
`Babelstone.Engine` and the handler fold exist.

Storage half (`Babelstone.EventStore`, A.4):

```csharp
public sealed record SnapshotRecord(
    Guid                 StreamId,
    long                 AtSequence,
    Guid                 LastEventId,    // folded into StateHash per §8.3
    string               StateHash,      // SnapshotHash.Compute(state, lastEventId)
    ReadOnlyMemory<byte> State,          // serialized projection state
    bool                 Trusted,        // advisory-until-trusted flag (§8.3)
    DateTimeOffset       CreatedAt);

public interface ISnapshotStorage
{
    Task<SnapshotRecord?> TryGetLatestAsync(Guid streamId, CancellationToken ct = default);
    Task                  PutAsync         (SnapshotRecord snapshot, CancellationToken ct = default);
    Task<int>             DiscardAsync     (Guid streamId, CancellationToken ct = default);
}
```

Typed half (`Babelstone.Engine`, A.6):

```csharp
public sealed record Snapshot<TState>(
    long AtSequence, Guid LastEventId, string StateHash, TState State, bool Trusted, DateTimeOffset CreatedAt);

public interface ISnapshotStore<TState>      // serializes TState, delegates to ISnapshotStorage
{
    Task<Snapshot<TState>?> TryGetAsync(Guid streamId, CancellationToken ct);
    Task                     PutAsync   (Guid streamId, Snapshot<TState> s, CancellationToken ct);
}

public interface ISnapshotPolicy<TState>
{
    // The three triggers from §8.1: per-N, lifecycle boundary, calendar boundary.
    bool ShouldSnapshot(SnapshotContext<TState> ctx);
}
```

The monthly discard drill ([feature-design §8.3 / §10.2](../../docs/product-management/product_concepts/feature-design-event-store-projections.md)) lands as a CLI entry point — likely `engine/tools/discard-rebuild-drill/` — that calls `ISnapshotStorage.DiscardAsync` for one stream, replays cold from `events`, and compares hashes. A.4 delivers the `DiscardAsync` primitive (the "drill hook"); the executable that folds and compares rides with A.6 because it needs the handler dispatch. Running it on a calendar cadence is ops practice, not a one-shot ticket.

### 4.7 PII envelope — `Babelstone.Pii`

```csharp
namespace Babelstone.Pii;

public interface IPiiEnvelope
{
    // Walks the domain event using the schema-declared PII annotations
    // (per [ADR-PC-004 §P1]), encrypts PII-annotated fields under the
    // subject's named key, returns the encryption-ready payload.
    Task<EncryptedPayload> EncryptAsync(DomainEvent ev, CancellationToken ct);

    // Returns null for PII fields whose subject key has been destroyed
    // (= GDPR erasure per [ADR-PC-004 §P3]); structural fields intact.
    Task<DomainEvent> DecryptAsync(EncryptedPayload payload, CancellationToken ct);
}

public interface IPiiKeyStore   // OpenBaoTransitClient implements
{
    Task<byte[]>  EncryptAsync   (string subjectId, byte[] plaintext, CancellationToken ct);
    Task<byte[]?> DecryptAsync   (string subjectId, byte[] ciphertext, CancellationToken ct);
    Task          DestroyKeyAsync(string subjectId, CancellationToken ct);   // = erasure
}
```

A.5's "CI rejection of unannotated string fields" lands as a schema-lint test in `Babelstone.Pii.Tests` that walks every loaded family schema and asserts each string field carries either `pii: true` (with `subject_id` named) or `pii: false`. The lint runs in the engine's CI job, not as a separate workflow.

### 4.8 Determinism analysers — `Babelstone.Engine.Analyzers`

Three Roslyn diagnostic IDs, modelled on the BMNY pattern from `Babelstone.Money.Analyzers`:

| ID | Category | Banned in handler bodies |
|---|---|---|
| **BENG001** | Determinism / clock | `DateTime.Now`, `DateTime.UtcNow`, `DateTimeOffset.Now`, `DateTimeOffset.UtcNow`, `Stopwatch.*`, `Environment.TickCount` |
| **BENG002** | Determinism / I/O | `HttpClient.*`, `File.*`, `Directory.*`, `Process.Start`, `DbConnection` and derived types |
| **BENG003** | Determinism / randomness | `Random`, `RandomNumberGenerator.*`, `Guid.NewGuid()` |

Scope: methods that implement `IEventHandler<,>.Apply` (or are reachable from one via a same-project call graph). The analyser does not police the rest of the engine — the engine's hosting layer reads the clock and does I/O, as it must.

A.7 also ships the **fixture-replay determinism test** that the catalogue's `DETERMINISM_GATE` row resolves to: a fixture event sequence, applied by registered handlers, must produce a byte-identical projection across runs. This is the runtime half of the gate; the analysers are the build-time half.

### 4.9 Shipped shapes for A.6/A.8 (deltas from the sketches above)

The §4.3–§4.6 listings are the original design sketches; the code that landed in A.6/A.8 refines them as follows (the ADR anchors are unchanged — this records the C# shape downstream):

- **Dispatch (§4.3).** `IDispatchableHandler.ApplyBoxed(object state, DomainEvent @event)` (a `DomainEvent` base record was introduced for the event hierarchy). `HandlerResult<TState>` gains a `From(state)` helper for the no-effect case. `DispatchableHandler<TState,TEvent>` adapts a typed handler to the boxed path.
- **Family module (§4.4).** `IFamilyModule` drops the separate `EventPayloadTypes` list — each `HandlerRegistration(EventType, PayloadType, Handler, EventSchemaVersion=1)` already carries its payload type, and `HandlerRegistry` builds both the event-type→handler and payload-type→registration maps from it. The `FamilyModuleLoader` CUE cross-check is deferred (archie-e6fr.6) until Epic C/E.
- **Aggregate runtime (§4.5).** `AggregateRuntime<TState>` reads via `IEventStore` and writes via the **`IEventSink`** seam (A.8), not `IEventStore` directly. The §5.3 encrypt seam is fronted by **`IPiiProtector`** (default `NullPiiProtector` until the CUE annotation source lands, archie-e6fr.5) rather than wiring `IPiiEnvelope` in the runtime. The Avro codec is the **`IEventSerializer`** seam. The runtime takes an injected `TimeProvider` (transaction_time) and a `seedState` factory; `LoadAsync` returns `Hydrated<TState>(State, Version, LastEventId)`.
- **Snapshots (§4.6).** As already noted: storage in `EventStore` (`ISnapshotStorage`), typed `Snapshot<TState>`/`SnapshotStore<TState>` + `ISnapshotPolicy`/`CountBasedSnapshotPolicy` in `Engine`.
- **A.8 — simulation is side-effect-free by structure, not by flag.** `SimulationRuntime<TState>` takes only `IEventStore` (read), `HandlerRegistry`, `IEventSerializer`, and a seed — no `IEventSink`, no `IPiiProtector`, no snapshot store. It therefore *cannot* write the log/outbox, mint OpenBao material, or persist a snapshot; rehydration folds structural state without unprotecting PII (ADR-PC-004 §P2: PII is off the structural hot path). The forward-lifecycle-by-clock-advance path (A.8 AC#4/#5, ADR-PC-011) is deferred to archie-e6fr.7.

---

## 5. The three answered design questions

Three structural choices the ADRs leave open. They were answered during the Epic A design discussion before this document was written; recording them here gives a single home so the implementation PRs do not re-litigate them.

### 5.1 The outbox polling publisher runs in-process as an `IHostedService`

The publisher reads `outbox` rows from the same PostgreSQL database the engine writes to, and produces to Redpanda. It is the IC-004 reader half. It lands in its own assembly `Babelstone.OutboxPublisher` (Confluent.Kafka surface stays out of `EventStore`), and the assembly is hosted by `Babelstone.Engine`'s ASP.NET Core process as an `IHostedService` — *not* a separate worker process or image.

Honours the "one codebase, one set of images" commitment from [01 §6 Deployment](../../docs/product-management/product_concepts/01-product-architecture.md) and the "single deployable … one process, one image, one configuration surface" framing in [ADR-PC-010 §Decision](../../docs/product-management/product_concepts/adrs/ADR-PC-010-dotnet-hand-rolled-engine.md). Minimises ops surface for the 1–2 person team. The publisher is not a load-bearing scaling unit — its work is bounded by the engine's own write rate, so co-hosting is the right call until that assumption breaks.

The publisher itself is not a story in Epic A. It rides into the codebase with the walking-skeleton epic (Epic E) where Redpanda + the schema registry come online; until then `Babelstone.Engine` writes outbox rows that no consumer reads.

### 5.2 Family modules ship as .NET assemblies with an `IFamilyModule` manifest

Each family is its own assembly (a future `Babelstone.Families.TermDeposit`, etc.) that exports an `IFamilyModule` implementation (§4.4). At engine startup, `FamilyModuleLoader` walks loaded assemblies, registers every module's `(event_type → handler)` pairs in `IHandlerRegistry`, and **cross-checks the manifest against the CUE schema for that family**. A mismatch — a handler registered for an event type the CUE schema does not declare, or a CUE event type with no handler — fails startup.

Two reasons over attribute-based discovery:

- The CUE-schema-to-C#-binding is the boundary [ADR-PC-006](../../docs/product-management/product_concepts/adrs/ADR-PC-006-cue-schema-language.md) names. Making the binding explicit and checked at boot beats inferring it from `[EventHandler("…")]` decorations.
- The dependency direction in §3 ("family modules cannot reach `EventStore` or `Pii`") is enforceable by inspecting one project reference; attribute discovery would need a separate analyser to police the same property.

### 5.3 PII encryption happens in `AggregateRuntime.AppendAsync`, between handler and store

Handlers return cleartext domain events. `AggregateRuntime.AppendAsync` walks each event's PII-annotated fields, calls `IPiiEnvelope.EncryptAsync` (which calls OpenBao under the hood), swaps cleartext for ciphertext, serializes to Avro, and hands the envelope to `IEventStore.AppendAsync`. Handlers stay pure (§5.1 invariant from the feature-design); `EventStore` stays Npgsql-only (§3 invariant); the OpenBao dependency is visible in exactly one method.

Honours [ADR-PC-004 §Decision](../../docs/product-management/product_concepts/adrs/ADR-PC-004-pii-crypto-shredding.md) ("the deliberate exception to the engine's hand-rolled-core posture") by keeping the exception observable rather than diffusing it through the storage layer or hiding it behind an Avro codec hook.

---

## 6. Story-to-artifact map

| Story | bd ID | Artifacts |
|---|---|---|
| **A.1** events table | `archie-z9po` | `Babelstone.EventStore.Migrations` project + initial migration scripts (`events` + `outbox` tables, §P4 indices, two PG roles per §P3) |
| **A.2** atomic append | `archie-2m49` | `Babelstone.EventStore.IEventStore.AppendAsync` + `ConcurrencyException`; `ES_ATOMIC_APPEND_OUTBOX` test |
| **A.3** load / rehydrate | `archie-6dlh` | `IEventStore.LoadAsync` (ordered `IAsyncEnumerable` read + `fromSequence` tail seam). The snapshot-then-tail caller in `AggregateRuntime.LoadAsync` rides with A.6 |
| **A.4** snapshot machinery | `archie-cyiv` | **storage half (this story):** `snapshots` table (migration `0003`), `SnapshotRecord`, `ISnapshotStorage` + `PostgresSnapshotStore` (incl. `DiscardAsync` drill hook), `SnapshotHash` (§8.3) with Trusted flag. **Typed half (A.6):** `ISnapshotStore<>`, `Snapshot<>`, `ISnapshotPolicy<>` three triggers, `engine/tools/discard-rebuild-drill/` executable |
| **A.5** PII crypto envelope | `archie-qzlb` | `Babelstone.Pii` project, `IPiiEnvelope`, `IPiiKeyStore` + `OpenBaoTransitClient` (OpenBao transit seam). **Deferred (`archie-e6fr.5`):** CUE-driven PII annotation source + the unannotated-string-field schema-lint |
| **A.6** handler dispatch | `archie-n0nq` | `IEventHandler<,>`, `HandlerResult<>`, `IHandlerRegistry`/`HandlerRegistry`, `AggregateRuntime<>` (read via `IEventStore`, write via `IEventSink`; `IPiiProtector` + `IEventSerializer` seams), `IFamilyModule`, `FamilyModuleLoader`, typed `Snapshot<>`/`SnapshotStore<>`/`ISnapshotPolicy`. **Deferred (`archie-e6fr.6`):** the `FamilyModuleLoader` CUE cross-check |
| **A.8** event-sink seam | `archie-e6fr.4` | `IEventSink` (`EventStoreSink` vs `NullSink`); `SimulationRuntime<>` structurally side-effect-free (no sink/protector/snapshot wiring). **Deferred (`archie-e6fr.7`):** forward-lifecycle-by-clock-advance (AC#4/#5, ADR-PC-011) |
| **A.7** determinism CI gate | `archie-k03q` | `Babelstone.Engine.Analyzers` with BENG001/002/003, fixture-replay test (`DETERMINISM_GATE`) |
| **A.9** property-based suite | `archie-e6fr.2` | FsCheck properties over `IEventStore` invariants (sequence monotonicity, no gaps under concurrency, snapshot+tail ≡ cold-fold) |
| **A.10** mutation testing | `archie-e6fr.3` | Stryker.NET config + score floor for `Babelstone.EventStore` + `Babelstone.Engine`; periodic CI lane |

---

## 7. Build order

```
A.1 (events table + migration runner)
   │
   ▼
A.2 (AppendAsync, optimistic concurrency)  ─────────────┐
   │                                                     │
   ▼                                                     │
A.3 (LoadAsync, snapshot-then-tail) ──────────► A.5 (PII envelope — IEventStore is its dep)
   │                                                     │
   │                                          ┌──────────┘
   ▼                                          ▼
A.4 (snapshots, drill hook)         A.6 (dispatch + AggregateRuntime — uses both)
                                              │
                                              ▼
                                  A.7 (analysers + fixture-replay)
                                              │
                                              ▼
                                     A.9 (FsCheck) + A.10 (Stryker)
```

A.5 can run in parallel with A.2/A.3 because it depends on the `IEventStore` *interface*, not the implementation — a fake store is enough to develop and test the encrypt path. A.7's analyser scope is "methods implementing `IEventHandler<,>.Apply`", so it needs the A.6 interface in place to have something to point at. A.9 + A.10 ride after the runtime is whole.

Suggested PR boundaries: **(A.1 + A.2)** first because the table is meaningless without the writer; **A.3** alone; **A.5 + A.6** together (they meet at `AggregateRuntime.AppendAsync`); **A.4 + A.7** as the closing pair; **A.9** and **A.10** as separate follow-ups.

---

## 8. What this document does not decide

The list below is deliberate. These are decisions whose ADRs are open, deferred, or not yet written — putting a sketch in this document would beat the ADR to the answer and create the kind of drift [ADR-PC-020 §D3](../../docs/product-management/product_concepts/adrs/ADR-PC-020-llm-toolchain-and-conformance-governance.md) names as the explicit-drift gate's failure mode.

- **Bitemporal projection storage** ([ADR-PC-002](../../docs/product-management/product_concepts/adrs/ADR-PC-002-application-level-bitemporality.md), [feature-design §6.1](../../docs/product-management/product_concepts/feature-design-event-store-projections.md)) — the projection store decision lives with ADR-PC-002. `Babelstone.Engine` does not host projection code; projections land under their own assembly when projection work begins.
- **Saga state machine** ([ADR-IC-003](../../docs/product-management/integration_concepts/adrs/ADR-IC-003-saga-orchestrator.md), [ADR-PC-010 §P4](../../docs/product-management/product_concepts/adrs/ADR-PC-010-dotnet-hand-rolled-engine.md)) — the in-process saga dispatcher lives in `Babelstone.Engine` per §P4, but its surface (the `saga_state` table, compensation states, the identity trio from [integration_concepts §01 Primitive 4](../../docs/product-management/integration_concepts/01-the-six-primitives.md)) is a separate epic. Epic A leaves the assembly hookable but unhooked.
- **Avro codec choice** ([ADR-IC-002](../../docs/product-management/integration_concepts/adrs/ADR-IC-002-schema-format-and-registry.md)) — the `IAvroCodec` interface in `AggregateRuntime` (§4.5) hides which Avro library lands. The pick (Apache.Avro vs Chr.Avro vs a hand-rolled walker) is tied to the schema-registry integration that Epic E brings online.
- **v4-scale load harness** ([Epic L](../../docs/product-management/product_concepts/feature-design-two-modes-asymmetry.md), [ADR-PC-001 §S1](../../docs/product-management/product_concepts/adrs/ADR-PC-001-event-store-technology.md)) — the 250 TPS sustained / 1000 TPS burst / `REPLAY_BUDGET_5S_30S` gates live under Epic L (`archie-2e6q`), not under Epic A. The engine spine is the *target* of those gates, not their home.
- **OpenBao operational topology** ([ADR-PC-004 §Residual Risks 1](../../docs/product-management/product_concepts/adrs/ADR-PC-004-pii-crypto-shredding.md)) — HA + DR for OpenBao is a deployment concern co-owned with [ADR-PC-005](../../docs/product-management/product_concepts/adrs/ADR-PC-005-dr-rto-rpo.md). The engine consumes it as a transit endpoint; how that endpoint is made available is platform work.

---

## 9. Cross-references

- [ADR-PC-001](../../docs/product-management/product_concepts/adrs/ADR-PC-001-event-store-technology.md) — PostgreSQL event store; §P1 envelope contract, §P2 atomic append + outbox, §P3 append-only by role privilege, §P4 indices, §P5 major-version upgrade drill.
- [ADR-IC-004](../../docs/product-management/integration_concepts/adrs/ADR-IC-004-outbox-pattern-mechanism.md) — Custom polling publisher; §P1 outbox table shape, §P2 publish order, §P3 schema_id at write time, §P4 lag SLI, §P5 cleanup, §P6 one-transaction boundary, §P7 backpressure semantics.
- [ADR-PC-010](../../docs/product-management/product_concepts/adrs/ADR-PC-010-dotnet-hand-rolled-engine.md) — C# (.NET 10) hand-rolled core; §P1–§P2 Money boundary discipline, §P3 the hand-rolled module implements ADR-PC-001 §P1 directly, §P4 sagas as hand-rolled state machines, §P5 determinism + forward-only schemas.
- [ADR-PC-004](../../docs/product-management/product_concepts/adrs/ADR-PC-004-pii-crypto-shredding.md) — OpenBao per-subject envelope; §P1 PII annotation contract, §P2 encrypt at boundary, §P3 erasure = key destruction, §P4 key rotation, §P5 backup-retention tension with DR.
- [feature-design-event-store-projections](../../docs/product-management/product_concepts/feature-design-event-store-projections.md) — §4 event taxonomy + envelope, §5 handler discipline, §6 bitemporal projections (deferred to ADR-PC-002), §7 replay reconciliation, §8 snapshot strategy, §10 risk mitigations.
- [commitment-catalogue](../../docs/product-management/product_concepts/adrs/commitment-catalogue.md) — `ES_ATOMIC_APPEND_OUTBOX` (A.2), `DETERMINISM_GATE` (A.7), `REPLAY_BUDGET_5S_30S` (Epic L's L.3, not Epic A).
- Epic A — bd epic `archie-e6fr`, nine children (A.1–A.7 + A.9 + A.10). Run `bd show archie-e6fr` for the live story list.

---

*This document is implementation companion, not a binding source. When a contract here drifts from its ADR anchor, the ADR wins and this document is updated to match. Substantive design changes happen in the ADRs; this file tracks the C# shape downstream.*
