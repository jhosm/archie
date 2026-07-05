using Babelstone.EventStore;

namespace Babelstone.Engine;

/// <summary>
/// The lifecycle state of an <see cref="AccountFreeze"/> — a CLOSED set of two members mirroring the
/// two pure transitions <c>AccountFrozen → AccountUnfrozen</c> (ADR-PC-041).
/// </summary>
public enum FreezeState
{
    /// <summary>Placed and unlifted: the stages-3–5 decider refuses the instance's debits.</summary>
    Active,

    /// <summary>A matching <c>AccountUnfrozen</c> arrived: debits are allowed again.</summary>
    Lifted,
}

/// <summary>
/// A compliance freeze — the spine value object for the total-block a fraud/AML/sanctions process
/// places on an instance (ADR-PC-041). In plain English: while a freeze is <see cref="FreezeState.Active"/>
/// the instance is blocked from debits, and the reason/actor are carried so a decline can name WHY.
/// Unlike a <see cref="Hold"/>, a freeze is NOT an amount — it never touches available balance; it is
/// a predicate the authorization decider consults.
/// </summary>
/// <remarks>
/// Carries NO family-typed shape and NO PII (ENGINE_FAMILY_AGNOSTIC / ADR-PC-004): opaque
/// <see cref="FreezeId"/>/<see cref="InstanceId"/>, a stable machine-code <see cref="FreezeReason"/>,
/// an operator <see cref="ComplianceActor"/>, and an optional date. A READ shape over the rebuildable
/// frozen-predicate fold (never a stored mutable flag — ADR-PC-041 slot 2).
/// </remarks>
/// <param name="FreezeId">The dedup/correlation key both lifecycle events of this freeze carry (ADR-PC-041 slot 4).</param>
/// <param name="InstanceId">The instance the freeze blocks — never PII (ADR-PC-004).</param>
/// <param name="FreezeReason">Why the freeze was placed — a stable machine code, never PII.</param>
/// <param name="ComplianceActor">The operator/service actor that placed the freeze — never PII.</param>
/// <param name="FreezeExpiresAt">The advisory expiry horizon (ADR-PC-023); null = open-ended.</param>
/// <param name="State">Where in the two-transition lifecycle this freeze is (ADR-PC-041).</param>
public sealed record AccountFreeze(
    string FreezeId,
    Guid InstanceId,
    string FreezeReason,
    string ComplianceActor,
    DateOnly? FreezeExpiresAt,
    FreezeState State);

/// <summary>
/// The spine-owned frozen-predicate read (ADR-PC-041 slot 2): "is this instance frozen, and if so,
/// why?" — the stage-3 input the authorization command shell reads and hands the pure
/// <see cref="FundsAndRulesDecider"/>, plus the projection-derived freeze-expiry read an operator
/// drives to append <c>AccountUnfrozen</c> facts (ADR-PC-023). Family-agnostic by construction: the
/// <c>account_freezes</c> store is keyed by the opaque instance id, so one reader serves every family.
/// </summary>
public sealed class AccountFreezeReader(IAccountFreezeStore freezes)
{
    /// <summary>
    /// The instance's currently-active freeze, or null if it is not frozen — the predicate the
    /// decider consults BEFORE its funds check (the freeze gates the decision, never the fold).
    /// </summary>
    public async Task<AccountFreeze?> GetActiveFreezeAsync(Guid instanceId, CancellationToken ct = default)
    {
        var row = await freezes.GetActiveFreezeAsync(instanceId, ct);
        return row is null ? null : ToFreeze(row);
    }

    /// <summary>
    /// The projection-derived freeze-expiry read (ADR-PC-023): every ACTIVE freeze whose
    /// <see cref="AccountFreeze.FreezeExpiresAt"/> horizon is at or before
    /// <paramref name="expiryHorizon"/> — what an operator/command shell reads to decide which
    /// <c>AccountUnfrozen</c> facts to append. Open-ended freezes are never candidates; the horizon
    /// is an input, never a clock read.
    /// </summary>
    public async Task<IReadOnlyList<AccountFreeze>> GetFreezeExpiryCandidatesAsync(
        DateOnly expiryHorizon, CancellationToken ct = default)
    {
        var rows = await freezes.GetActiveFreezesWithExpiryAtOrBeforeAsync(expiryHorizon, ct);
        return rows.Select(ToFreeze).ToList();
    }

    // Rows out of the store are ACTIVE by query; the closed-set parse is total (fail-loud on a state
    // outside the migration-0022 CHECK set rather than a silent default).
    private static AccountFreeze ToFreeze(AccountFreezeRow row) => new(
        FreezeId: row.FreezeId,
        InstanceId: row.InstanceId,
        FreezeReason: row.FreezeReason,
        ComplianceActor: row.ComplianceActor,
        FreezeExpiresAt: row.FreezeExpiresAt,
        State: Enum.Parse<FreezeState>(row.State, ignoreCase: true));
}
