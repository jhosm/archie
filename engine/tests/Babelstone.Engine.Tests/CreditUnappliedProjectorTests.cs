using Babelstone.EventStore;
using Babelstone.FinancialTypes;
using Xunit;

namespace Babelstone.Engine.Tests;

/// <summary>
/// Tests for the spine-owned <see cref="CreditUnappliedProjector"/> — the undeliverable-credit (IOU /
/// escheat) ledger fold (ADR-PC-043 slot 5). In plain English: these prove that replaying the
/// <c>operations.CreditUnapplied</c> / <c>operations.CreditReapplied</c> stream materialises the right
/// set of OUTSTANDING IOUs — the credits recorded unapplied MINUS the ones resolved by a matching
/// <c>CreditReapplied</c> on the derived resolution key — that an IOU leaves the outstanding set once
/// its resolution lands, that <c>intent_id</c> makes the lifecycle idempotent (a re-delivered unapply
/// or a duplicate resolution folds at most once — never a double-pay), that a no-op resolution is
/// SURFACED rather than silently absorbed, that the resolution key must be derived from the original
/// intent (the double-pay guard), and that replaying the same sequence reproduces the same set (replay
/// determinism — no clock anywhere in the fold).
/// </summary>
public sealed class CreditUnappliedProjectorTests
{
    private static readonly DateOnly UnappliedDate = new(2026, 6, 25);

    // The resolution key is g(IntentId) — derived from the original intent, never fresh (the ADR-PC-043
    // double-pay guard). The engine spine does not depend on the orchestrator's
    // SettlementReferences.DeriveResolutionIntentId helper, so the test derives the key structurally
    // (a prefix of the original intent) exactly as the projector's guard verifies it.
    private static string ResolutionKeyFor(string intentId) => "RESOLVE-" + intentId;

    private static CreditUnapplied Unapplied(
        string intentId, string beneficiary = "acct-1", long cents = 5_000,
        string reason = "BENEFICIARY_ACCOUNT_CLOSED") =>
        new(intentId, beneficiary, new Money(cents), reason, UnappliedDate);

    private static CreditReapplied Reapplied(
        string originalIntentId, string beneficiary = "acct-1", long cents = 5_000) =>
        new(ResolutionKeyFor(originalIntentId), originalIntentId, beneficiary, new Money(cents),
            UnappliedDate.AddDays(10));

    [Fact]
    public async Task An_unapplied_credit_is_outstanding_and_attributed()
    {
        var store = new InMemoryIouLedgerStore();
        var projector = new CreditUnappliedProjector(store);
        var stream = Guid.NewGuid();

        await projector.ApplyAsync(stream, 0, Unapplied("INTENT-a", beneficiary: "acct-1", cents: 5_000));

        var iou = Assert.Single(await store.GetOutstandingAsync());
        Assert.Equal("INTENT-a", iou.IntentId);
        Assert.Equal("acct-1", iou.BeneficiaryRef);
        Assert.Equal(5_000, iou.AmountCents);
        Assert.Equal("BENEFICIARY_ACCOUNT_CLOSED", iou.Reason);
        Assert.Equal(UnappliedDate, iou.UnappliedAt);
        Assert.Equal("OUTSTANDING", iou.State);
    }

    [Fact]
    public async Task A_reapplied_credit_leaves_the_outstanding_set()
    {
        var store = new InMemoryIouLedgerStore();
        var projector = new CreditUnappliedProjector(store);
        var stream = Guid.NewGuid();

        // The acceptance criterion: an IOU leaves the OUTSTANDING set once its CreditReapplied lands.
        await projector.ApplyAsync(stream, 0, Unapplied("INTENT-a"));
        Assert.Single(await store.GetOutstandingAsync());

        await projector.ApplyAsync(stream, 1, Reapplied("INTENT-a"));

        Assert.Empty(await store.GetOutstandingAsync());
        var row = store.Row("INTENT-a");
        Assert.Equal("RESOLVED", row.State);
        Assert.Equal(ResolutionKeyFor("INTENT-a"), row.ResolutionIntentId);
        Assert.Equal(UnappliedDate.AddDays(10), row.ReappliedAt);
    }

