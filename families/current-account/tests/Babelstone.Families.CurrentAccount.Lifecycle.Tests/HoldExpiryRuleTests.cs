using Babelstone.Engine;
using Babelstone.Engine.Hosting;
using Babelstone.EventStore;
using Babelstone.Families.CurrentAccount.Lifecycle;
using Babelstone.Lifecycle;
using Xunit;

namespace Babelstone.Families.CurrentAccount.Lifecycle.Tests;

/// <summary>
/// Tests for <see cref="HoldExpiryRule"/> — the current_account family's projection-derived hold-expiry rule
/// (ADR-PC-037; ADR-PC-036). They drive the rule over a fake spine hold store (no live
/// engine / no DB), behind a real <see cref="AccountBalanceReader"/>, and assert the expiry-specific
/// decisions:
/// <list type="bullet">
/// <item>an ACTIVE authorization hold at/after its value-date horizon is expired EXACTLY ONCE (the dispatch
/// ledger absorbs the re-tick), under the canonical server-derived key the engine /expire endpoint also
/// derives — <c>("expire_hold", placed_sequence)</c>;</item>
/// <item>the rule expires a hold due TODAY but NEVER one due tomorrow — the value-date-horizon boundary is
/// inclusive of today (ADR-PC-023);</item>
/// <item>a backfilled (past) value-date is re-derived under the SAME number-pinned id, carrying its OWN
/// value-date as the business valid_time so a late firing records the correct date;</item>
/// <item>each of several holds on ONE account is its OWN occurrence (keyed on the placing sequence, NOT a
/// one-shot constant) — the key difference from maturity;</item>
/// <item>a non-ACTIVE (captured/expired) hold is never a candidate — the read is state-precise.</item>
/// </list>
/// All Docker-free, no clock: the horizon is the <c>asOf</c> input.
/// </summary>
public sealed class HoldExpiryRuleTests
{
    private static readonly DateOnly Today = new(2026, 6, 28);

    // The stable command-kind the ENGINE /v1/accounts/{id}/holds/{holdId}/expire endpoint dedupes under. The
    // driver must converge on it, so the key cross-checks use this literal rather than the rule's own constant
    // (which would make the assertion circular).
    private const string EngineExpireKind = "expire_hold";

    [Fact]
    public async Task An_authorization_hold_at_or_before_the_horizon_is_expired_exactly_once()
    {
        var accountId = Guid.NewGuid();
        const long placedSequence = 7;
        var sink = new RecordingSink();
        var pass = NewPass(sink, new HoldExpiryRule(Reader(Hold(accountId, "hold-1", placedSequence, Today))));

        var first = await pass.RunOnceAsync(Today);
        var second = await pass.RunOnceAsync(Today);

        Assert.Single(first);
        Assert.Empty(second);

        // POSTed once, under the canonical server-derived key the engine also derives — ("expire_hold", 7).
        Assert.Single(sink.Dispatched);
        Assert.Equal("expire_hold", first[0].CommandKind);
        Assert.Equal(placedSequence, first[0].OccurrenceKey);
        Assert.Equal(accountId, first[0].InstanceId);
        Assert.Equal(LifecycleCommandKey.Derive(accountId, EngineExpireKind, placedSequence), first[0].CommandId);
    }

    [Fact]
    public async Task The_rule_expires_a_hold_due_today_but_never_one_due_tomorrow()
    {
        var dueToday = Guid.NewGuid();
        var dueTomorrow = Guid.NewGuid();
        var rule = new HoldExpiryRule(Reader(
            Hold(dueToday, "hold-today", 1, Today),
            Hold(dueTomorrow, "hold-tomorrow", 2, Today.AddDays(1))));

        var decision = Assert.Single(await rule.EvaluateAsync(Today));

        // Today's value-date expires (the horizon is inclusive of today); tomorrow's is excluded — a hold
        // expires on/after its value-date and never before (ADR-PC-023 projection-derived, never a clock read).
        Assert.Equal(dueToday, decision.InstanceId);
        Assert.Equal($"/v1/accounts/{dueToday:D}/holds/hold-today/expire", decision.RequestPath);
        // Expiry is NOT a money-mover: a HoldExpired releases the earmark with no posting (ADR-PC-037),
        // so — unlike maturity — the decision carries NO scoped SCA service principal.
        Assert.Null(decision.ServicePrincipalScope);
    }

    [Fact]
    public async Task A_backfilled_past_value_date_is_rederived_under_the_same_number_pinned_key()
    {
        var accountId = Guid.NewGuid();
        var pastValueDate = Today.AddDays(-10); // value-date passed while the driver was down
        const long placedSequence = 3;
        var rule = new HoldExpiryRule(Reader(Hold(accountId, "hold-1", placedSequence, pastValueDate)));

        var decision = Assert.Single(await rule.EvaluateAsync(Today));

        // The occurrence key is the placing sequence, so a backfill re-derives the SAME id a first firing
        // would — the engine's command_dedup swallows the repeat (number-pinned per hold).
        Assert.Equal(placedSequence, decision.OccurrenceKey);
        Assert.Equal(LifecycleCommandKey.Derive(accountId, EngineExpireKind, placedSequence),
            LifecycleDispatchId.Of(decision));

        // The hold's OWN (past) value-date rides as the business valid_time, not today, so a late firing
        // records the correct economic date (ADR-PC-002 / ADR-PC-023).
        Assert.Equal(pastValueDate, decision.DueAt);
        Assert.Equal(pastValueDate, Assert.IsType<DateOnly>(decision.Body["value_date"]));
    }

