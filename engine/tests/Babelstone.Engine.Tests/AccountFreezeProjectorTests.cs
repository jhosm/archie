using Babelstone.EventStore;
using Xunit;

namespace Babelstone.Engine.Tests;

/// <summary>
/// Tests for the spine-owned <see cref="AccountFreezeProjector"/> and the frozen-predicate read
/// (ADR-PC-041). In plain English: these prove a compliance freeze folds into a rebuildable
/// per-instance predicate the authorization decider consults — the instance is frozen from
/// <c>AccountFrozen</c> until a matching <c>AccountUnfrozen</c>, the reason/actor are observable, a
/// duplicate unfreeze folds at most once and is surfaced, and a truncate-then-refold reproduces the
/// predicate identically (no clock in the fold — DETERMINISM_GATE).
/// </summary>
public sealed class AccountFreezeProjectorTests
{
    private static AccountFrozen Frozen(
        Guid instance, string freezeId, string reason = "SANCTIONS_MATCH", string actor = "compliance-svc",
        DateOnly? expiresAt = null) =>
        new(instance, freezeId, reason, actor, expiresAt);

    private static AccountUnfrozen Unfrozen(
        Guid instance, string freezeId, string actor = "compliance-svc", string reason = "SCREENING_CLEARED") =>
        new(instance, freezeId, actor, reason);

    // FREEZE_GATES_AUTHORIZATION (the predicate side, ADR-PC-041 slot 2): a placed freeze makes the
    // instance read as frozen, carrying its reason/actor (HOLD_REASON_OBSERVABLE).
    [Fact]
    public async Task A_placed_freeze_makes_the_instance_read_as_frozen_with_its_reason_and_actor()
    {
        var store = new InMemoryAccountFreezeStore();
        var reader = new AccountFreezeReader(store);
        var projector = new AccountFreezeProjector(store);
        var instance = Guid.NewGuid();

        Assert.Null(await reader.GetActiveFreezeAsync(instance));

        await projector.ApplyAsync(instance, 0, Frozen(instance, "freeze-1", reason: "AML_SCREENING", actor: "aml-team"));

        var freeze = await reader.GetActiveFreezeAsync(instance);
        Assert.NotNull(freeze);
        Assert.Equal(FreezeState.Active, freeze!.State);
        Assert.Equal("AML_SCREENING", freeze.FreezeReason);
        Assert.Equal("aml-team", freeze.ComplianceActor);
    }

    [Fact]
    public async Task An_unfreeze_lifts_the_block()
    {
        var store = new InMemoryAccountFreezeStore();
        var reader = new AccountFreezeReader(store);
        var projector = new AccountFreezeProjector(store);
        var instance = Guid.NewGuid();

        await projector.ApplyAsync(instance, 0, Frozen(instance, "freeze-1"));
        await projector.ApplyAsync(instance, 1, Unfrozen(instance, "freeze-1"));

        Assert.Null(await reader.GetActiveFreezeAsync(instance));
    }