    [Fact]
    public async Task Replaying_the_operations_stream_materialises_the_correct_outstanding_set()
    {
        // The core acceptance test (ADR-PC-043 slot 5): the OUTSTANDING set is exactly the credits
        // recorded unapplied MINUS the ones matched by a CreditReapplied on the derived resolution key.
        // Three IOUs open; the middle one is resolved; the outstanding set is the other two.
        var streamA = Guid.NewGuid();
        var streamB = Guid.NewGuid();
        var streamC = Guid.NewGuid();
        var events = new (Guid Stream, long Seq, DomainEvent Event)[]
        {
            (streamA, 0, Unapplied("INTENT-a", beneficiary: "acct-a", cents: 1_000)),
            (streamB, 0, Unapplied("INTENT-b", beneficiary: "acct-b", cents: 2_000)),
            (streamC, 0, Unapplied("INTENT-c", beneficiary: "acct-c", cents: 3_000)),
            (streamB, 1, Reapplied("INTENT-b", beneficiary: "acct-b", cents: 2_000)),
        };

        var store = new InMemoryIouLedgerStore();
        var projector = new CreditUnappliedProjector(store);
        foreach (var (stream, seq, @event) in events)
        {
            await projector.ApplyAsync(stream, seq, @event);
        }

        var outstanding = await store.GetOutstandingAsync();
        Assert.Equal(["INTENT-a", "INTENT-c"], outstanding.Select(o => o.IntentId).OrderBy(id => id));
        Assert.DoesNotContain(outstanding, o => o.IntentId == "INTENT-b"); // resolved — no longer owed
    }

    [Fact]
    public async Task Replaying_the_same_sequence_reproduces_the_same_outstanding_set()
    {
        // Deterministic, replay-rebuildable: the fold is a function of the event sequence — no clock,
        // no randomness — so folding the SAME sequence into a fresh (rebuilt) store reproduces the same
        // rows. This is the unit half of the replay gate; the Postgres truncate-then-refold half rides
        // the integration suite.
        var streamA = Guid.NewGuid();
        var streamB = Guid.NewGuid();
        var events = new (Guid Stream, long Seq, DomainEvent Event)[]
        {
            (streamA, 0, Unapplied("INTENT-a", cents: 1_000)),
            (streamB, 0, Unapplied("INTENT-b", cents: 2_000)),
            (streamA, 1, Reapplied("INTENT-a", cents: 1_000)),
        };

        var first = new InMemoryIouLedgerStore();
        var second = new InMemoryIouLedgerStore();
        foreach (var store in new[] { first, second })
        {
            var projector = new CreditUnappliedProjector(store);
            foreach (var (stream, seq, @event) in events)
            {
                await projector.ApplyAsync(stream, seq, @event);
            }
        }

        Assert.Equal(first.AllRows(), second.AllRows());
    }

    [Fact]
    public async Task Reset_for_rebuild_then_refold_reproduces_the_outstanding_set()
    {
        var store = new InMemoryIouLedgerStore();
        var projector = new CreditUnappliedProjector(store);
        var stream = Guid.NewGuid();

        async Task FoldAll()
        {
            await projector.ApplyAsync(stream, 0, Unapplied("INTENT-a", cents: 1_000));
            await projector.ApplyAsync(stream, 1, Unapplied("INTENT-b", cents: 2_000));
            await projector.ApplyAsync(stream, 2, Reapplied("INTENT-a", cents: 1_000));
        }

        await FoldAll();
        var before = (await store.GetOutstandingAsync()).Select(o => o.IntentId).ToList();

        await projector.ResetForRebuildAsync();
        await FoldAll();
        var after = (await store.GetOutstandingAsync()).Select(o => o.IntentId).ToList();

        Assert.Equal(["INTENT-b"], before); // only INTENT-b remains owed
        Assert.Equal(before, after);        // truncate-then-refold reproduces it identically
    }

