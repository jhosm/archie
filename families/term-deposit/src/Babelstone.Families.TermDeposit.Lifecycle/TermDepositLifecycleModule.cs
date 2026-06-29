using Babelstone.Families.TermDeposit.Application;
using Babelstone.Lifecycle;
using Microsoft.Extensions.DependencyInjection;

namespace Babelstone.Families.TermDeposit.Lifecycle;

/// <summary>
/// The term-deposit family's lifecycle-command module (ADR-PC-036; ADR-PC-021) — the
/// plug-in by which the family's <see cref="MaturityRule"/> joins the family-agnostic lifecycle-command driver.
/// It is the lifecycle-side sibling of the family's engine module (<c>IFamilyHostModule</c>), saga module
/// (<c>ISagaModule</c>) and notification module (<c>IFamilyNotificationModule</c>): the host composes by
/// looping over the discovered modules calling <see cref="ConfigureServices"/>, and the driver core never
/// names this family (ADR-IC-019) — the host (the composition root) is the only place that does.
/// </summary>
/// <remarks>
/// The deposit read-model store the rule range-scans is FAMILY-OWNED (ADR-IC-019): a "deposit" that matures
/// is term-deposit knowledge, so this module names the concrete <see cref="PostgresDepositReadModelStore"/> and
/// binds it behind the family-agnostic <see cref="IDepositReadModelStore"/> the rule depends on, over the engine
/// read-model connection the host conveyed on the context. A second family (e.g. personal-loan) ships its own
/// module + rule alongside this one with zero driver-core diff — the open/closed property ADR-PC-036 commits
/// to.
/// </remarks>
public sealed class TermDepositLifecycleModule : IFamilyLifecycleModule
{
    /// <summary>The family discriminator this module composes (equals the engine envelope's
    /// <c>aggregate_type</c> for term deposits, ADR-PC-009).</summary>
    public const string Family = "term_deposit";

    /// <inheritdoc />
    public string FamilyName => Family;

    /// <inheritdoc />
    public void ConfigureServices(IServiceCollection services, LifecycleModuleContext ctx)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(ctx);

        // The family-owned Npgsql read-model store, registered behind the family-agnostic interface the rule
        // depends on (the composition pattern: the family module names Postgres, the driver core never
        // does). It reads the SAME engine read-model database the engine materialises (read_model.deposits);
        // the driver only reads it, over the connection the host resolved once at the composition root.
        services.AddSingleton<IDepositReadModelStore>(
            new PostgresDepositReadModelStore(ctx.ReadModelConnectionString));

        // The family's lifecycle rule joins the core-resolvable ILifecycleCommandRule set the
        // LifecycleSchedulePass enumerates per tick. It resolves the family-owned store registered just above.
        services.AddSingleton<ILifecycleCommandRule>(sp =>
            new MaturityRule(sp.GetRequiredService<IDepositReadModelStore>()));
    }
}