    [Fact]
    public async Task A_freeze_event_must_ride_its_own_instance_stream()
    {
        var store = new InMemoryAccountFreezeStore();
        var projector = new AccountFreezeProjector(store);
        var instance = Guid.NewGuid();
        var foreignStream = Guid.NewGuid();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => projector.ApplyAsync(foreignStream, 0, Frozen(instance, "freeze-1")));
    }

    [Fact]
    public async Task A_duplicate_unfreeze_folds_once_and_is_surfaced()
    {
        var anomalies = new List<FreezeLiftAnomaly>();
        var store = new InMemoryAccountFreezeStore();
        var projector = new AccountFreezeProjector(store, anomalies.Add);
        var instance = Guid.NewGuid();

        await projector.ApplyAsync(instance, 0, Frozen(instance, "freeze-1"));
        await projector.ApplyAsync(instance, 1, Unfrozen(instance, "freeze-1"));
        await projector.ApplyAsync(instance, 2, Unfrozen(instance, "freeze-1")); // duplicate

        var anomaly = Assert.Single(anomalies);
        Assert.Equal(FreezeLiftResult.AlreadyLifted, anomaly.Kind);
        Assert.Equal("freeze-1", anomaly.FreezeId);
    }

    // DETERMINISM_GATE (ADR-PC-041 slot 2; ADR-PC-023): the freeze fold reads no clock, so a
    // truncate-then-refold reproduces the frozen predicate identically.
    [Fact]
    public async Task Replaying_the_freeze_sequence_after_a_rebuild_reproduces_the_predicate()
    {
        var store = new InMemoryAccountFreezeStore();
        var reader = new AccountFreezeReader(store);
        var projector = new AccountFreezeProjector(store);
        var instance = Guid.NewGuid();

        async Task FoldAll()
        {
            await projector.ApplyAsync(instance, 0, Frozen(instance, "freeze-1", reason: "SANCTIONS_MATCH"));
            await projector.ApplyAsync(instance, 1, Frozen(instance, "freeze-2", reason: "AML_SCREENING"));
            await projector.ApplyAsync(instance, 2, Unfrozen(instance, "freeze-1"));
        }

        await FoldAll();
        var before = await reader.GetActiveFreezeAsync(instance);

        await projector.ResetForRebuildAsync();
        await FoldAll();
        var after = await reader.GetActiveFreezeAsync(instance);

        // freeze-2 (AML_SCREENING) is the surviving active freeze, reproduced identically.
        Assert.NotNull(before);
        Assert.Equal("AML_SCREENING", before!.FreezeReason);
        Assert.Equal(before.FreezeId, after!.FreezeId);
        Assert.Equal(before.FreezeReason, after.FreezeReason);
    }

    /// <summary>
    /// An in-memory <see cref="IAccountFreezeStore"/> test double mirroring the
    /// <see cref="PostgresAccountFreezeStore"/> contract: placement idempotent on <c>freeze_id</c>,
    /// the lift transitioning ONLY an ACTIVE row with the three-way <see cref="FreezeLiftResult"/>
    /// answer, and truncate for rebuild.
    /// </summary>
    private sealed class InMemoryAccountFreezeStore : IAccountFreezeStore
    {
        private readonly Dictionary<string, AccountFreezeRow> _rows = new(StringComparer.Ordinal);

        public Task FreezeAsync(AccountFreezeRow freeze, CancellationToken ct = default)
        {
            _rows.TryAdd(freeze.FreezeId, freeze); // ON CONFLICT (freeze_id) DO NOTHING
            return Task.CompletedTask;
        }

        public Task<FreezeLiftResult> UnfreezeAsync(
            string freezeId, Guid liftedStreamId, long liftedSequence, string unfreezeActor,
            string unfreezeReason, CancellationToken ct = default)
        {
            if (!_rows.TryGetValue(freezeId, out var row))
            {
                return Task.FromResult(FreezeLiftResult.NeverFrozen);
            }

            if (row.State != "ACTIVE")
            {
                return Task.FromResult(FreezeLiftResult.AlreadyLifted);
            }

            _rows[freezeId] = row with
            {
                State = "LIFTED",
                LiftedStreamId = liftedStreamId,
                LiftedSequence = liftedSequence,
                UnfreezeActor = unfreezeActor,
                UnfreezeReason = unfreezeReason,
            };
            return Task.FromResult(FreezeLiftResult.Transitioned);
        }

        public Task<AccountFreezeRow?> GetActiveFreezeAsync(Guid instanceId, CancellationToken ct = default)
        {
            var row = _rows.Values
                .Where(r => r.InstanceId == instanceId && r.State == "ACTIVE")
                .OrderBy(r => r.PlacedSequence)
                .ThenBy(r => r.FreezeId, StringComparer.Ordinal)
                .FirstOrDefault();
            return Task.FromResult(row);
        }

        public Task<IReadOnlyList<AccountFreezeRow>> GetActiveFreezesWithExpiryAtOrBeforeAsync(
            DateOnly expiryHorizon, CancellationToken ct = default)
        {
            IReadOnlyList<AccountFreezeRow> rows = _rows.Values
                .Where(r => r.State == "ACTIVE" && r.FreezeExpiresAt is { } e && e <= expiryHorizon)
                .OrderBy(r => r.InstanceId)
                .ThenBy(r => r.FreezeId, StringComparer.Ordinal)
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
