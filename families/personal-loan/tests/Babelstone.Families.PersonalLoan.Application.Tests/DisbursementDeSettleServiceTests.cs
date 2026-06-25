using System.Text.Json;
using Babelstone.Engine;
using Babelstone.EventStore;
using Babelstone.FinancialTypes;
using Babelstone.Families.PersonalLoan;
using Babelstone.Packs;
using Babelstone.RateSheets;
using Xunit;

namespace Babelstone.Families.PersonalLoan.Application.Tests;

/// <summary>
/// MOVEMENT_APPEND_FIRST for the disbursement path (ADR-PC-032 §Decision slot 5 / commitment 1; bd
/// babelstone-5r9n.1). In plain English: a loan disbursement must record its money movement on the event and
/// append FIRST — it must NOT settle eagerly before the append (the old settle-then-append window that could
/// orphan a cash leg with no durable record). These drive the real <see cref="PersonalLoanConstitutionService"/>
/// against an in-memory sink + a recording settlement port and assert (1) the settlement port is NEVER called
/// on the disbursement path (no eager <c>SettleAsync</c>), and (2) the appended <see cref="LoanDisbursed"/>
/// carries the Originated Credit <see cref="Movement"/> the substrate-owned settlement saga effects the cash
/// leg off. The double-move / settle-succeeds-append-fails idempotency is the substrate saga's
/// MOVEMENT_CASH_LEG_IDEMPOTENT (the WireMock-Core integration test on the settlement side); here we prove the
/// PRODUCER never opens that window — append-first closes it by construction (ADR-PC-031 §D3).
/// </summary>
public sealed class DisbursementDeSettleServiceTests
{
    private const string ProductId = "cp_pt_general_12m";
    private const string Role = "standard";
    private const string AccountRef = "acct-token-borrower";

    [Fact]
    public async Task DisburseAsync_appends_the_movement_bearing_event_and_NEVER_settles_eagerly()
    {
        var sink = new RecordingSink();
        var settlement = new RecordingSettlementPort();
        var service = BuildService(sink, settlement);

        var loanId = Guid.NewGuid();
        var commandId = Guid.NewGuid();
        await service.DisburseAsync(new DisburseLoanCommand(
            LoanId: loanId,
            PrincipalCents: 1_000_000,
            ProductId: ProductId,
            Role: Role,
            TermMonths: 12,
            StartDate: new DateOnly(2026, 1, 1),
            DisbursedAt: new DateTimeOffset(2026, 1, 1, 9, 0, 0, TimeSpan.Zero),
            Purpose: "general",
            DisbursementAccountRef: AccountRef,
            Actor: "test",
            CommandId: commandId));

        // (1) MOVEMENT_APPEND_FIRST: the disbursement path made ZERO eager settlement calls. The cash leg is
        //     the substrate settlement saga's gated, downstream step — not an in-engine pre-append settle.
        Assert.Empty(settlement.Instructions);

        // (2) Append-first: exactly one LoanDisbursed was appended, carrying the Originated Credit Movement.
        var disbursed = Assert.IsType<LoanDisbursed>(Assert.Single(sink.AppendedEvents));
        var movement = Assert.Single(disbursed.Movements!);
        Assert.Equal(SettlementDirection.Credit, movement.Direction);   // the lump sum ENTERS the borrower's account
        Assert.Equal(AccountRef, movement.AccountRef);
        Assert.Equal(new Money(1_000_000), movement.Amount);
        Assert.Equal(MovementOperation.Disburse, movement.Operation);
        Assert.Equal(MovementOrigin.Originated, movement.Origin);       // → the gated settlement saga drives it
        Assert.Equal(commandId, movement.CommandId);

        // The event promotes the headers the settlement saga auto-starts on (the producer hop, t7o3.20).
        Assert.Equal("Originated", disbursed.IntegrationHeaders![MovementHeaders.OriginKey]);
        Assert.Equal("Credit", disbursed.IntegrationHeaders[MovementHeaders.DirectionKey]);
    }

