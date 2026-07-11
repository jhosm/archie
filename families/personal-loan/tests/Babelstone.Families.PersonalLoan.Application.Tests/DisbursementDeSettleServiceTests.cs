using System.Runtime.CompilerServices;
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
/// MOVEMENT_APPEND_FIRST for the personal_loan money-moving paths (ADR-PC-032 §Decision slot 5 / commitment 1;
/// bd babelstone-5r9n.1 for disbursement, bd babelstone-t7o3.16 for the installment + early-repayment legs).
/// In plain English: every leg that moves money — the disbursement, an installment collection, an early
/// repayment — must record its money movement ON the event and append FIRST. It must NOT settle eagerly before
/// the append (the old settle-then-append window that could orphan a cash leg with no durable record). These
/// drive the real <see cref="PersonalLoanConstitutionService"/> against an in-memory store and assert (1) NO
/// eager settlement ever happens (the service has no eager settlement dependency — the old eager settlement
/// port was deleted in bd babelstone-t7o3.17), and (2) the
/// appended event carries the Originated <see cref="Movement"/> the substrate-owned settlement saga effects the
/// cash leg off. The double-move / settle-succeeds-append-fails idempotency is the substrate saga's
/// MOVEMENT_CASH_LEG_IDEMPOTENT (the WireMock-Core integration test on the settlement side); here we prove the
/// PRODUCER never opens that window — append-first closes it by construction (ADR-PC-031 §D3).
/// </summary>
public sealed class DisbursementDeSettleServiceTests
{
    private const string ProductId = "cp_pt_general_12m";
    private const string Role = "standard";
    private const string AccountRef = "acct-token-borrower";
    private const string CollectionRef = "acct-token-collection";
    private const string RepaymentRef = "acct-token-repayment";

    [Fact]
    public async Task DisburseAsync_appends_the_movement_bearing_event_and_NEVER_settles_eagerly()
    {
        var store = new InMemoryStore();
        var service = BuildService(store);

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

        // Append-first: exactly one LoanDisbursed was appended, carrying the Originated Credit Movement. The
        // service has no settlement port at all (the whole family is de-settled), so there is no eager settle
        // to make — MOVEMENT_APPEND_FIRST holds by construction.
        var disbursed = Assert.IsType<LoanDisbursed>(Assert.Single(store.AppendedEvents));
        var movement = Assert.Single(disbursed.Movements!);
        Assert.Equal(SettlementDirection.Credit, movement.Direction);   // the lump sum ENTERS the borrower's account
        Assert.Equal(AccountRef, movement.AccountRef);
        Assert.Equal(new Money(1_000_000), movement.Amount);
        Assert.Equal(MovementOperation.Disburse, movement.Operation);
        Assert.Equal(MovementOrigin.Originated, movement.Origin);       // → the gated settlement saga drives it
        Assert.Equal(commandId, movement.CommandId);

        // The event promotes the headers the settlement saga auto-starts on (the producer hop, t7o3.20):
        // a one-entry movementdirections list for this standalone Credit leg.
        Assert.Equal("Originated", disbursed.IntegrationHeaders![MovementHeaders.OriginKey]);
        Assert.Equal("Credit", disbursed.IntegrationHeaders[MovementHeaders.DirectionsKey]);
        // The disbursement CREDIT settles against the engine-owned CA, not the legacy demand core (ADR-PC-043).
        Assert.Equal(MovementHeaders.EngineCaValue, disbursed.IntegrationHeaders[MovementHeaders.SettlementTargetKey]);
    }

