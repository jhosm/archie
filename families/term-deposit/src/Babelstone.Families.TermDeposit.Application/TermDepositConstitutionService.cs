using Babelstone.Engine;
using Babelstone.Packs;
using Babelstone.RateSheets;

namespace Babelstone.Families.TermDeposit.Application;

/// <summary>
/// The term-deposit decider's impure orchestration (ADR-PC-021): it resolves the rate sheet
/// and pack primitives, calls the pure <see cref="TermDepositDecider"/>, settles the money leg,
/// and appends through the runtime. It depends only on generic engine ports
/// (<see cref="AggregateRuntime{TState}"/>, <see cref="IRateSheetStore"/>, <see cref="ISettlementPort"/>)
/// plus the pinned <see cref="VerifiedPack"/> — the dependency arrow is family→engine, never the
/// reverse (ADR-PC-021 §D2).
/// </summary>
/// <remarks>
/// The pinned <paramref name="pack"/> and its primitive bindings model the engine-instance's
/// pinned configuration for the walking skeleton (ADR-PC-009); a config registry resolving them
/// per deposit is later work. The resolve→append pair is two transactions here; the ADR-PC-008
/// §S2 in-transaction version is a tracked follow-up (bd babelstone-3k10). The shared
/// resolve→stamp→settle→append choreography is kept as separable steps so it can lift into a
/// generic ConstitutionPipeline on the second decider (ADR-PC-021 §P5, bd babelstone-osv6).
/// </remarks>
public sealed class TermDepositConstitutionService(
    AggregateRuntime<DepositPosition> runtime,
    IRateSheetStore rateSheets,
    ISettlementPort settlement,
    VerifiedPack pack,
    string dayCountPrimitive,
    string withholdingPrimitive)
{
    // The stream is keyed by the deposit id (v1: stream_id == deposit_id; partition_key == stream_id).
    private static readonly TermDepositFamilyModule Family = new();

    /// <summary>
    /// Constitute a deposit: resolve the active rate sheet, stamp the TAN + version id, debit
    /// the principal, and append <c>DepositConstituted</c> as the stream's first event.
    /// </summary>
    public async Task ConstituteAsync(ConstituteDepositCommand command, CancellationToken ct = default)
    {
        // 1. Resolve the rate sheet active at constitution (ADR-PC-008 §P3); fail loud if none.
        var resolution = await rateSheets.ResolveAsync(Family.FamilyName, command.ConstitutedAt, ct)
            ?? throw new InvalidOperationException(
                $"No rate sheet effective for '{Family.FamilyName}' at {command.ConstitutedAt:O}.");

        // 2. Resolve the TAN for (product, role, principal); a null on a deployed sheet means the
        //    pair is genuinely unpriced — fail loud rather than constitute at a silent zero rate.
        var tan = resolution.ResolveTanBasisPoints(command.ProductId, command.Role, command.PrincipalCents)
            ?? throw new InvalidOperationException(
                $"Rate sheet '{resolution.RateSheetVersionId}' does not price " +
                $"({command.ProductId}, {command.Role}) at {command.PrincipalCents}c.");

        // 3. Decide (pure): build the event, stamping the resolved TAN + the version it came from.
        var constituted = TermDepositDecider.DecideConstitution(command, tan, resolution.RateSheetVersionId);

        // 4. Settle (ADR-PC-016): debit the principal from the funding account before recording it.
        await settlement.SettleAsync(
            new SettlementInstruction(
                command.DepositId, SettlementDirection.Debit, constituted.Principal,
                command.FundingAccount, "constitution"),
            ct);

        // 5. Append the new stream (expectedVersion -1) — events + outbox in one transaction.
        await runtime.AppendAsync(
            command.DepositId, expectedVersion: -1, [constituted],
            Context(command.Actor, command.ConstitutedAt), ct);
    }

    /// <summary>
    /// Mature a constituted deposit: rehydrate it, run the AT_MATURITY flow against the pinned
    /// pack's day-count and withholding, credit the payout, and append the three closing events.
    /// </summary>
    public async Task MatureAsync(MatureDepositCommand command, CancellationToken ct = default)
    {
        // 1. Rehydrate the constituted position (load-then-append on the live stream head).
        var hydrated = await runtime.LoadAsync(command.DepositId, ct);
        var position = hydrated.State;
        if (position.Lifecycle != DepositLifecycle.Active)
        {
            throw new InvalidOperationException(
                $"Deposit {command.DepositId} is {position.Lifecycle}, not Active; cannot mature.");
        }

        // 2. Pack-resolved primitives (fail loud, never a silent default): the day-count
        //    convention and the withholding rate the deposit's pinned pack declares.
        var dayCount = pack.ResolveDayCount(dayCountPrimitive);
        var withholdingBps = pack.Withholdings.TryGetValue(withholdingPrimitive, out var withholding)
            ? withholding.RateBasisPoints
            : throw new InvalidOperationException(
                $"Withholding primitive '{withholdingPrimitive}' is not declared in pack {pack.VersionKey}.");

        // 3. Decide (pure): accrue → withhold → mature.
        var events = TermDepositDecider.DecideMaturity(position, dayCount, withholdingBps);

        // 4. Settle (ADR-PC-016): credit the total payout. The DepositMatured event is the last.
        var matured = (DepositMatured)events[^1];
        await settlement.SettleAsync(
            new SettlementInstruction(
                command.DepositId, SettlementDirection.Credit, matured.TotalPayout,
                command.PayoutAccount, "maturity"),
            ct);

        // 5. Append at the current head (optimistic concurrency on the second append).
        await runtime.AppendAsync(
            command.DepositId, hydrated.Version, events,
            Context(command.Actor, command.MaturedAt), ct);
    }

    private AppendContext Context(string actor, DateTimeOffset validTime) =>
        new(Family.FamilyName, pack.VersionKey, Family.SchemaVersion, actor, validTime);
}
