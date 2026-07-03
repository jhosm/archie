using Babelstone.Families.PersonalLoan.Application;
using Babelstone.Lifecycle;
using Microsoft.Extensions.DependencyInjection;

namespace Babelstone.Families.PersonalLoan.Lifecycle;

/// <summary>
/// The personal-loan family's lifecycle-command module (ADR-PC-036; ADR-PC-021) — the
/// plug-in by which the family's <see cref="InstallmentRule"/> joins the family-agnostic lifecycle-command
/// driver. It is the recurring-installment sibling of the term-deposit one-shot
/// <c>TermDepositLifecycleModule</c>: the host composes by looping over the discovered modules calling
/// <see cref="ConfigureServices"/>, and the driver core never names this family (ADR-IC-019) — the host
/// (the composition root) is the only place that does.
/// </summary>
/// <remarks>
/// The forward <c>installment_calendar</c> read-model store the rule range-scans is FAMILY-OWNED (ADR-IC-019
///): a "loan installment" is personal-loan knowledge, so this module names the concrete
/// <see cref="PostgresInstallmentCalendarReadModelStore"/> and binds it behind the family-agnostic
/// <see cref="IInstallmentCalendarReadModelStore"/> the rule depends on, over the engine read-model connection
/// the host conveyed on the context. It ships alongside the term-deposit module with zero driver-core diff —
/// the open/closed property ADR-PC-036 commits to.
/// </remarks>
public sealed class PersonalLoanLifecycleModule : IFamilyLifecycleModule
{
    /// <summary>The family discriminator this module composes (equals the engine envelope's
    /// <c>aggregate_type</c> for personal loans, ADR-PC-009).</summary>
    public const string Family = "personal_loan";

    /// <inheritdoc />
    public string FamilyName => Family;

    /// <inheritdoc />
    public void ConfigureServices(IServiceCollection services, LifecycleModuleContext ctx)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(ctx);

        // The family-owned Npgsql read-model store, registered behind the family-agnostic interface the rule
        // depends on (the composition pattern: the family module names Postgres, the driver core never
        // does). It reads the SAME engine read-model database the engine materialises
        // (read_model.installment_calendar); the driver only reads it, over the connection the host resolved
        // once at the composition root.
        services.AddSingleton<IInstallmentCalendarReadModelStore>(
            new PostgresInstallmentCalendarReadModelStore(ctx.ReadModelConnectionString));

        // The family's lifecycle rule joins the core-resolvable ILifecycleCommandRule set the
        // LifecycleSchedulePass enumerates per tick. It resolves the family-owned store registered just
        // above, plus the HOST-registered family-agnostic ISettlementHealthProbe — the LCD-2
        // settlement-health gate the RECURRING path consults before surfacing a loan's next occurrence
        // (ADR-PC-036 §Decision 4): the probe is generic driver-core machinery (it keys on the instance id
        // alone), so the composition root registers it once and every recurring family rule shares it.
        services.AddSingleton<ILifecycleCommandRule>(sp =>
            new InstallmentRule(
                sp.GetRequiredService<IInstallmentCalendarReadModelStore>(),
                sp.GetRequiredService<ISettlementHealthProbe>()));
    }
}
