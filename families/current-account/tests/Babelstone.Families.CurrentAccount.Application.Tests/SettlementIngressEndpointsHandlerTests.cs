using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Babelstone.Engine;
using Babelstone.EventStore;
using Babelstone.Families.CurrentAccount;
using Babelstone.FinancialTypes;
using Babelstone.Packs;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Babelstone.Families.CurrentAccount.Application.Tests;

/// <summary>
/// The engine-CA SETTLEMENT INGRESS handler branches (ADR-PC-043). In plain English: the
/// settlement saga POSTs to three fixed paths (<c>/v1/reservations</c>, <c>/v1/debits</c>, <c>/v1/credits</c>)
/// and this adapter maps each onto the current-account authorize / capture / credit-receive writers. The
/// sibling <see cref="SettlementIngressTests"/> exercise the pure wire binding + the idempotency-key derivation;
/// these drive the HANDLER logic itself — the parse/validate 400s, the dedup replay, the decline / reject 422s,
/// and the mapped success responses — so the ingress's decision branches are actually exercised, not just its
/// helpers.
/// </summary>
/// <remarks>
/// Docker-free by construction (ADR-PC-010): the handlers run against a REAL
/// <see cref="AggregateRuntime{TState}"/> whose ports are in-memory fakes (an in-memory event store/sink, a
/// null PII protector, a plain-JSON store codec) and the three REAL command services wired on top of it. The
/// <see cref="ICommandLog"/> dedup pre-check is a controllable fake, and account lifecycle is seeded by folding
/// the family's own <c>AccountOpened</c> / <c>AccountClosed</c> events through the same runtime — so a Closed
/// account genuinely drives the credit-admission decider's ACCOUNT_CLOSED reject, an unmatched target hold
/// genuinely drives the capture decider's reject, and an Active account with funds genuinely places a hold. No
/// Postgres, no Testcontainers — the same idiom the personal-loan <c>DisbursementDeSettleServiceTests</c> use.
///
/// The private static handler methods (<c>ReserveAsync</c> / <c>ConfirmDebitAsync</c> / <c>ConfirmCreditAsync</c>)
/// are invoked through reflection: they are the minimal-API lambdas <c>SettlementIngressEndpoints.Map</c>
/// registers, so calling them directly exercises exactly the code path an HTTP request would, without standing
/// up a host. The asserted outcome is the concrete <c>IResult</c>'s status code / payload — a real behavioural
/// assertion, never a bare invoke-for-coverage.
/// </remarks>
public sealed class SettlementIngressEndpointsHandlerTests
{
    private const string Actor = "svc:settlement-dispatch";

    // ---- The three handlers, reached by reflection (they are private static minimal-API lambdas). ----

    private static Task<IResult> ReserveAsync(
        SettlementLegRequest request, CurrentAccountAuthorizeService service, ICommandLog log, TimeProvider clock) =>
        InvokeHandler("ReserveAsync", request, service, log, clock);

    private static Task<IResult> ConfirmDebitAsync(
        SettlementLegRequest request, CurrentAccountCaptureService service, AggregateRuntime<AccountPosition> runtime,
        ICommandLog log, TimeProvider clock) =>
        InvokeHandler("ConfirmDebitAsync", request, service, runtime, log, clock);

    private static Task<IResult> ConfirmCreditAsync(
        SettlementLegRequest request, CurrentAccountCreditReceiveService service,
        AggregateRuntime<AccountPosition> runtime, ICommandLog log, TimeProvider clock) =>
        InvokeHandler("ConfirmCreditAsync", request, service, runtime, log, clock);