    [Fact]
    public async Task Each_of_several_holds_on_one_account_is_its_own_occurrence()
    {
        var accountId = Guid.NewGuid();
        var sink = new RecordingSink();
        var pass = NewPass(sink, new HoldExpiryRule(Reader(
            Hold(accountId, "hold-a", 5, Today),
            Hold(accountId, "hold-b", 9, Today))));

        var fired = await pass.RunOnceAsync(Today);

        // Two holds on the SAME account, keyed on their distinct placing sequences — two independent expiries,
        // NOT one deduped occurrence (ADR-PC-036: a hold-expiry occurrence is per-hold, unlike a
        // deposit maturity's one-shot constant). This is the load-bearing reason the occurrence key is the
        // placed sequence, not a constant 1.
        Assert.Equal(2, fired.Count);
        Assert.Equal([5L, 9L], fired.Select(f => f.OccurrenceKey).OrderBy(k => k).ToArray());
        Assert.Equal(2, fired.Select(f => f.CommandId).Distinct().Count());
    }

    [Fact]
    public async Task A_non_active_hold_is_never_an_expiry_candidate()
    {
        // A CAPTURED/EXPIRED hold has already left the active set — the expiry read filters state='ACTIVE',
        // so the rule never re-expires a released hold (the engine's dedup is the backstop regardless).
        var rule = new HoldExpiryRule(Reader(Hold(Guid.NewGuid(), "hold-captured", 1, Today, state: "CAPTURED")));

        Assert.Empty(await rule.EvaluateAsync(Today));
    }

    // --- helpers ---

    private static LifecycleSchedulePass NewPass(ILifecycleCommandSink sink, params ILifecycleCommandRule[] rules) =>
        new(rules, new InMemoryLifecycleDispatchLedger(), sink);

    private static AccountBalanceReader Reader(params AccountHoldRow[] rows) =>
        new(new ThrowingMovementLedgerStore(), new FakeAccountHoldStore(rows));

    private static AccountHoldRow Hold(
        Guid accountId, string holdId, long placedSequence, DateOnly valueDate, string state = "ACTIVE") =>
        new(
            HoldId: holdId,
            AccountRef: accountId.ToString(),
            AmountCents: 5_000,
            ValueDate: valueDate,
            State: state,
            PlacedStreamId: accountId,
            PlacedSequence: placedSequence);

    /// <summary>A fake hold store that honours the ACTIVE + AUTHORIZATION + <c>value_date &lt;= horizon</c>
    /// semantics of the real expiry read (so the horizon boundary is genuinely exercised), throwing for every
    /// read the rule never makes.</summary>
    private sealed class FakeAccountHoldStore(params AccountHoldRow[] rows) : IAccountHoldStore
    {
        public Task<IReadOnlyList<AccountHoldRow>> GetActiveHoldsWithValueDateAtOrBeforeAsync(
            DateOnly valueDateHorizon, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<AccountHoldRow>>(
                rows.Where(r => r.State == "ACTIVE"
                                && r.Kind == "AUTHORIZATION"
                                && r.ValueDate is { } vd && vd <= valueDateHorizon)
                    .OrderBy(r => r.AccountRef, StringComparer.Ordinal)
                    .ThenBy(r => r.HoldId, StringComparer.Ordinal)
                    .ToList());

        public Task PlaceAsync(AccountHoldRow hold, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task PlaceLegalAsync(AccountHoldRow legalHold, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<HoldReleaseResult> ReleaseLegalAsync(
            string holdId, Guid releasedStreamId, long releasedSequence, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<HoldReleaseResult> CaptureAsync(
            string holdId, long capturedAmountCents, Guid releasedStreamId, long releasedSequence,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<HoldReleaseResult> ExpireAsync(
            string holdId, Guid releasedStreamId, long releasedSequence, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<long> GetActiveHoldCentsAsync(string accountRef, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<AccountHoldRow>> GetActiveHoldsAsync(
            string accountRef, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<AccountHoldRow>> GetActiveLegalHoldsWithExpiryAtOrBeforeAsync(
            DateOnly expiryHorizon, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<long> GetWindowedAuthorizationHoldCentsAsync(
            string accountRef, DateOnly fromInclusive, DateOnly toInclusive, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task TruncateAsync(CancellationToken ct = default) => throw new NotSupportedException();
    }

    /// <summary>The movement ledger AccountBalanceReader's ctor requires; the expiry read never touches it, so
    /// every member fails loud if the read path ever regresses into reading movements.</summary>
    private sealed class ThrowingMovementLedgerStore : IMovementLedgerStore
    {
        public Task AppendAsync(IReadOnlyList<MovementLedgerEntry> entries, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<long> GetBalanceCentsAsync(string accountRef, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<MovementLedgerEntry>> GetStatementAsync(
            string accountRef, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task TruncateAsync(CancellationToken ct = default) => throw new NotSupportedException();
    }

    /// <summary>Records every command the pass POSTs through it (the decision + the derived command id).</summary>
    private sealed class RecordingSink : ILifecycleCommandSink
    {
        private readonly List<(LifecycleCommandDecision Decision, Guid CommandId)> _dispatched = [];

        public IReadOnlyList<(LifecycleCommandDecision Decision, Guid CommandId)> Dispatched => _dispatched;

        public Task DispatchAsync(LifecycleCommandDecision decision, Guid commandId, CancellationToken ct = default)
        {
            _dispatched.Add((decision, commandId));
            return Task.CompletedTask;
        }
    }
}