    [Fact]
    public async Task A_redelivered_unapplied_never_records_the_iou_twice()
    {
        var store = new InMemoryIouLedgerStore();
        var projector = new CreditUnappliedProjector(store);
        var stream = Guid.NewGuid();
        var unapplied = Unapplied("INTENT-a", cents: 5_000);

        // The at-least-once drive may re-deliver after a crash between apply and checkpoint; the
        // intent_id key (ADR-PC-043) makes the re-apply a no-op.
        await projector.ApplyAsync(stream, 0, unapplied);
        await projector.ApplyAsync(stream, 0, unapplied);

        Assert.Single(await store.GetOutstandingAsync());
    }

    [Fact]
    public async Task A_second_resolution_is_a_no_op_and_is_surfaced_as_already_resolved()
    {
        var store = new InMemoryIouLedgerStore();
        var anomalies = new List<CreditResolutionAnomaly>();
        var projector = new CreditUnappliedProjector(store, anomalies.Add);
        var stream = Guid.NewGuid();

        await projector.ApplyAsync(stream, 0, Unapplied("INTENT-a"));
        await projector.ApplyAsync(stream, 1, Reapplied("INTENT-a"));
        await projector.ApplyAsync(stream, 2, Reapplied("INTENT-a"));

        // The first resolution's fold stands; the duplicate transitioned zero rows — a no-op, never a
        // double-pay — and was SURFACED as the reconciliation signal ADR-PC-043 requires.
        Assert.Equal("RESOLVED", store.Row("INTENT-a").State);
        var anomaly = Assert.Single(anomalies);
        Assert.Equal(CreditResolutionResult.AlreadyResolved, anomaly.Kind);
        Assert.Equal("INTENT-a", anomaly.IntentId);
        Assert.Equal(stream, anomaly.ResolvingStreamId);
        Assert.Equal(2, anomaly.ResolvingSequence);
    }

    [Fact]
    public async Task A_resolution_folded_before_its_open_records_a_tombstone_and_the_late_open_never_reopens()
    {
        var store = new InMemoryIouLedgerStore();
        var anomalies = new List<CreditResolutionAnomaly>();
        var projector = new CreditUnappliedProjector(store, anomalies.Add);
        var resolveStream = Guid.NewGuid();
        var openStream = Guid.NewGuid();

        // The commutative-fold core: a resolution folded BEFORE its open (the drainer folds streams
        // unordered, and the credit events do not share a guaranteed stream) records a RESOLVED
        // tombstone — NOT a surfaced no-op — and a later CreditUnapplied for that intent no-ops on the
        // conflict rather than re-opening it. So the intent ends RESOLVED, never stuck OUTSTANDING.
        await projector.ApplyAsync(resolveStream, 0, Reapplied("INTENT-a"));
        Assert.Empty(await store.GetOutstandingAsync());     // the tombstone is not an OUTSTANDING IOU
        Assert.Equal("RESOLVED", store.Row("INTENT-a").State);
        Assert.Empty(anomalies);                             // resolve-before-open is Transitioned, not surfaced

        await projector.ApplyAsync(openStream, 0, Unapplied("INTENT-a"));
        Assert.Empty(await store.GetOutstandingAsync());     // the late open did NOT re-open it
        Assert.Equal("RESOLVED", store.Row("INTENT-a").State);
    }

