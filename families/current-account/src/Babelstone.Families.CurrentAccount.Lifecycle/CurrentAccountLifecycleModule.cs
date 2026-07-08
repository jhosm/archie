using Babelstone.Engine;
using Babelstone.EventStore;
using Babelstone.Lifecycle;
using Microsoft.Extensions.DependencyInjection;

namespace Babelstone.Families.CurrentAccount.Lifecycle;

/// <summary>
/// The current_account family's lifecycle-command module (ADR-PC-036; ADR-PC-021) — the plug-in by which the
/// family's <see cref="HoldExpiryRule"/> joins the family-agnostic lifecycle-command driver. It is the
/// lifecycle-side sibling of the family's engine module (<c>IFamilyHostModule</c>) and authorize service: the
/// host composes by looping over the discovered modules calling <see cref="ConfigureServices"/>, and the
/// driver core never names this family (ADR-IC-019) — the host (the composition root) is the only place that
/// does.
/// </summary>
/// <remarks>
/// The read side the rule range-scans is the SPINE's active-hold fold (ADR-PC-033), NOT a family-owned read
/// model: a current account's holds live in account_holds, read through the engine
/// <see cref="AccountBalanceReader"/>. So — unlike <c>TermDepositLifecycleModule</c>, which binds a
/// family-owned <c>PostgresDepositReadModelStore</c> — this module composes the reader from the two concrete
/// spine Npgsql stores over the read-model connection the host conveyed on the context (the composition
/// pattern: the family module names Postgres, the driver core never does). A second family ships its own
/// module + rule alongside this one with zero driver-core diff — the open/closed property ADR-PC-036 commits
/// to. This module is distinct from the family's <c>CurrentAccountLifecycleService</c> (the account
/// open/dormant/close state machine): that relabels account state; this expires spine holds.
/// </remarks>
public sealed class CurrentAccountLifecycleModule : IFamilyLifecycleModule
{
    /// <summary>The family discriminator this module composes (equals the engine envelope's
    /// <c>aggregate_type</c> for current accounts, ADR-PC-009).</summary>
    public const string Family = "current_account";

    /// <inheritdoc />
    public string FamilyName => Family;

    /// <inheritdoc />
    public void ConfigureServices(IServiceCollection services, LifecycleModuleContext ctx)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(ctx);

        // The SPINE-owned active-hold read (ADR-PC-033): unlike the deposit/loan lifecycle modules (which bind
        // a family read-model store), a current account's holds live in the spine's account_holds fold, read
        // through AccountBalanceReader. Build it over the host's read-model connection from the two concrete
        // Npgsql spine stores (both spine projections materialised in the same engine read-model database).
        // The movement-ledger store is required by AccountBalanceReader's ctor but is never touched by the
        // expiry read (GetExpiryCandidatesAsync reads only the hold store); constructing it is cheap — Npgsql
        // connects lazily, on the first query that never comes.
        services.AddSingleton(new AccountBalanceReader(
            new PostgresMovementLedgerStore(ctx.ReadModelConnectionString),
            new PostgresAccountHoldStore(ctx.ReadModelConnectionString)));

        // The family's hold-expiry rule joins the core-resolvable ILifecycleCommandRule set the
        // LifecycleSchedulePass enumerates per tick. It resolves the spine reader registered just above.
        services.AddSingleton<ILifecycleCommandRule>(sp =>
            new HoldExpiryRule(sp.GetRequiredService<AccountBalanceReader>()));
    }
}
