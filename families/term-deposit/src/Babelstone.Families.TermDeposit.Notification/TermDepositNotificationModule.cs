using Babelstone.Notification;
using Microsoft.Extensions.DependencyInjection;

namespace Babelstone.Families.TermDeposit.Notification;

/// <summary>
/// The term-deposit family's notification module (ADR-IC-019 §D4/§P4 + Amendment 2026-06-24) — the plug-in
/// by which the family's <see cref="MaturityReminderRule"/> joins the family-agnostic notification core. It
/// is the notification-side sibling of the family's engine module (<c>IFamilyHostModule</c>) and saga module
/// (<c>ISagaModule</c>): the host composes by looping over the registered modules calling
/// <see cref="ConfigureServices"/>, and the core never names this family (ADR-IC-019 §D2/§P2) — the host
/// (the §D4 composition root) is the only place that does.
/// </summary>
/// <remarks>
/// The opt-out-window width is sourced family-side from the pinned PT pack's
/// <c>AutoRenewalOptoutWindowDays</c>, surfaced to the host as configuration under
/// <see cref="OptOutWindowDaysConfigKey"/> (ADR-PC-007 — pack parameters are version-pinned per instance),
/// with the family-side documented default <see cref="DefaultOptOutWindowDays"/> as the fallback. The value
/// never lives in the notification core (ADR-IC-019 §D1). A second family (e.g. personal-loan) ships its own
/// module + rule alongside this one with zero core diff — the open/closed property §D2 commits to.
/// </remarks>
public sealed class TermDepositNotificationModule : IFamilyNotificationModule
{
    /// <summary>The family discriminator this module composes (ADR-PC-009 — equals the engine
    /// envelope's <c>aggregate_type</c> for term deposits).</summary>
    public const string Family = "term_deposit";

    /// <summary>The family-side documented default opt-out-window width (02 §2.4.4 — "typically the final 14
    /// days before maturity"). The canonical value is the pinned PT pack's <c>AutoRenewalOptoutWindowDays</c>;
    /// this default applies only when the host supplies no configured override.</summary>
    public const int DefaultOptOutWindowDays = 14;

    /// <summary>The configuration key the host binds the pack-pinned opt-out-window width under, so the
    /// value reaches this family-side module without the notification core ever holding it (ADR-IC-019 §D1).</summary>
    public const string OptOutWindowDaysConfigKey = "TermDeposit:AutoRenewalOptoutWindowDays";

    /// <inheritdoc />
    public string FamilyName => Family;

    /// <inheritdoc />
    public void ConfigureServices(IServiceCollection services, NotificationModuleContext ctx)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(ctx);

        // The opt-out window is pack data (ADR-PC-007), resolved family-side from the host configuration —
        // never a literal in the notification core (ADR-IC-019 §D1). The family-side default applies only
        // when the host configures no override.
        var optOutWindowDays = ctx.GetInt32(OptOutWindowDaysConfigKey) ?? DefaultOptOutWindowDays;

        // Register the family's schedule rule as the core-resolvable INotificationScheduleRule contract, so
        // it joins the set the core's NotificationSchedulePass enumerates each tick (exactly as the
        // orchestrator's ISagaModule registers its typed sink into the saga_type → sink registry). It
        // resolves the family-agnostic DepositReadClient from the container the host configures.
        services.AddSingleton<INotificationScheduleRule>(sp =>
            new MaturityReminderRule(sp.GetRequiredService<DepositReadClient>(), optOutWindowDays));
    }
}