    [Fact]
    public async Task A_precondition_refusal_neither_settles_nor_appends_a_disbursement()
    {
        // The eligibility gate refuses BEFORE the disbursement (ADR-PC-024 §5): no loan opens, so there is
        // nothing to settle and no LoanDisbursed (a refusal event is appended, never a disbursement).
        var sink = new RecordingSink();
        var settlement = new RecordingSettlementPort();
        var service = BuildService(sink, settlement, requiredPreconditions: ["solvency_assessed"]);

        await service.DisburseAsync(new DisburseLoanCommand(
            LoanId: Guid.NewGuid(),
            PrincipalCents: 1_000_000,
            ProductId: ProductId,
            Role: Role,
            TermMonths: 12,
            StartDate: new DateOnly(2026, 1, 1),
            DisbursedAt: new DateTimeOffset(2026, 1, 1, 9, 0, 0, TimeSpan.Zero),
            Purpose: "general",
            DisbursementAccountRef: AccountRef,
            Actor: "test",
            Preconditions: null)); // the required precondition is absent → refuse

        Assert.Empty(settlement.Instructions);
        Assert.IsType<LoanDisbursementFailed>(Assert.Single(sink.AppendedEvents));
    }

    // ---- In-memory harness (Docker-free): a recording sink captures the appended events; the runtime never
    //      touches a real store on the disbursement path (expectedVersion -1, no load). -------------------

    private static PersonalLoanConstitutionService BuildService(
        RecordingSink sink, ISettlementPort settlement, IReadOnlyCollection<string>? requiredPreconditions = null)
    {
        var runtime = new AggregateRuntime<LoanPosition>(
            store: new UnusedEventStore(),
            sink: sink,
            handlers: PersonalLoanFamilyModule.Registry(),
            serializer: new JsonEventSerializer(),
            protector: new NullPiiProtector(),
            clock: TimeProvider.System,
            seedState: () => LoanPosition.Empty);

        return new PersonalLoanConstitutionService(
            runtime, new FlatRateSheetStore(tanBasisPoints: 600), settlement, MinimalPack(), requiredPreconditions);
    }

    /// <summary>Records the settlement instructions it is handed so the test can assert the disbursement path
    /// makes NONE (no eager settle — MOVEMENT_APPEND_FIRST). The install/repay paths still settle through this
    /// port (bd babelstone-t7o3.16's scope), so the port is wired but must stay untouched by disbursement.</summary>
    private sealed class RecordingSettlementPort : ISettlementPort
    {
        private readonly List<SettlementInstruction> _instructions = [];

        public IReadOnlyList<SettlementInstruction> Instructions => _instructions;

        public Task SettleAsync(SettlementInstruction instruction, CancellationToken ct = default)
        {
            _instructions.Add(instruction);
            return Task.CompletedTask;
        }
    }

    /// <summary>A plain JSON codec standing in for the Avro codec (the same idiom the term-deposit tests
    /// use): SchemaId is a constant 1. The runtime is wired with this, so the appended payloads are JSON the
    /// recording sink decodes back.</summary>
    private sealed class JsonEventSerializer : IEventSerializer
    {
        public EncodedPayload Encode(DomainEvent @event)
            => new(JsonSerializer.SerializeToUtf8Bytes(@event, @event.GetType()), SchemaId: 1);

        public DomainEvent Decode(ReadOnlyMemory<byte> payload, Type payloadType)
            => (DomainEvent)JsonSerializer.Deserialize(payload.Span, payloadType)!;
    }

    /// <summary>Records the events handed to the sink so the test can assert exactly what was appended. The
    /// disbursement is the stream's first append (expectedVersion -1), so no read path is exercised.</summary>
    private sealed class RecordingSink : IEventSink
    {
        private readonly List<DomainEvent> _events = [];

        public IReadOnlyList<DomainEvent> AppendedEvents => _events;