    [Fact]
    public async Task Folding_open_and_resolve_in_either_order_yields_the_identical_outstanding_set()
    {
        // THE rebuild-determinism guard (ADR-PC-043 slot 3): the open and the resolve for one intent may
        // ride DIFFERENT streams (the events are intent-keyed, not InstanceId-keyed, and a resolution can
        // re-target a different account), and the SpineProjectionDrainer folds streams in UNORDERED
        // sequence — so a full truncate-then-refold can present the two events in EITHER order. The
        // OUTSTANDING set MUST be identical either way (before this fix, resolve-first wrote nothing and
        // the late open left the intent stuck OUTSTANDING forever — an incremental/rebuild divergence).
        var openStream = Guid.NewGuid();
        var resolveStream = Guid.NewGuid();
        var open = Unapplied("INTENT-a", beneficiary: "acct-a", cents: 4_200);
        var resolve = Reapplied("INTENT-a", beneficiary: "acct-a", cents: 4_200);

        var openFirst = new InMemoryIouLedgerStore();
        var openFirstProjector = new CreditUnappliedProjector(openFirst);
        await openFirstProjector.ApplyAsync(openStream, 0, open);
        await openFirstProjector.ApplyAsync(resolveStream, 0, resolve);

        var resolveFirst = new InMemoryIouLedgerStore();
        var resolveFirstProjector = new CreditUnappliedProjector(resolveFirst);
        await resolveFirstProjector.ApplyAsync(resolveStream, 0, resolve);
        await resolveFirstProjector.ApplyAsync(openStream, 0, open);

        var openFirstOutstanding = (await openFirst.GetOutstandingAsync()).Select(o => o.IntentId).ToList();
        var resolveFirstOutstanding = (await resolveFirst.GetOutstandingAsync()).Select(o => o.IntentId).ToList();

        Assert.Empty(openFirstOutstanding);                       // resolved either way — nothing owed
        Assert.Equal(openFirstOutstanding, resolveFirstOutstanding); // the two orders AGREE (the guard)
    }

    [Fact]
    public async Task A_clean_lifecycle_surfaces_no_anomaly()
    {
        var store = new InMemoryIouLedgerStore();
        var anomalies = new List<CreditResolutionAnomaly>();
        var projector = new CreditUnappliedProjector(store, anomalies.Add);
        var stream = Guid.NewGuid();

        await projector.ApplyAsync(stream, 0, Unapplied("INTENT-a"));
        await projector.ApplyAsync(stream, 1, Reapplied("INTENT-a"));

        Assert.Empty(anomalies);
    }

    [Fact]
    public async Task A_resolution_with_a_non_derived_key_is_refused()
    {
        var store = new InMemoryIouLedgerStore();
        var projector = new CreditUnappliedProjector(store);
        var stream = Guid.NewGuid();

        await projector.ApplyAsync(stream, 0, Unapplied("INTENT-a"));

        // The double-pay guard (ADR-PC-043): a resolution key that is NOT derived from the original
        // intent (a freshly minted value) breaks the structural collapse of a late original apply and
        // the resolution to one landing, so it is refused loud rather than folded as safely resolved.
        var forged = new CreditReapplied(
            ResolutionIntentId: "RESOLVE-INTENT-different",
            OriginalIntentId: "INTENT-a",
            BeneficiaryAccountRef: "acct-1",
            Amount: new Money(5_000),
            ReappliedAt: UnappliedDate.AddDays(3));

        await Assert.ThrowsAsync<InvalidOperationException>(() => projector.ApplyAsync(stream, 1, forged));
        Assert.Single(await store.GetOutstandingAsync()); // still owed — nothing was resolved
    }

    [Fact]
    public async Task A_non_credit_event_contributes_nothing()
    {
        var store = new InMemoryIouLedgerStore();
        var projector = new CreditUnappliedProjector(store);

        await projector.ApplyAsync(Guid.NewGuid(), 0, new TestUnrelatedCredit("no credit here"));

        Assert.Empty(await store.GetOutstandingAsync());
    }

    // A family-agnostic, test-only event the projector must ignore (kept local so Engine.Tests stays
    // family-agnostic).
    private sealed record TestUnrelatedCredit(string Note) : DomainEvent;

