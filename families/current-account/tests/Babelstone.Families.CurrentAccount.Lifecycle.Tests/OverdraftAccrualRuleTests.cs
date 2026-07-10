using Babelstone.Engine;
using Babelstone.Engine.Hosting;
using Babelstone.EventStore;
using Babelstone.Families.CurrentAccount.Lifecycle;
using Babelstone.Lifecycle;
using Xunit;

namespace Babelstone.Families.CurrentAccount.Lifecycle.Tests;

/// <summary>
/// Tests for <see cref="OverdraftAccrualRule"/> — the current_account family's projection-derived
/// overdraft-interest accrual rule (ADR-PC-037 §D5; ADR-PC-036). They drive the rule over a fake spine
/// movement store (no live engine / no DB), behind a real <see cref="AccountBalanceReader"/>, and assert the
/// accrual-specific decisions:
/// <list type="bullet">
/// <item>a drawn account accrues EXACTLY ONCE per day (the dispatch ledger absorbs the re-tick), under the
/// canonical number-pinned key <c>("accrue_overdraft_interest", accrual_day)</c>;</item>
/// <item>each drawn account is its OWN occurrence, keyed on the same accrual day;</item>
/// <item>the occurrence key is the accrual DAY (a recurring per-day charge), so a different day is a different
/// accrual and the same day re-derives the same id;</item>
/// <item>a non-Guid account_ref (a cross-family shape the family-agnostic overdraft set may carry) is skipped,
/// not fired and not thrown on.</item>
/// </list>
/// All Docker-free, no clock: the accrual day is the <c>asOf</c> input.
/// </summary>
public sealed class OverdraftAccrualRuleTests
{
    private static readonly DateOnly Today = new(2026, 6, 28);

    // The stable command-kind the ENGINE /v1/accounts/{id}/overdraft/accrue endpoint dedupes under. The driver
    // must converge on it, so the key cross-checks use this literal rather than the rule's own constant.
    private const string EngineAccrueKind = "accrue_overdraft_interest";

    [Fact]
    public async Task A_drawn_account_accrues_exactly_once_per_day_under_the_canonical_key()
    {
        var accountId = Guid.NewGuid();
        var sink = new RecordingSink();
        var pass = NewPass(sink, new OverdraftAccrualRule(Reader(Overdrawn(accountId, -100_000))));

        var first = await pass.RunOnceAsync(Today);
        var second = await pass.RunOnceAsync(Today);

        Assert.Single(first);
        Assert.Empty(second); // same (account, day) occurrence — the dispatch ledger dedups the re-tick

        Assert.Single(sink.Dispatched);
        Assert.Equal(EngineAccrueKind, first[0].CommandKind);
        Assert.Equal(Today.DayNumber, first[0].OccurrenceKey); // the accrual day's ordinal, one accrual per day
        Assert.Equal(accountId, first[0].InstanceId);
        Assert.Equal($"/v1/accounts/{accountId:D}/overdraft/accrue", first[0].RequestPath);
        Assert.Equal(LifecycleCommandKey.Derive(accountId, EngineAccrueKind, Today.DayNumber), first[0].CommandId);
    }

    [Fact]
    public async Task The_accrual_decision_carries_the_accrual_date_body_and_no_sca_scope()
    {
        var accountId = Guid.NewGuid();
        var rule = new OverdraftAccrualRule(Reader(Overdrawn(accountId, -100_000)));

        var decision = Assert.Single(await rule.EvaluateAsync(Today));

        // The body carries only the accrual's economic date (no PII, ADR-PC-004); the engine stamps it as the
        // business valid_time so a late/backfilled accrual records the correct date (ADR-PC-023).
        Assert.Equal(Today, Assert.IsType<DateOnly>(decision.Body["accrual_date"]));
        Assert.Equal(Today, decision.DueAt);
        // NOT a rails money-mover: an internal overdraft charge carries no scoped SCA service principal.
        Assert.Null(decision.ServicePrincipalScope);
    }

    [Fact]
    public async Task Each_drawn_account_is_its_own_occurrence_on_the_accrual_day()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var rule = new OverdraftAccrualRule(Reader(Overdrawn(a, -5_000), Overdrawn(b, -250_000)));

        var decisions = await rule.EvaluateAsync(Today);

