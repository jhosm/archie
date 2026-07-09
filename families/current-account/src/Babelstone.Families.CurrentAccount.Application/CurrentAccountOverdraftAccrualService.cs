using Babelstone.Engine;
using Babelstone.Families.CurrentAccount;
using Babelstone.Packs;
using Babelstone.RateSheets;

namespace Babelstone.Families.CurrentAccount.Application;

/// <summary>
/// The impure command shell for the projection-derived overdraft-interest accrual (ADR-PC-037 §D5). In plain
/// English: it does the I/O a day's descoberto charge needs — load the account, read the drawn balance, resolve
/// the overdraft TAN from the rate sheet, and append the fee — while the decision of WHICH accounts are drawn
/// stays upstream in the clock-owning ADR-PC-036 lifecycle-command driver (this shell never reads a clock or a
/// projection to decide; it records the accrual the driver asked for). The money math + event shape are the
/// pure <see cref="CurrentAccountOverdraftAccrualDecider"/> (command-side, ADR-PC-037 §P3), never a fold.
/// </summary>
/// <remarks>
/// <para>
/// A SEPARATE service from the account state machine (<see cref="CurrentAccountLifecycleService"/>), the
/// authorize money-mover (<see cref="CurrentAccountAuthorizeService"/>), and the hold-expiry release
/// (<see cref="CurrentAccountHoldExpiryService"/>): this one posts a Debit <see cref="Movement"/> against a
/// drawn balance. It depends only on the generic engine runtime + spine reader, the family's own config store,
/// the rate-sheet store, and the pinned pack — the dependency arrow is family→engine, never the reverse
/// (ENGINE_FAMILY_AGNOSTIC).
/// </para>
/// <para>
/// <b>No-op vs. fail-loud.</b> Three legitimate no-ops append nothing and return the current head (so the
/// driver marks the occurrence dispatched, never a 422 retry-storm): the account is not Active, the balance is
/// not drawn (it flipped non-negative since the driver read it), or the product declares no overdraft rate (a
/// <c>ca_pt_basic</c> account). But a product that DECLARES an overdraft rate whose sheet is not deployed — or
/// does not price the drawn band — is a real misconfiguration: this throws <see cref="DomainRejectedException"/>
/// (→ 422), so the driver retries the occurrence until the sheet lands rather than silently skipping a fee the
/// account genuinely owes (never a silent failure).
/// </para>
/// <para>
/// <b>Idempotent on the command id (ADR-PC-029 slot 4).</b> The append threads the driver's canonical
/// number-pinned dispatch id, so an at-least-once re-POST returns the ORIGINAL head with no second accrual —
/// one accrual lands per account per day (the engine's command_dedup under the driver's dispatch ledger).
/// </para>
/// </remarks>
public sealed class CurrentAccountOverdraftAccrualService(
    AggregateRuntime<AccountPosition> runtime,
    AccountBalanceReader balances,
    CurrentAccountProductConfigStore configs,
    IRateSheetStore rateSheets,
    VerifiedPack pack)
{
    private static readonly CurrentAccountFamilyModule Family = new();

    /// <summary>
    /// Accrue one day of overdraft interest for the command's account and return the new stream head (or the
    /// unchanged head on a no-op). Loads the account, gates on lifecycle + drawn balance + a declared overdraft
    /// rate, resolves the TAN from the rate sheet active as of <paramref name="validTime"/>, decides the fee
    /// with the pure decider, and appends idempotently on the command id. Propagates <c>ConcurrencyException</c>
    /// / <c>DuplicateCommandException</c> for the endpoint to map, and throws
    /// <see cref="DomainRejectedException"/> when a rate-declaring account cannot resolve its rate.
    /// </summary>
    public async Task<long> AccrueOverdraftInterestAsync(
        OverdraftAccrualCommand command, DateTimeOffset validTime, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var hydrated = await runtime.LoadAsync(command.AccountId, ct);
        var position = hydrated.State;

        // Gate 1 — only a live current account accrues. A Pending (never-opened / non-current) or Failed target
        // is a no-op, not a 422: the driver reads a family-agnostic overdraft set, so a cross-family or
        // non-account id resolves to a non-Active position here and is skipped without a retry-storm. (Whether a
        // Closed/Dormant drawn account keeps accruing is a reconciliation nuance out of the coarse-start;
        // v1 accrues only from Active — ADR-PC-037 §Residual-risks.)
        if (position.Lifecycle != AccountLifecycle.Active)
        {
            return hydrated.Version;
        }

        var accountRef = position.AccountRef;
        var balanceCents = await balances.GetAccountingBalanceCentsAsync(accountRef, ct);

        // Gate 2 — not drawn ⇒ nothing to accrue (the balance flipped non-negative between the driver's
        // projection read and this command).
        if (balanceCents >= 0)
        {
            return hydrated.Version;
        }

        // Gate 3 — the product must declare an overdraft-interest rate. Absent ⇒ this product accrues no
        // overdraft interest (a ca_pt_basic account); a legitimate no-op, not a misconfiguration.
        var config = configs.Resolve(position.ProductCode);
        if (config?.OverdraftRate is not { } rate)
        {
            return hydrated.Version;
        }

        // Resolve the overdraft TAN from the rate sheet effective on the accrual date (ADR-PC-008), the SAME
        // shape the deposit constitution service resolves its rate. A product that DECLARES an overdraft rate
        // but whose sheet is not deployed / does not price the (product, role, drawn) band is a real
        // MISCONFIGURATION — throw (→ 422) so the driver retries until the sheet lands, never a silent skip.
        var resolution = await rateSheets.ResolveAsync(Family.FamilyName, validTime, ct)
            ?? throw new DomainRejectedException(
                $"No current_account rate sheet is effective as of {command.AccrualDate:O} to resolve the "
                + $"overdraft-interest rate for product '{position.ProductCode}'.");

        var drawnPrincipalCents = -balanceCents;
        var tanBasisPoints =
            resolution.ResolveTanBasisPoints(position.ProductCode, rate.RoleSelector, drawnPrincipalCents)
            ?? throw new DomainRejectedException(
                $"Rate sheet '{resolution.RateSheetVersionId}' does not price overdraft interest for product "
                + $"'{position.ProductCode}' (role '{rate.RoleSelector}', drawn {drawnPrincipalCents} cents).");

        // The fee math + event shape is the pure decider (command-side, ADR-PC-037 §P3). A drawn balance whose
        // day's interest rounds to zero cents accrues nothing (the decider returns null) — a no-op.
        var @event = CurrentAccountOverdraftAccrualDecider.Decide(
            command.AccountId, accountRef, balanceCents, tanBasisPoints, resolution.RateSheetVersionId,
            command.AccrualDate, command.CommandId);
        if (@event is null)
        {
            return hydrated.Version;
        }

        return await runtime.AppendAsync(
            command.AccountId, hydrated.Version, [@event],
            Context(command.Actor, validTime, command.CommandId), ct);
    }

    // The family / pack / schema pins ride the EventEnvelope via AppendContext, never on the event record
    // (ADR-PC-009). commandId is the ADR-PC-029 idempotency key that makes a replay return the original head.
    private AppendContext Context(string actor, DateTimeOffset validTime, Guid commandId) =>
        new(Family.FamilyName, pack.VersionKey, Family.SchemaVersion, actor, validTime, CommandId: commandId);
}