        public Task AppendAsync(
            Guid streamId, long expectedVersion, IReadOnlyList<EventEnvelope> events,
            IReadOnlyList<OutboxRow> outboxRows, Guid? commandId = null, CancellationToken ct = default)
        {
            // The runtime hands the sink the JSON-encoded envelopes; decode each back to assert the
            // Movement-bearing event the decider produced (the JSON codec the runtime is wired with).
            var serializer = new JsonEventSerializer();
            foreach (var envelope in events)
            {
                _events.Add(serializer.Decode(envelope.Payload, ResolveType(envelope.EventType)));
            }

            return Task.CompletedTask;
        }

        private static Type ResolveType(string eventType) => eventType switch
        {
            "personal_loan.LoanDisbursed" => typeof(LoanDisbursed),
            "personal_loan.LoanDisbursementFailed" => typeof(LoanDisbursementFailed),
            _ => throw new InvalidOperationException($"unexpected appended event type '{eventType}'"),
        };
    }

    /// <summary>The store is never reached on the disbursement append path (expectedVersion -1 → no load, and
    /// the recording sink is wired directly, not the EventStoreSink). Throws if anything calls it, so a future
    /// load on this path is caught rather than silently passing.</summary>
    private sealed class UnusedEventStore : IEventStore
    {
        public Task AppendAsync(
            Guid streamId, long expectedVersion, IReadOnlyList<EventEnvelope> events,
            IReadOnlyList<OutboxRow> outboxRows, Guid? commandId = null, CancellationToken ct = default)
            => throw new InvalidOperationException("the disbursement append path must not reach the event store directly");

        public IAsyncEnumerable<EventEnvelope> LoadAsync(Guid streamId, long fromSequence = 0, CancellationToken ct = default)
            => throw new InvalidOperationException("the disbursement append path must not read the event store");

        public Task<IReadOnlyList<Guid>> ReadStreamIdsAsync(string family, CancellationToken ct = default)
            => throw new InvalidOperationException("the disbursement append path must not enumerate streams");
    }

    /// <summary>A rate-sheet store pricing the test (product, role) at a flat TAN across all principals.</summary>
    private sealed class FlatRateSheetStore(int tanBasisPoints) : IRateSheetStore
    {
        public Task InsertAsync(RateSheet sheet, CancellationToken ct = default) => Task.CompletedTask;

        public Task<RateSheet?> TryGetAsync(string rateSheetVersionId, CancellationToken ct = default)
            => Task.FromResult<RateSheet?>(null);

        public Task<RateSheetResolution?> ResolveAsync(string productFamily, DateTimeOffset asOf, CancellationToken ct = default)
        {
            var body = new RateSheetBody
            {
                Products = new Dictionary<string, Dictionary<string, RoleRates>>
                {
                    [ProductId] = new()
                    {
                        [Role] = new RoleRates { Bands = [new RateBand(0L, null, tanBasisPoints)] },
                    },
                },
            };
            return Task.FromResult<RateSheetResolution?>(new RateSheetResolution("rs-test-1", body));
        }
    }

    // The service touches the pack only for its VersionKey (stamped on the AppendContext), so a minimal
    // structurally-valid pack suffices — the disbursement reads no pack primitive at constitution.
    private static VerifiedPack MinimalPack() => new(
        Manifest: new PackManifest(
            PackId: "pt", PackVersion: "2026.1", Namespace: "pt", ManifestSchemaVersion: 1,
            Publisher: "test", PackEffectiveFrom: new DateOnly(2026, 1, 1), BasedOnPackVersion: null,
            DeltaSummary: "", BreakingChanges: [], EngineCompatibleVersions: "*",
            SchemaPins: new Dictionary<string, string>(), RateSheetRefNames: [], TestCorpusRef: ""),
        DayCounts: new Dictionary<string, PackDayCount>(),
        Withholdings: new Dictionary<string, PackWithholding>(),
        Fgds: new Dictionary<string, PackFgd>(),
        Reportings: new Dictionary<string, PackReporting>(),
        Parameters: new PackParameters(MaxConsumerRateBps: 0, AutoRenewalOptoutWindowDays: 0),
        RateSheetRefs: [],
        Families: []);
}