    [Fact]
    public async Task PayInstallmentAsync_appends_the_movement_bearing_event_and_NEVER_settles_eagerly()
    {
        // bd babelstone-t7o3.16: the installment-collection leg is migrated off the eager SettleAsync onto an
        // Originated Debit Movement against the named collection account (the installment LEAVES that account).
        var store = new InMemoryStore();
        var service = BuildService(store);
        var loanId = await DisburseAsync(service, principalCents: 1_000_000, termMonths: 12);

        await service.PayInstallmentAsync(new PayInstallmentCommand(
            LoanId: loanId,
            PaidAt: new DateTimeOffset(2026, 2, 1, 9, 0, 0, TimeSpan.Zero),
            CollectionAccountRef: CollectionRef,
            Actor: "test",
            CommandId: Guid.NewGuid()));

        // The LoanInstallmentPaid carries the Originated Debit collection Movement append-first; no eager settle.
        var paid = store.AppendedEvents.OfType<LoanInstallmentPaid>().Single();
        var movement = Assert.Single(paid.Movements!);
        Assert.Equal(SettlementDirection.Debit, movement.Direction);            // the installment LEAVES the account
        Assert.Equal(CollectionRef, movement.AccountRef);
        Assert.Equal(paid.Interest + paid.Capital, movement.Amount);            // the full installment is collected
        Assert.Equal(MovementOperation.CollectInstallment, movement.Operation);
        Assert.Equal(MovementOrigin.Originated, movement.Origin);

        Assert.Equal("Originated", paid.IntegrationHeaders![MovementHeaders.OriginKey]);
        Assert.Equal("Debit", paid.IntegrationHeaders[MovementHeaders.DirectionsKey]);
        // The installment DEBIT settles against the engine-owned CA, not the legacy demand core (ADR-PC-043).
        Assert.Equal(MovementHeaders.EngineCaValue, paid.IntegrationHeaders[MovementHeaders.SettlementTargetKey]);
    }

    [Fact]
    public async Task RepayEarlyAsync_appends_the_movement_bearing_event_and_NEVER_settles_eagerly()
    {
        // bd babelstone-t7o3.16: the early-repayment leg is migrated off the eager SettleAsync onto an
        // Originated Debit Movement against the named repayment account (capital + capped commission LEAVES it).
        var store = new InMemoryStore();
        var service = BuildService(store);
        var loanId = await DisburseAsync(service, principalCents: 1_000_000, termMonths: 36);

        var commandId = Guid.NewGuid();
        await service.RepayEarlyAsync(new RepayEarlyCommand(
            LoanId: loanId,
            RepaymentAmountCents: 1_000_000, // full repayment
            RepaidAt: new DateTimeOffset(2026, 6, 1, 9, 0, 0, TimeSpan.Zero),
            RepaymentAccountRef: RepaymentRef,
            Actor: "test",
            CommandId: commandId));

        var repaid = store.AppendedEvents.OfType<LoanRepaidEarly>().Single();
        var movement = Assert.Single(repaid.Movements!);
        Assert.Equal(SettlementDirection.Debit, movement.Direction);            // capital + commission LEAVES the account
        Assert.Equal(RepaymentRef, movement.AccountRef);
        Assert.Equal(repaid.CapitalRepaid + repaid.Commission, movement.Amount);
        Assert.Equal(MovementOperation.RepayEarly, movement.Operation);
        Assert.Equal(MovementOrigin.Originated, movement.Origin);
        Assert.Equal(commandId, movement.CommandId);

        // A full repayment settles the loan (the balance reaches zero) — the closing LoanSettled is appended too.
        Assert.Contains(store.AppendedEvents, e => e is LoanSettled);

        Assert.Equal("Originated", repaid.IntegrationHeaders![MovementHeaders.OriginKey]);
        Assert.Equal("Debit", repaid.IntegrationHeaders[MovementHeaders.DirectionsKey]);
        // The early-repayment DEBIT settles against the engine-owned CA, not the legacy demand core (ADR-PC-043).
        Assert.Equal(MovementHeaders.EngineCaValue, repaid.IntegrationHeaders[MovementHeaders.SettlementTargetKey]);
    }

    [Fact]
    public async Task A_precondition_refusal_neither_settles_nor_appends_a_disbursement()
    {
        // The eligibility gate refuses BEFORE the disbursement (ADR-PC-024 §5): no loan opens, so there is
        // nothing to settle and no LoanDisbursed (a refusal event is appended, never a disbursement).
        var store = new InMemoryStore();
        var service = BuildService(store, requiredPreconditions: ["solvency_assessed"]);

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

        Assert.IsType<LoanDisbursementFailed>(Assert.Single(store.AppendedEvents));
    }

