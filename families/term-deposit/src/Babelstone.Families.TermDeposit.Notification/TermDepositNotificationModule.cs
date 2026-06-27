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

    /// <summary>The canonical (snake_case) pack-parameter name the pinned pack declares the opt-out-window
    /// width under (ADR-PC-007). The family looks it up family-side via
    /// <see cref="NotificationModuleContext.GetPackInt32"/>, so the notification core conveys the value
    /// generically and never names this term-deposit parameter (ADR-IC-019 §D1).</summary>
    public const string AutoRenewalOptoutWindowDaysPackParam = "auto_renewal_optout_window_days";

    /// <inheritdoc />
    public string FamilyName => Family;

    /// <inheritdoc />
    public void ConfigureServices(IServiceCollection services, NotificationModuleContext ctx)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(ctx);

        // The deposit-shaped read client is FAMILY-OWNED (ADR-IC-019 §D1) — a "deposit" that matures and has
        // tax withheld is term-deposit knowledge, so the client + its view types live in this contribution,
        // not the family-agnostic core. The family module registers it (the family → core arrow, §P2); the
        // core never names it. BaseAddress is the engine read endpoint the host conveyed on the context
        // (ADR-PC-027), normalised to a trailing "/" so the client's relative "v1/deposits/..." resolves.
        services.AddHttpClient<DepositReadClient>(client =>
            client.BaseAddress = new Uri(
                ctx.EngineBaseUrl.EndsWith('/') ? ctx.EngineBaseUrl : ctx.EngineBaseUrl + "/"));

        // The opt-out window is pack data (ADR-PC-007, ADR-PC-025 §2): source it from the instance-pinned
        // pack the host resolved, looked up by the canonical pack-parameter name (ctx.GetPackInt32) — never a
        // literal, and never a term-deposit-named field, in the notification core (ADR-IC-019 §D1). A config
        // override (OptOutWindowDaysConfigKey) is honoured next for operability, and the family-side
        // documented default is the ultimate fallback only when neither is present (bd babelstone-60n8.6).
        var optOutWindowDays =
            ctx.GetPackInt32(AutoRenewalOptoutWindowDaysPackParam)
            ?? ctx.GetInt32(OptOutWindowDaysConfigKey)
            ?? DefaultOptOutWindowDays;

        // The disclosure-template sets the instance-pinned pack declares (pack.yaml template_refs), conveyed
        // by the host as plain data (ADR-IC-019 §P2 — the core holds no pack type). The maturity rule
        // REQUIRES its template-set is in this set and fails loud otherwise (ADR-PC-025 §2 pinning).
        var packTemplateRefs = ctx.PackTemplateRefs;

        // Register the family's schedule rules as the core-resolvable INotificationScheduleRule contract, so
        // each joins the set the core's NotificationSchedulePass enumerates per tick (exactly as the
        // orchestrator's ISagaModule registers its typed sink into the saga_type → sink registry). They
        // resolve the family-owned DepositReadClient registered just above.
        services.AddSingleton<INotificationScheduleRule>(sp =>
            new MaturityReminderRule(sp.GetRequiredService<DepositReadClient>(), packTemplateRefs, optOutWindowDays));

        // The annual IRS-withholding statement rule (bd babelstone-q15c) — the sibling scheduler that reads
        // the withholding population (not the maturity calendar) and emits an idempotent SCHEDULED statement
        // per deposit per tax year. Same family-owned shape, registered alongside the maturity rule with zero
        // notification-core diff (the open/closed property ADR-IC-019 §D2 commits to).
        services.AddSingleton<INotificationScheduleRule>(sp =>
            new WithholdingStatementRule(sp.GetRequiredService<DepositReadClient>()));
    }
}