        // Two drawn accounts, one accrual each, keyed on the SAME accrual day but distinct accounts — two
        // independent accruals with distinct command ids.
        Assert.Equal(2, decisions.Count);
        Assert.Equal(new[] { a, b }.OrderBy(g => g).ToArray(), decisions.Select(d => d.InstanceId).OrderBy(g => g).ToArray());
        Assert.All(decisions, d => Assert.Equal(Today.DayNumber, d.OccurrenceKey));
        Assert.Equal(2, decisions.Select(LifecycleDispatchId.Of).Distinct().Count());
    }

    [Fact]
    public async Task The_occurrence_key_is_the_accrual_day_so_a_different_day_is_a_different_accrual()
    {
        var accountId = Guid.NewGuid();
        var rule = new OverdraftAccrualRule(Reader(Overdrawn(accountId, -100_000)));

        var today = Assert.Single(await rule.EvaluateAsync(Today));
        var tomorrow = Assert.Single(await rule.EvaluateAsync(Today.AddDays(1)));

        // A recurring per-day charge: the accrual day is the occurrence, so the next day re-derives a DIFFERENT
        // id (a distinct accrual), while re-running the same day re-derives the same id (deduped above).
        Assert.NotEqual(today.OccurrenceKey, tomorrow.OccurrenceKey);
        Assert.NotEqual(LifecycleDispatchId.Of(today), LifecycleDispatchId.Of(tomorrow));
        Assert.Equal(Today, today.DueAt);
        Assert.Equal(Today.AddDays(1), tomorrow.DueAt);
    }

    [Fact]
    public async Task A_non_guid_account_ref_is_skipped_not_fired_and_not_thrown_on()
    {
        var accountId = Guid.NewGuid();
        // The overdraft set is family-agnostic; a ref that is not a Guid is a non-current-account shape the
        // rule must skip defensively (never crash the whole pass) — the current-account endpoint is the filter.
        var rule = new OverdraftAccrualRule(Reader(
            new OverdrawnAccount("not-a-guid", -1_000),
            Overdrawn(accountId, -100_000)));

        var decision = Assert.Single(await rule.EvaluateAsync(Today));
        Assert.Equal(accountId, decision.InstanceId);
    }

    // --- helpers ---

    private static LifecycleSchedulePass NewPass(ILifecycleCommandSink sink, params ILifecycleCommandRule[] rules) =>
        new(rules, new InMemoryLifecycleDispatchLedger(), sink);

    private static AccountBalanceReader Reader(params OverdrawnAccount[] accounts) =>
        new(new FakeMovementLedgerStore(accounts), new ThrowingAccountHoldStore());

    private static OverdrawnAccount Overdrawn(Guid accountId, long balanceCents) =>
        new(accountId.ToString(), balanceCents);

    /// <summary>A fake movement-ledger store that returns the given overdrawn set (already the negative-balance
    /// set the real GetOverdrawnAccountsAsync groups + filters), throwing for every read the accrual rule never
    /// makes.</summary>
    private sealed class FakeMovementLedgerStore(params OverdrawnAccount[] accounts) : IMovementLedgerStore
    {
        public Task<IReadOnlyList<OverdrawnAccount>> GetOverdrawnAccountsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<OverdrawnAccount>>(accounts);

        public Task AppendAsync(IReadOnlyList<MovementLedgerEntry> entries, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<long> GetBalanceCentsAsync(string accountRef, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<MovementLedgerEntry>> GetStatementAsync(
            string accountRef, CancellationToken ct = default) => throw new NotSupportedException();

        public Task TruncateAsync(CancellationToken ct = default) => throw new NotSupportedException();
    }

    /// <summary>The hold store AccountBalanceReader's ctor requires; the accrual read never touches it, so every
    /// member fails loud if the read path ever regresses into reading holds.</summary>
    private sealed class ThrowingAccountHoldStore : IAccountHoldStore
    {
        public Task PlaceAsync(AccountHoldRow hold, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task PlaceLegalAsync(AccountHoldRow legalHold, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<HoldReleaseResult> ReleaseLegalAsync(
            string holdId, Guid releasedStreamId, long releasedSequence, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<HoldReleaseResult> CaptureAsync(
            string holdId, long capturedAmountCents, Guid releasedStreamId, long releasedSequence,
            CancellationToken ct = default) => throw new NotSupportedException();

        public Task<HoldReleaseResult> ExpireAsync(
            string holdId, Guid releasedStreamId, long releasedSequence, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<long> GetActiveHoldCentsAsync(string accountRef, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<AccountHoldRow>> GetActiveHoldsAsync(
            string accountRef, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<IReadOnlyList<AccountHoldRow>> GetActiveHoldsWithValueDateAtOrBeforeAsync(
            DateOnly valueDateHorizon, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<IReadOnlyList<AccountHoldRow>> GetActiveLegalHoldsWithExpiryAtOrBeforeAsync(
            DateOnly expiryHorizon, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<long> GetWindowedAuthorizationHoldCentsAsync(
            string accountRef, DateOnly fromInclusive, DateOnly toInclusive, CancellationToken ct = default) =>
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