    private static async Task<IResult> InvokeHandler(string name, params object[] leading)
    {
        var method = typeof(SettlementIngressEndpoints).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)!;
        var args = new object[leading.Length + 1];
        Array.Copy(leading, args, leading.Length);
        args[^1] = CancellationToken.None;
        return await (Task<IResult>)method.Invoke(null, args)!;
    }

    // ========================= Validation 400s (Resolve → BadAccountOrAmount) =========================

    [Theory]
    [InlineData(0L)]     // zero amount
    [InlineData(-1L)]    // negative amount
    public async Task Reserve_rejects_a_non_positive_amount_with_400(long amountCents)
    {
        // amount_cents must be a positive integer in cents (ADR-PC-010): the amount guard runs before the
        // account/intent checks, so a non-positive amount is the specific 400 even with a valid account_ref.
        var world = World.Fresh();
        var request = Leg(Guid.NewGuid(), amountCents, "RSV-1");

        var result = await ReserveAsync(request, world.Authorize, world.Log, world.Clock);

        AssertStatus(result, StatusCodes.Status400BadRequest);
    }

    [Fact]
    public async Task Reserve_rejects_a_non_guid_account_ref_with_400()
    {
        // account_ref must be the engine current-account id (a GUID) on an engine-ca leg (ADR-PC-043): a legacy
        // ACT-token that reached the engine-ca path by misconfiguration is a 400 — fail loud, never guess.
        var world = World.Fresh();
        var request = new SettlementLegRequest("ACT-not-a-guid", 500_000, IntentReference: "RSV-1");

        var result = await ReserveAsync(request, world.Authorize, world.Log, world.Clock);

        AssertStatus(result, StatusCodes.Status400BadRequest);
    }

    [Fact]
    public async Task Reserve_rejects_a_missing_intent_reference_with_400()
    {
        // intent_reference is required — the settlement append command_id derives from it (ADR-PC-043). A body
        // with a good account and amount but NO intent (and no fall-back reference) is the intent-specific 400.
        var world = World.Fresh();
        var request = new SettlementLegRequest(Guid.NewGuid().ToString(), 500_000);

        var result = await ReserveAsync(request, world.Authorize, world.Log, world.Clock);

        AssertStatus(result, StatusCodes.Status400BadRequest);
    }

    [Fact]
    public async Task ConfirmDebit_rejects_a_non_guid_account_ref_with_400()
    {
        var world = World.Fresh();
        var request = new SettlementLegRequest("ACT-token", 500_000, CoreHoldRef: "CORE-HOLD-1");

        var result = await ConfirmDebitAsync(request, world.Capture, world.Runtime, world.Log, world.Clock);

        AssertStatus(result, StatusCodes.Status400BadRequest);
    }

    [Fact]
    public async Task ConfirmCredit_rejects_a_zero_amount_with_400()
    {
        var world = World.Fresh();
        var request = new SettlementLegRequest(Guid.NewGuid().ToString(), 0, CreditRef: "CREDIT-1");

        var result = await ConfirmCreditAsync(request, world.Credit, world.Runtime, world.Log, world.Clock);

        AssertStatus(result, StatusCodes.Status400BadRequest);
    }

    // ========================= Dedup replay (ICommandLog pre-check hit) =========================

    [Fact]
    public async Task Reserve_replays_an_authorized_verdict_on_a_dedup_hit_with_no_second_append()
    {
        // A saga reissue of the reserve leg hits command_dedup: the ingress replays the ORIGINAL verdict from
        // the already-appended HoldPlaced (ReconstructVerdictAsync) — same hold, no second append (ADR-PC-029).
        var world = World.Fresh();
        var accountId = Guid.NewGuid();
        await world.OpenAccount(accountId);
        const string intent = "RSV-replay-1";
        var authorizeCommandId = SettlementIntentKey.Derive("AUTHORIZE-HOLD:" + intent);
        var holdId = $"hold-{authorizeCommandId:N}";

        // Seed the original HoldPlaced the reserve leg appended; the reconstruct reads it back at its sequence.
        var head = await world.Append(accountId, new HoldPlaced(
            accountId, holdId, accountId.ToString(), new Money(500_000), Today));
        world.Log.Seed(authorizeCommandId, accountId, head);

        var result = await ReserveAsync(Leg(accountId, 500_000, intent), world.Authorize, world.Log, world.Clock);

        AssertStatus(result, StatusCodes.Status200OK);
        Assert.Single(world.Store.AppendedEvents, e => e is HoldPlaced); // exactly one — the replay appended nothing
    }

    [Fact]
    public async Task Reserve_replays_a_declined_verdict_on_a_dedup_hit()
    {
        // The replay branch is symmetric on a decline: the single appended AuthorizationDeclined is
        // reconstructed to the same declined verdict (a 200 carrying the declined outcome, ADR-PC-029).
        var world = World.Fresh();
        var accountId = Guid.NewGuid();
        await world.OpenAccount(accountId);
        const string intent = "RSV-replay-declined";
        var authorizeCommandId = SettlementIntentKey.Derive("AUTHORIZE-HOLD:" + intent);

        var head = await world.Append(accountId, new AuthorizationDeclined(
            accountId, AccountDeclinedReason.InsufficientAvailableBalance, new Money(500_000), Today));
        world.Log.Seed(authorizeCommandId, accountId, head);

        var result = await ReserveAsync(Leg(accountId, 500_000, intent), world.Authorize, world.Log, world.Clock);

        // A reconstructed decline is still a settlement result (a 200) — ReconstructVerdict does NOT re-map it to
        // 422; the 422 decline is only produced on a FRESH authorize (asserted separately below).
        AssertStatus(result, StatusCodes.Status200OK);
    }

    [Fact]
    public async Task ConfirmDebit_replays_the_original_verdict_on_a_dedup_hit()
    {
        // The confirm-debit dedup hit replays via the account STATUS read (StatusAsync over the runtime), no
        // second capture — a redelivered capture lands exactly one Debit (ADR-PC-043 double-guard, command side).
        var world = World.Fresh();
        var accountId = Guid.NewGuid();
        await world.OpenAccount(accountId);
        const string intent = "CORE-HOLD-replay-1";
        var captureCommandId = SettlementIntentKey.Derive(intent);
        world.Log.Seed(captureCommandId, accountId, commitSequence: 7);

        var result = await ConfirmDebitAsync(
            Leg(accountId, 500_000, intent), world.Capture, world.Runtime, world.Log, world.Clock);

        AssertStatus(result, StatusCodes.Status200OK);
    }

    [Fact]
    public async Task ConfirmCredit_replays_the_original_verdict_on_a_dedup_hit()
    {
        var world = World.Fresh();
        var accountId = Guid.NewGuid();
        await world.OpenAccount(accountId);
        const string intent = "CREDIT-replay-1";
        var creditCommandId = SettlementIntentKey.Derive(intent);
        world.Log.Seed(creditCommandId, accountId, commitSequence: 3);

        var result = await ConfirmCreditAsync(
            Leg(accountId, 500_000, intent), world.Credit, world.Runtime, world.Log, world.Clock);

        AssertStatus(result, StatusCodes.Status200OK);
    }

    // ========================= Happy paths (fresh, real service success) =========================

    [Fact]
    public async Task Reserve_places_a_hold_on_an_active_funded_account_and_returns_the_deterministic_hold_link()
    {
        // The reserve → authorize mapping: an Active account with available funds authorizes, so the ingress
        // places hold-{authorizeCommandId:N} (the deterministic reserve→confirm link) and returns a 200
        // settlement result (ADR-PC-043).
        var world = World.Fresh(balanceCents: 1_000_000);
        var accountId = Guid.NewGuid();
        await world.OpenAccount(accountId);
        const string intent = "RSV-happy-1";
        var expectedHoldId = $"hold-{SettlementIntentKey.Derive("AUTHORIZE-HOLD:" + intent):N}";

        var result = await ReserveAsync(Leg(accountId, 500_000, intent), world.Authorize, world.Log, world.Clock);

        AssertStatus(result, StatusCodes.Status200OK);
        // The real authorize decider placed exactly the deterministic hold the confirm leg will reconstruct.
        var placed = Assert.Single(world.Store.AppendedEvents.OfType<HoldPlaced>());
        Assert.Equal(expectedHoldId, placed.HoldId);
    }

    [Fact]
    public async Task Reserve_returns_422_when_the_authorize_is_declined_for_insufficient_funds()
    {
        // A DECLINED authorize is a 422 on this settlement surface, never a 200-with-Declined the dispatcher
        // would mis-read as Applied: an Active account with ZERO available balance declines the debit, so the
        // ingress surfaces a 422 (the source holds the funds, ADR-PC-043 error model).
        var world = World.Fresh(balanceCents: 0);
        var accountId = Guid.NewGuid();
        await world.OpenAccount(accountId);

        var result = await ReserveAsync(
            Leg(accountId, 500_000, "RSV-declined-1"), world.Authorize, world.Log, world.Clock);

        AssertStatus(result, StatusCodes.Status422UnprocessableEntity);
    }

    [Fact]
    public async Task Reserve_returns_422_when_the_account_is_not_active()
    {
        // The family lifecycle gate refuses a debit on a non-Active account (ACCOUNT_NOT_ACTIVE): a Closed
        // account declines regardless of balance, so the reserve leg is a 422 (ADR-PC-043).
        var world = World.Fresh(balanceCents: 1_000_000);
        var accountId = Guid.NewGuid();
        await world.OpenAccount(accountId);
        await world.CloseAccount(accountId);

        var result = await ReserveAsync(
            Leg(accountId, 500_000, "RSV-closed-1"), world.Authorize, world.Log, world.Clock);

        AssertStatus(result, StatusCodes.Status422UnprocessableEntity);
    }

    [Fact]
    public async Task ConfirmDebit_captures_the_matching_hold_and_returns_200()
    {
        // The confirm-debit → capture mapping: given the SAME hold the reserve leg placed
        // (hold-{authorizeCommandId:N}) is ACTIVE, the capture matches it, appends the HoldCaptured +
        // AccountDebited batch, and returns a 200 settlement result (ADR-PC-043).
        var world = World.Fresh(balanceCents: 1_000_000);
        var accountId = Guid.NewGuid();
        await world.OpenAccount(accountId);
        const string intent = "CORE-HOLD-happy-1";
        var authorizeCommandId = SettlementIntentKey.Derive("AUTHORIZE-HOLD:" + intent);
        var holdId = $"hold-{authorizeCommandId:N}";
        world.Holds.SeedActive(holdId, accountId.ToString(), amountCents: 500_000);

        var result = await ConfirmDebitAsync(
            Leg(accountId, 500_000, intent), world.Capture, world.Runtime, world.Log, world.Clock);

        AssertStatus(result, StatusCodes.Status200OK);
        Assert.Single(world.Store.AppendedEvents.OfType<AccountDebited>());
    }

    [Fact]
    public async Task ConfirmDebit_returns_422_when_the_target_hold_is_not_active()
    {
        // A capture naming NO active authorization hold is a DomainRejectedException the ingress maps to a 422
        // (terminal Refused; the source holds the funds), never a phantom debit (ADR-PC-043). No hold seeded.
        var world = World.Fresh(balanceCents: 1_000_000);
        var accountId = Guid.NewGuid();
        await world.OpenAccount(accountId);

        var result = await ConfirmDebitAsync(
            Leg(accountId, 500_000, "CORE-HOLD-unmatched"), world.Capture, world.Runtime, world.Log, world.Clock);

        AssertStatus(result, StatusCodes.Status422UnprocessableEntity);
        Assert.Empty(world.Store.AppendedEvents.OfType<AccountDebited>());
    }

    [Fact]
    public async Task ConfirmCredit_lands_the_credit_on_an_active_account_and_returns_200()
    {
        // The confirm-credit → credit-receive mapping: an Active account admits the credit, so the ingress
        // appends an AccountCredited and returns a 200 settlement result (ADR-PC-043).
        var world = World.Fresh();
        var accountId = Guid.NewGuid();
        await world.OpenAccount(accountId);

        var result = await ConfirmCreditAsync(
            Leg(accountId, 500_000, "CREDIT-happy-1"), world.Credit, world.Runtime, world.Log, world.Clock);

        AssertStatus(result, StatusCodes.Status200OK);
        Assert.Single(world.Store.AppendedEvents.OfType<AccountCredited>());
    }

    [Fact]
    public async Task ConfirmCredit_returns_422_when_the_account_is_closed()
    {
        // A non-admitting account is a 4xx, never a 200-with-Declined the dispatcher would march to COMPLETED:
        // a Closed account rejects the credit (ACCOUNT_CLOSED) by construction, so the ingress is a 422 and no
        // AccountCredited folds into a closed account (ADR-PC-043).
        var world = World.Fresh();
        var accountId = Guid.NewGuid();
        await world.OpenAccount(accountId);
        await world.CloseAccount(accountId);

        var result = await ConfirmCreditAsync(
            Leg(accountId, 500_000, "CREDIT-closed-1"), world.Credit, world.Runtime, world.Log, world.Clock);

        AssertStatus(result, StatusCodes.Status422UnprocessableEntity);
        Assert.Empty(world.Store.AppendedEvents.OfType<AccountCredited>());
    }

    [Fact]
    public async Task ConfirmCredit_falls_back_to_the_credit_ref_when_no_intent_reference_is_threaded()
    {
        // The intent-reference fall-back is a live handler branch: a pre-threading body carrying only credit_ref
        // still resolves an exactly-once key, so the credit lands (a 200), exercising Resolve's FirstNonBlank arm.
        var world = World.Fresh();
        var accountId = Guid.NewGuid();
        await world.OpenAccount(accountId);
        var request = new SettlementLegRequest(accountId.ToString(), 500_000, CreditRef: "CREDIT-fallback-1");

        var result = await ConfirmCreditAsync(request, world.Credit, world.Runtime, world.Log, world.Clock);

        AssertStatus(result, StatusCodes.Status200OK);
    }

    // ================================ Fixtures / in-memory harness ================================

    private static DateOnly Today => DateOnly.FromDateTime(TimeProvider.System.GetUtcNow().UtcDateTime);

    private static SettlementLegRequest Leg(Guid accountId, long amountCents, string intent) =>
        new(accountId.ToString(), amountCents, IntentReference: intent);

    private static void AssertStatus(IResult result, int expected)
    {
        // A minimal-API IResult carries its status on a public StatusCode property (Ok / Problem both do).
        var prop = result.GetType().GetProperty("StatusCode");
        var status = prop?.GetValue(result) as int?;
        Assert.Equal(expected, status);
    }

    /// <summary>
    /// The whole DB-free world one handler call runs against: an in-memory event store/sink, the family
    /// registry, stub read stores, and the three REAL services wired on top. Fresh per test so no state leaks.
    /// </summary>
    private sealed class World
    {
        public required InMemoryStore Store { get; init; }
        public required AggregateRuntime<AccountPosition> Runtime { get; init; }
        public required CurrentAccountAuthorizeService Authorize { get; init; }
        public required CurrentAccountCaptureService Capture { get; init; }
        public required CurrentAccountCreditReceiveService Credit { get; init; }
        public required FakeCommandLog Log { get; init; }
        public required StubHoldStore Holds { get; init; }
        public TimeProvider Clock => TimeProvider.System;

        public static World Fresh(long balanceCents = 0)
        {
            var store = new InMemoryStore();
            var runtime = new AggregateRuntime<AccountPosition>(
                store: store,
                sink: store,
                handlers: CurrentAccountFamilyModule.Registry(),
                serializer: new JsonEventSerializer(),
                protector: new NullPiiProtector(),
                clock: TimeProvider.System,
                seedState: () => AccountPosition.Empty);

            var holds = new StubHoldStore();
            var movements = new StubMovementLedgerStore(balanceCents);
            var freezes = new StubFreezeStore();
            var balances = new AccountBalanceReader(movements, holds);
            var drainer = new SpineProjectionDrainer(
                store, new StubCheckpointStore(), new JsonEventSerializer(),
                [new CurrentAccountFamilyModule()], [], TimeProvider.System);
            var pack = MinimalPack();

            return new World
            {
                Store = store,
                Runtime = runtime,
                Holds = holds,
                Log = new FakeCommandLog(),
                Authorize = new CurrentAccountAuthorizeService(
                    runtime, balances, new AccountFreezeReader(freezes), drainer, pack, store,
                    new JsonEventSerializer(), new NullPiiProtector(),
                    CurrentAccountProductConfigStore.FromConfigs([])),
                Capture = new CurrentAccountCaptureService(runtime, balances, drainer, pack),
                Credit = new CurrentAccountCreditReceiveService(runtime, pack),
            };
        }

        public Task<long> Append(Guid accountId, DomainEvent @event) => AppendMany(accountId, @event);

        public async Task<long> AppendMany(Guid accountId, params DomainEvent[] events)
        {
            var hydrated = await Runtime.LoadAsync(accountId);
            return await Runtime.AppendAsync(
                accountId, hydrated.Version, events,
                new AppendContext("current_account", "pt/2026.1", "1", Actor, Clock.GetUtcNow()));
        }

        public Task OpenAccount(Guid accountId) =>
            AppendMany(accountId, new AccountOpened(accountId, "ca_pt_basic", "EUR", Today));

        public Task CloseAccount(Guid accountId) =>
            AppendMany(accountId, new AccountClosed(accountId, Today, "CUSTOMER_REQUEST"));
    }

    /// <summary>A plain-JSON store codec standing in for the Avro codec (the term-deposit / personal-loan idiom):
    /// SchemaId is a constant 1, and the runtime's payloads decode back as JSON.</summary>
    private sealed class JsonEventSerializer : IEventSerializer
    {
        public EncodedPayload Encode(DomainEvent @event)
            => new(JsonSerializer.SerializeToUtf8Bytes(@event, @event.GetType()), SchemaId: 1);

        public DomainEvent Decode(ReadOnlyMemory<byte> payload, Type payloadType)
            => (DomainEvent)JsonSerializer.Deserialize(payload.Span, payloadType)!;
    }

    /// <summary>One in-memory store that is BOTH the <see cref="IEventStore"/> the runtime loads from and the
    /// <see cref="IEventSink"/> it appends through, so load-then-append cycles rehydrate the seeded account.
    /// Records the decoded appended events so a test can assert exactly what was appended.</summary>
    private sealed class InMemoryStore : IEventStore, IEventSink
    {
        private readonly Dictionary<Guid, List<EventEnvelope>> _streams = [];
        private readonly List<DomainEvent> _appended = [];
        private static readonly HandlerRegistry Registry = CurrentAccountFamilyModule.Registry();
        private static readonly JsonEventSerializer Serializer = new();

        public IReadOnlyList<DomainEvent> AppendedEvents => _appended;

        public Task AppendAsync(
            Guid streamId, long expectedVersion, IReadOnlyList<EventEnvelope> events,
            IReadOnlyList<OutboxRow> outboxRows, Guid? commandId = null, CancellationToken ct = default)
        {
            if (!_streams.TryGetValue(streamId, out var stream))
            {
                stream = [];
                _streams[streamId] = stream;
            }

            stream.AddRange(events);
            foreach (var envelope in events)
            {
                Registry.TryResolveByEventType(envelope.EventType, out var registration);
                _appended.Add(Serializer.Decode(envelope.Payload, registration!.PayloadType));
            }

            return Task.CompletedTask;
        }

        public async IAsyncEnumerable<EventEnvelope> LoadAsync(
            Guid streamId, long fromSequence = 0, [EnumeratorCancellation] CancellationToken ct = default)
        {
            if (_streams.TryGetValue(streamId, out var stream))
            {
                foreach (var envelope in stream)
                {
                    if (envelope.SequenceNumber >= fromSequence)
                    {
                        yield return envelope;
                    }
                }
            }

            await Task.CompletedTask;
        }

        public Task<IReadOnlyList<Guid>> ReadStreamIdsAsync(string family, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<Guid>>(_streams.Keys.ToList());
    }

    /// <summary>A controllable <see cref="ICommandLog"/>: a seeded command id returns its receipt (the dedup
    /// pre-check hit that drives the ingress's replay branch); an unseeded one returns null (the fresh path).</summary>
    private sealed class FakeCommandLog : ICommandLog
    {
        private readonly Dictionary<Guid, CommandReceipt> _receipts = [];

        public void Seed(Guid commandId, Guid streamId, long commitSequence) =>
            _receipts[commandId] = new CommandReceipt(commandId, streamId, commitSequence);

        public Task<CommandReceipt?> TryGetAsync(Guid commandId, CancellationToken ct = default) =>
            Task.FromResult(_receipts.TryGetValue(commandId, out var r) ? r : null);
    }

    /// <summary>A stub active-hold read: the capture decider matches the command's target hold against what
    /// GetActiveHoldsAsync returns, so seeding one ACTIVE row makes the happy-path capture find its hold.</summary>
    private sealed class StubHoldStore : IAccountHoldStore
    {
        private readonly List<AccountHoldRow> _active = [];

        public void SeedActive(string holdId, string accountRef, long amountCents) =>
            _active.Add(new AccountHoldRow(
                holdId, accountRef, amountCents, ValueDate: null, State: "ACTIVE",
                PlacedStreamId: Guid.NewGuid(), PlacedSequence: 0));

        public Task<IReadOnlyList<AccountHoldRow>> GetActiveHoldsAsync(string accountRef, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<AccountHoldRow>>(_active.Where(h => h.AccountRef == accountRef).ToList());

        public Task<long> GetActiveHoldCentsAsync(string accountRef, CancellationToken ct = default)
            => Task.FromResult(_active.Where(h => h.AccountRef == accountRef).Sum(h => h.AmountCents));

        public Task<long> GetWindowedAuthorizationHoldCentsAsync(
            string accountRef, DateOnly fromInclusive, DateOnly toInclusive, CancellationToken ct = default)
            => Task.FromResult(0L);

        // Unused on the exercised paths — the ingress never drives these read/write methods DB-free.
        public Task PlaceAsync(AccountHoldRow hold, CancellationToken ct = default) => Task.CompletedTask;
        public Task PlaceLegalAsync(AccountHoldRow legalHold, CancellationToken ct = default) => Task.CompletedTask;
        public Task<HoldReleaseResult> ReleaseLegalAsync(
            string holdId, Guid releasedStreamId, long releasedSequence, CancellationToken ct = default)
            => Task.FromResult(HoldReleaseResult.Transitioned);
        public Task<HoldReleaseResult> CaptureAsync(
            string holdId, long capturedAmountCents, Guid releasedStreamId, long releasedSequence,
            CancellationToken ct = default) => Task.FromResult(HoldReleaseResult.Transitioned);
        public Task<HoldReleaseResult> ExpireAsync(
            string holdId, Guid releasedStreamId, long releasedSequence, CancellationToken ct = default)
            => Task.FromResult(HoldReleaseResult.Transitioned);
        public Task<IReadOnlyList<AccountHoldRow>> GetActiveHoldsWithValueDateAtOrBeforeAsync(
            DateOnly valueDateHorizon, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<AccountHoldRow>>([]);
        public Task<IReadOnlyList<AccountHoldRow>> GetActiveLegalHoldsWithExpiryAtOrBeforeAsync(
            DateOnly expiryHorizon, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<AccountHoldRow>>([]);
        public Task TruncateAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    /// <summary>A stub movement ledger: reports a fixed accounting balance (so the authorize decider's
    /// available-balance read is controllable) and no statement lines.</summary>
    private sealed class StubMovementLedgerStore(long balanceCents) : IMovementLedgerStore
    {
        public Task<long> GetBalanceCentsAsync(string accountRef, CancellationToken ct = default)
            => Task.FromResult(balanceCents);

        public Task AppendAsync(IReadOnlyList<MovementLedgerEntry> entries, CancellationToken ct = default)
            => Task.CompletedTask;
        public Task<IReadOnlyList<OverdrawnAccount>> GetOverdrawnAccountsAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<OverdrawnAccount>>([]);
        public Task<IReadOnlyList<MovementLedgerEntry>> GetStatementAsync(
            string accountRef, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<MovementLedgerEntry>>([]);
        public Task TruncateAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    /// <summary>A stub freeze read: the account is never frozen, so the authorize decider's freeze gate is
    /// transparent on the exercised paths.</summary>
    private sealed class StubFreezeStore : IAccountFreezeStore
    {
        public Task<AccountFreezeRow?> GetActiveFreezeAsync(Guid instanceId, CancellationToken ct = default)
            => Task.FromResult<AccountFreezeRow?>(null);

        public Task FreezeAsync(AccountFreezeRow freeze, CancellationToken ct = default) => Task.CompletedTask;
        public Task<FreezeLiftResult> UnfreezeAsync(
            string freezeId, Guid liftedStreamId, long liftedSequence, string unfreezeActor, string unfreezeReason,
            CancellationToken ct = default) => Task.FromResult(FreezeLiftResult.Transitioned);
        public Task<IReadOnlyList<AccountFreezeRow>> GetActiveFreezesWithExpiryAtOrBeforeAsync(
            DateOnly expiryHorizon, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<AccountFreezeRow>>([]);
        public Task TruncateAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    /// <summary>A no-op projection checkpoint store: the drainer with an empty projector list folds nothing, so
    /// the checkpoint reads/writes are inert on the exercised paths.</summary>
    private sealed class StubCheckpointStore : IProjectionCheckpointStore
    {
        public Task<ProjectionCheckpointRecord?> ReadAsync(
            string projectionKind, Guid streamId, CancellationToken ct = default)
            => Task.FromResult<ProjectionCheckpointRecord?>(null);
        public Task WriteAsync(ProjectionCheckpointRecord record, CancellationToken ct = default) => Task.CompletedTask;
        public Task ResetAsync(string projectionKind, CancellationToken ct = default) => Task.CompletedTask;
    }

    // The services touch the pack only for its VersionKey (stamped on the AppendContext), so a minimal
    // structurally-valid pack suffices — mirrors the personal-loan DisbursementDeSettleServiceTests helper.
    private static VerifiedPack MinimalPack() => new(
        Manifest: new PackManifest(
            PackId: "pt", PackVersion: "2026.1", Namespace: "pt", ManifestSchemaVersion: 1,
            Publisher: "test", PackEffectiveFrom: new DateOnly(2026, 1, 1), BasedOnPackVersion: null,
            DeltaSummary: "", BreakingChanges: [], EngineCompatibleVersions: "*",
            SchemaPins: new Dictionary<string, string>(), RateSheetRefNames: [], TemplateRefNames: [], TestCorpusRef: ""),
        DayCounts: new Dictionary<string, PackDayCount>(),
        Withholdings: new Dictionary<string, PackWithholding>(),
        Fgds: new Dictionary<string, PackFgd>(),
        Reportings: new Dictionary<string, PackReporting>(),
        Parameters: new PackParameters(MaxConsumerRateBps: 0, AutoRenewalOptoutWindowDays: 0),
        RateSheetRefs: [],
        Families: []);
}