    // ---- In-memory harness (Docker-free): one store backs BOTH the load and append paths, so the
    //      load-then-append lifecycle steps (installment, early-repay) can rehydrate the disbursed loan. ----

    private static async Task<Guid> DisburseAsync(
        PersonalLoanConstitutionService service, long principalCents, int termMonths)
    {
        var loanId = Guid.NewGuid();
        await service.DisburseAsync(new DisburseLoanCommand(
            LoanId: loanId,
            PrincipalCents: principalCents,
            ProductId: ProductId,
            Role: Role,
            TermMonths: termMonths,
            StartDate: new DateOnly(2026, 1, 1),
            DisbursedAt: new DateTimeOffset(2026, 1, 1, 9, 0, 0, TimeSpan.Zero),
            Purpose: "general",
            DisbursementAccountRef: AccountRef,
            Actor: "test",
            CommandId: Guid.NewGuid()));
        return loanId;
    }

    private static PersonalLoanConstitutionService BuildService(
        InMemoryStore store, IReadOnlyCollection<string>? requiredPreconditions = null)
    {
        var runtime = new AggregateRuntime<LoanPosition>(
            store: store,
            sink: store,
            handlers: PersonalLoanFamilyModule.Registry(),
            serializer: new JsonEventSerializer(),
            protector: new NullPiiProtector(),
            clock: TimeProvider.System,
            seedState: () => LoanPosition.Empty);

        return new PersonalLoanConstitutionService(
            runtime, new FlatRateSheetStore(tanBasisPoints: 600), MinimalPack(), requiredPreconditions);
    }

    /// <summary>A plain JSON codec standing in for the Avro codec (the same idiom the term-deposit tests
    /// use): SchemaId is a constant 1. The runtime is wired with this, so the appended payloads are JSON the
    /// store decodes back.</summary>
    private sealed class JsonEventSerializer : IEventSerializer
    {
        public EncodedPayload Encode(DomainEvent @event)
            => new(JsonSerializer.SerializeToUtf8Bytes(@event, @event.GetType()), SchemaId: 1);

        public DomainEvent Decode(ReadOnlyMemory<byte> payload, Type payloadType)
            => (DomainEvent)JsonSerializer.Deserialize(payload.Span, payloadType)!;
    }

    /// <summary>
    /// A single in-memory event store that is BOTH the <see cref="IEventStore"/> the runtime loads from and
    /// the <see cref="IEventSink"/> it appends through, so the load-then-append lifecycle steps can rehydrate
    /// the loan the disbursement opened. Records the decoded appended events so the test can assert exactly
    /// what was appended (and that the Movement-bearing events carry the right Movement).
    /// </summary>
    private sealed class InMemoryStore : IEventStore, IEventSink
    {
        private readonly Dictionary<Guid, List<EventEnvelope>> _streams = [];
        private readonly List<DomainEvent> _appended = [];
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
                _appended.Add(Serializer.Decode(envelope.Payload, ResolveType(envelope.EventType)));
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

        private static Type ResolveType(string eventType) => eventType switch
        {
            "personal_loan.LoanDisbursed" => typeof(LoanDisbursed),
            "personal_loan.LoanDisbursementFailed" => typeof(LoanDisbursementFailed),
            "personal_loan.LoanInstallmentPaid" => typeof(LoanInstallmentPaid),
            "personal_loan.LoanRepaidEarly" => typeof(LoanRepaidEarly),
            "personal_loan.LoanSettled" => typeof(LoanSettled),
            "personal_loan.LoanWrittenOff" => typeof(LoanWrittenOff),
            _ => throw new InvalidOperationException($"unexpected appended event type '{eventType}'"),
        };
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
            SchemaPins: new Dictionary<string, string>(), RateSheetRefNames: [], TemplateRefNames: [], TestCorpusRef: ""),
        DayCounts: new Dictionary<string, PackDayCount>(),
        Withholdings: new Dictionary<string, PackWithholding>(),
        Fgds: new Dictionary<string, PackFgd>(),
        Reportings: new Dictionary<string, PackReporting>(),
        Parameters: new PackParameters(MaxConsumerRateBps: 0, AutoRenewalOptoutWindowDays: 0),
        RateSheetRefs: [],
        Families: []);
}