    /// <summary>
    /// An in-memory <see cref="IIouLedgerStore"/> test double mirroring the COMMUTATIVE
    /// <see cref="PostgresIouLedgerStore"/> contract: record idempotent on <c>intent_id</c> (a re-open
    /// against a resolution tombstone no-ops), resolution transitioning an OUTSTANDING row OR recording
    /// a RESOLVED tombstone when the open has not been folded yet, a duplicate resolution surfaced as
    /// <see cref="CreditResolutionResult.AlreadyResolved"/>, and truncate for rebuild. Kept in the test
    /// project (the same convention as the other in-memory storage doubles).
    /// </summary>
    private sealed class InMemoryIouLedgerStore : IIouLedgerStore
    {
        private readonly Dictionary<string, UndeliverableCreditRow> _rows = new(StringComparer.Ordinal);

        public UndeliverableCreditRow Row(string intentId) => _rows[intentId];

        public bool Has(string intentId) => _rows.ContainsKey(intentId);

        public IReadOnlyList<UndeliverableCreditRow> AllRows() =>
            _rows.Values.OrderBy(r => r.IntentId, StringComparer.Ordinal).ToList();

        public Task RecordUnappliedAsync(UndeliverableCreditRow iou, CancellationToken ct = default)
        {
            _rows.TryAdd(iou.IntentId, iou); // ON CONFLICT (intent_id) DO NOTHING — a tombstone stays RESOLVED
            return Task.CompletedTask;
        }

        public Task<CreditResolutionResult> ResolveAsync(
            string originalIntentId, string resolutionIntentId, string reappliedRef, long reappliedAmountCents,
            DateOnly reappliedAt, Guid resolvedStreamId, long resolvedSequence, CancellationToken ct = default)
        {
            // Commutative fold: UPDATE an OUTSTANDING row if the open was folded, else record a RESOLVED
            // tombstone (resolve-before-open). Both are Transitioned; only a duplicate resolution of an
            // already-RESOLVED intent is the AlreadyResolved no-op (mirrors the real store's UPDATE-then
            // -tombstone-INSERT-ON-CONFLICT sequence).
            if (_rows.TryGetValue(originalIntentId, out var row))
            {
                if (row.State != "OUTSTANDING")
                {
                    return Task.FromResult(CreditResolutionResult.AlreadyResolved);
                }

                _rows[originalIntentId] = row with
                {
                    State = "RESOLVED",
                    ResolutionIntentId = resolutionIntentId,
                    ReappliedRef = reappliedRef,
                    ReappliedAmountCents = reappliedAmountCents,
                    ReappliedAt = reappliedAt,
                    ResolvedStreamId = resolvedStreamId,
                    ResolvedSequence = resolvedSequence,
                };
                return Task.FromResult(CreditResolutionResult.Transitioned);
            }

            // Resolve-before-open: a RESOLVED tombstone keyed by intent_id, carrying NO open facts (the
            // real table's nullable unapplied_* columns; the sentinels below are never read because a
            // RESOLVED row is filtered out of every OUTSTANDING read).
            _rows[originalIntentId] = new UndeliverableCreditRow(
                IntentId: originalIntentId,
                BeneficiaryRef: string.Empty,
                AmountCents: 0,
                Reason: string.Empty,
                UnappliedAt: default,
                State: "RESOLVED",
                UnappliedStreamId: Guid.Empty,
                UnappliedSequence: 0,
                ResolutionIntentId: resolutionIntentId,
                ReappliedRef: reappliedRef,
                ReappliedAmountCents: reappliedAmountCents,
                ReappliedAt: reappliedAt,
                ResolvedStreamId: resolvedStreamId,
                ResolvedSequence: resolvedSequence);
            return Task.FromResult(CreditResolutionResult.Transitioned);
        }

        public Task<IReadOnlyList<UndeliverableCreditRow>> GetOutstandingAsync(CancellationToken ct = default)
        {
            IReadOnlyList<UndeliverableCreditRow> rows = _rows.Values
                .Where(r => r.State == "OUTSTANDING")
                .OrderBy(r => r.UnappliedAt)
                .ThenBy(r => r.IntentId, StringComparer.Ordinal)
                .ToList();
            return Task.FromResult(rows);
        }

        public Task TruncateAsync(CancellationToken ct = default)
        {
            _rows.Clear();
            return Task.CompletedTask;
        }
    }
}
