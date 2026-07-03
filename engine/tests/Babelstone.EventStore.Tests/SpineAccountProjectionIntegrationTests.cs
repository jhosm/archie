using System.Text.Json;
using Babelstone.Engine;
using Babelstone.FinancialTypes;
using Xunit;

namespace Babelstone.EventStore.Tests;

/// <summary>
/// Integration tests for the spine account-projection drive — <see cref="SpineProjectionDrainer"/>
/// feeding <see cref="MovementLedgerProjector"/> + <see cref="AccountHoldProjector"/> off REAL
/// appends to a real PostgreSQL event store. This is the ACCOUNT_BALANCE_IS_A_FOLD gate
/// (ADR-PC-033) over the ADR-PC-032 read side: after real appends the movement ledger holds the
/// account's statement and signed-sum balance; the available balance is
/// <c>accounting − Σ(active holds)</c>; at-least-once re-delivery re-applies as a no-op; and a
/// discard-and-rebuild (truncate + refold) reproduces the ledger, the hold set, and both balances
/// READ-identically from the stream (the BIGSERIAL surrogate is excluded from every read).
/// </summary>
[Trait("Category", "Integration")]
public sealed class SpineAccountProjectionIntegrationTests(PostgresEventStoreFixture fixture)
    : IClassFixture<PostgresEventStoreFixture>
{
    private static readonly DateOnly ValueDate = new(2026, 6, 25);
    private static readonly DateTimeOffset Origin = new(2026, 6, 25, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Real_appends_drive_the_ledger_statement_and_signed_sum_balance()
    {
        var runtime = Runtime();
        var account = NewAccount();
        var stream = Guid.NewGuid();

        // Two real appends on one stream: a 100.00 credit, then a 30.00 debit + 2.50 credit on one
        // multi-movement event (the renewal shape, ADR-PC-032).
        await AppendAsync(runtime, stream, 0,
            new TestMoneyMover([Credit(account, 10_000)]));
        await AppendAsync(runtime, stream, 1,
            new TestMoneyMover([Debit(account, 3_000), Credit(account, 250)]));

        var folded = await runtime.Drainer.DrainOnceAsync();

        Assert.Equal(2, folded);
        Assert.Equal(7_250, await runtime.Ledger.GetBalanceCentsAsync(account));
        var statement = await runtime.Ledger.GetStatementAsync(account);
        Assert.Equal(3, statement.Count); // one line per movement, the multi-movement legs distinct
        Assert.Equal(7_250, await runtime.Balances.GetAccountingBalanceCentsAsync(account));
    }

    [Fact]
    public async Task Movements_for_one_account_aggregate_across_streams()
    {
        var runtime = Runtime();
        var account = NewAccount();
        var streamA = Guid.NewGuid();
        var streamB = Guid.NewGuid();

        // Cross-stream, one account_ref: exactly why the drive is a spine singleton and not the
        // per-family/per-stream ProjectionDrainer.
        await AppendAsync(runtime, streamA, 0, new TestMoneyMover([Credit(account, 5_000)]));
        await AppendAsync(runtime, streamB, 0, new TestMoneyMover([Debit(account, 1_200)]));

        await runtime.Drainer.DrainOnceAsync();

        Assert.Equal(3_800, await runtime.Ledger.GetBalanceCentsAsync(account));
    }

    [Fact]
    public async Task The_hold_lifecycle_folds_into_the_available_balance()
    {
        var runtime = Runtime();
        var account = NewAccount();
        var stream = Guid.NewGuid();

        await AppendAsync(runtime, stream, 0, new TestMoneyMover([Credit(account, 10_000)]));
        await AppendAsync(runtime, stream, 1,
            new HoldPlaced(stream, $"{account}-h1", account, new Money(4_000), ValueDate));
        await runtime.Drainer.DrainOnceAsync();

        // available = accounting − Σ(active holds) (ADR-PC-033): the accounting balance is
        // untouched by the earmark; only spendability drops.
        Assert.Equal(10_000, await runtime.Balances.GetAccountingBalanceCentsAsync(account));
        Assert.Equal(6_000, await runtime.Balances.GetAvailableBalanceCentsAsync(account));

        // A partial capture releases the WHOLE earmark (ADR-PC-033); the posting movement of the
        // capture rides its own Movement-bearing event and moves the accounting balance.
        await AppendAsync(runtime, stream, 2,
            new HoldCaptured(stream, $"{account}-h1", account, new Money(2_500), ValueDate.AddDays(1)));
        await AppendAsync(runtime, stream, 3, new TestMoneyMover([Debit(account, 2_500)]));
        await runtime.Drainer.DrainOnceAsync();

        Assert.Equal(7_500, await runtime.Balances.GetAccountingBalanceCentsAsync(account));
        Assert.Equal(7_500, await runtime.Balances.GetAvailableBalanceCentsAsync(account));
        Assert.Empty(await runtime.Balances.GetActiveHoldsAsync(account));
    }

    [Fact]
    public async Task An_expired_hold_restores_the_available_balance_with_no_posting()
    {
        var runtime = Runtime();
        var account = NewAccount();
        var stream = Guid.NewGuid();

        await AppendAsync(runtime, stream, 0, new TestMoneyMover([Credit(account, 10_000)]));
        await AppendAsync(runtime, stream, 1,
            new HoldPlaced(stream, $"{account}-h1", account, new Money(4_000), ValueDate));
        await AppendAsync(runtime, stream, 2,
            new HoldExpired(stream, $"{account}-h1", account, ValueDate.AddDays(7)));

        await runtime.Drainer.DrainOnceAsync();

        Assert.Equal(10_000, await runtime.Balances.GetAccountingBalanceCentsAsync(account));
        Assert.Equal(10_000, await runtime.Balances.GetAvailableBalanceCentsAsync(account));
    }

    [Fact]
    public async Task At_least_once_redelivery_re_applies_as_a_no_op()
    {
        var runtime = Runtime();
        var account = NewAccount();
        var stream = Guid.NewGuid();

        await AppendAsync(runtime, stream, 0, new TestMoneyMover([Credit(account, 10_000)]));
        await AppendAsync(runtime, stream, 1,
            new HoldPlaced(stream, $"{account}-h1", account, new Money(4_000), ValueDate));

        await runtime.Drainer.DrainOnceAsync();
        // A caught-up second pass folds nothing new…
        Assert.Equal(0, await runtime.Drainer.DrainOnceAsync());

        // …and a LOST CHECKPOINT (the crash-between-apply-and-checkpoint shape) re-delivers the
        // whole tail, which must re-apply as a no-op: the ledger's ON CONFLICT identity and the
        // hold store's active-only transitions absorb it.
        await runtime.Checkpoints.ResetAsync(SpineProjectionDrainer.CheckpointKind);
        var refolded = await runtime.Drainer.DrainOnceAsync();

        Assert.True(refolded > 0); // the tail was genuinely re-delivered
        Assert.Single(await runtime.Ledger.GetStatementAsync(account)); // no double-count
        Assert.Single(await runtime.Holds.GetActiveHoldsAsync(account)); // no double-earmark
        Assert.Equal(10_000, await runtime.Ledger.GetBalanceCentsAsync(account));
        Assert.Equal(6_000, await runtime.Balances.GetAvailableBalanceCentsAsync(account));
    }

    [Fact]
    public async Task Rebuild_reproduces_ledger_holds_and_balances_identically()
    {
        var runtime = Runtime();
        var account = NewAccount();
        var stream = Guid.NewGuid();

        await AppendAsync(runtime, stream, 0,
            new TestMoneyMover([Credit(account, 10_000)]));
        await AppendAsync(runtime, stream, 1,
            new HoldPlaced(stream, $"{account}-h1", account, new Money(4_000), ValueDate));
        await AppendAsync(runtime, stream, 2,
            new HoldPlaced(stream, $"{account}-h2", account, new Money(1_000), ValueDate.AddDays(1)));
        await AppendAsync(runtime, stream, 3,
            new HoldCaptured(stream, $"{account}-h1", account, new Money(4_000), ValueDate.AddDays(2)));
        await AppendAsync(runtime, stream, 4,
            new TestMoneyMover([Debit(account, 4_000)]));

        await runtime.Drainer.DrainOnceAsync();
        var statementBefore = await runtime.Ledger.GetStatementAsync(account);
        var holdsBefore = await runtime.Holds.GetActiveHoldsAsync(account);
        var accountingBefore = await runtime.Balances.GetAccountingBalanceCentsAsync(account);
        var availableBefore = await runtime.Balances.GetAvailableBalanceCentsAsync(account);

        // The ACCOUNT_BALANCE_IS_A_FOLD drill (ADR-PC-033): discard and rebuild — truncate both
        // read models, reset the checkpoints, refold every stream from 0.
        var refolded = await runtime.Drainer.RebuildAsync();

        Assert.True(refolded >= 5);
        Assert.Equal(statementBefore, await runtime.Ledger.GetStatementAsync(account));
        Assert.Equal(holdsBefore, await runtime.Holds.GetActiveHoldsAsync(account));
        Assert.Equal(accountingBefore, await runtime.Balances.GetAccountingBalanceCentsAsync(account));
        Assert.Equal(availableBefore, await runtime.Balances.GetAvailableBalanceCentsAsync(account));
        // Sanity anchor: accounting = 10_000 − 4_000 = 6_000; active holds = h2's 1_000 (h1 was
        // captured), so available = 5_000 — the same on both sides of the rebuild.
        Assert.Equal(6_000, accountingBefore);
        Assert.Equal(5_000, availableBefore);
    }

    // --- the synthetic spine-test family ---
    //
    // Family-agnostic by construction: the drive decodes through IFamilyModule bindings (spine
    // types), so a synthetic module suffices — no real family is named. Each Runtime() gets its
    // OWN family name, so ReadStreamIdsAsync scopes every test to its own streams and the shared
    // per-class Postgres fixture needs no cross-test cleanup.

    private sealed record TestState;

    private sealed record TestMoneyMover(IReadOnlyList<Movement> Movements) : DomainEvent, IMovementBearing;

    private sealed class NoOp<TEvent> : IEventHandler<TestState, TEvent> where TEvent : DomainEvent
    {
        public HandlerResult<TestState> Apply(TestState state, TEvent @event) =>
            HandlerResult<TestState>.From(state);
    }

    private sealed class SpineTestFamilyModule(string familyName) : IFamilyModule
    {
        public string FamilyName => familyName;
        public string SchemaVersion => $"{familyName}@1";
        public IReadOnlyList<HandlerRegistration> Handlers =>
        [
            new($"{familyName}.MoneyMoved", typeof(TestMoneyMover),
                new DispatchableHandler<TestState, TestMoneyMover>(new NoOp<TestMoneyMover>())),
            // The three cross-cutting hold facts, bound exactly as a real family binds them (the
            // CrossCuttingEventRegistrations splice) — no-op folds; the spine projector owns the set.
            new("operations.HoldPlaced", typeof(HoldPlaced),
                new DispatchableHandler<TestState, HoldPlaced>(new NoOp<HoldPlaced>())),
            new("operations.HoldCaptured", typeof(HoldCaptured),
                new DispatchableHandler<TestState, HoldCaptured>(new NoOp<HoldCaptured>())),
            new("operations.HoldExpired", typeof(HoldExpired),
                new DispatchableHandler<TestState, HoldExpired>(new NoOp<HoldExpired>())),
        ];
    }

    // Store-codec double: self-describing enough for the drive (Decode(payload, type)), mirroring
    // the production JsonEventSerializer's STJ round-trip for these structural events.
    private sealed class TestJsonEventSerializer : IEventSerializer
    {
        public EncodedPayload Encode(DomainEvent @event) =>
            new(JsonSerializer.SerializeToUtf8Bytes(@event, @event.GetType()), 0);

        public DomainEvent Decode(ReadOnlyMemory<byte> payload, Type payloadType) =>
            (DomainEvent)JsonSerializer.Deserialize(payload.Span, payloadType)!;
    }

    private sealed record TestRuntime(
        SpineProjectionDrainer Drainer,
        PostgresEventStore Events,
        IMovementLedgerStore Ledger,
        IAccountHoldStore Holds,
        AccountBalanceReader Balances,
        PostgresProjectionCheckpointStore Checkpoints,
        IFamilyModule Module,
        IEventSerializer Serializer);

    private TestRuntime Runtime()
    {
        // A unique family per test isolates ReadStreamIdsAsync (and hence drain counts) on the
        // shared fixture database.
        var module = new SpineTestFamilyModule($"spinetest_{Guid.NewGuid():N}");
        var events = new PostgresEventStore(fixture.ConnectionString);
        var ledger = new PostgresMovementLedgerStore(fixture.ConnectionString);
        var holds = new PostgresAccountHoldStore(fixture.ConnectionString);
        var checkpoints = new PostgresProjectionCheckpointStore(fixture.ConnectionString);
        var serializer = new TestJsonEventSerializer();
        var drainer = new SpineProjectionDrainer(
            events,
            checkpoints,
            serializer,
            [module],
            [new MovementLedgerProjector(ledger), new AccountHoldProjector(holds)],
            TimeProvider.System);
        return new TestRuntime(
            drainer, events, ledger, holds, new AccountBalanceReader(ledger, holds), checkpoints,
            module, serializer);
    }

    private static string NewAccount() => $"acct-{Guid.NewGuid():N}";

    private static Movement Credit(string account, long cents) => new(
        account, SettlementDirection.Credit, new Money(cents), ValueDate,
        MovementOperation.Disburse, MovementOrigin.Originated, Guid.NewGuid());

    private static Movement Debit(string account, long cents) => new(
        account, SettlementDirection.Debit, new Money(cents), ValueDate,
        MovementOperation.CollectInstallment, MovementOrigin.Originated, Guid.NewGuid());

    // A REAL append through PostgresEventStore (not a seeded in-memory read): envelope built the
    // way the aggregate runtime builds one, store-only (no outbox rows — every event here is
    // uncatalogued).
    private async Task AppendAsync(TestRuntime runtime, Guid streamId, long sequence, DomainEvent @event)
    {
        var eventType = @event switch
        {
            HoldPlaced => "operations.HoldPlaced",
            HoldCaptured => "operations.HoldCaptured",
            HoldExpired => "operations.HoldExpired",
            _ => $"{runtime.Module.FamilyName}.MoneyMoved",
        };
        var encoded = runtime.Serializer.Encode(@event);

        await runtime.Events.AppendAsync(
            streamId,
            expectedVersion: sequence - 1,
            events:
            [
                new EventEnvelope(
                    EventId: Guid.NewGuid(),
                    StreamId: streamId,
                    SequenceNumber: sequence,
                    EventType: eventType,
                    EventSchemaVersion: 1,
                    Family: runtime.Module.FamilyName,
                    PartitionKey: streamId,
                    PackVersion: "test",
                    SchemaVersion: runtime.Module.SchemaVersion,
                    ValidTime: Origin.AddDays(sequence),
                    TransactionTime: Origin.AddHours(sequence),
                    CausationId: null,
                    CorrelationId: null,
                    Actor: "test",
                    Payload: encoded.Bytes,
                    PayloadSchemaId: encoded.SchemaId),
            ],
            outboxRows: []);
    }
}
