using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Babelstone.Notification;

/// <summary>
/// A family's contribution to the notification core's composition (ADR-IC-019 §D4/§P4, Amendment
/// 2026-06-24) — the notification-side mirror of the engine's <c>IFamilyHostModule</c> (ADR-PC-021 §A1)
/// and the orchestrator's <c>ISagaModule</c> (ADR-IC-018 §D4). In plain terms: the generic notification
/// core owns the poll loop, the dedupe ledger, the composite-id, the read client and the outbox, but it
/// must NOT know that a <em>term deposit</em> has a 14-day pre-maturity window or that its reminder uses
/// the <c>pt.notice.maturity</c> template. Those family-shaped decisions live in a per-family module that
/// plugs into the core here.
/// </summary>
/// <remarks>
/// Following the established module shape, this carries an identity and a <see cref="ConfigureServices"/>
/// hook through which the family registers its own <see cref="INotificationScheduleRule"/> — exactly as
/// <c>ISagaModule.ConfigureServices</c> registers a family's sink/router/status-map. The host (the §D4
/// composition root) loops over the registered modules calling <see cref="ConfigureServices"/>; the core
/// then resolves the registered rules and runs each per tick. The dependency arrow is <b>family → core</b>
/// (§P2): the core names no family, and the host is the only place that does (the §A2 exemption). The host
/// holds an explicit list now (ADR-PC-021 §A3 — explicit-list-now, assembly-scan-later); because every
/// module is a public-parameterless-ctor type, swapping the list for a <c>FamilyModuleLoader</c>-style scan
/// later is a host-only change with zero family diff.
/// </remarks>
public interface IFamilyNotificationModule
{
    /// <summary>The family this module schedules notifications for (e.g. <c>"term_deposit"</c>) —
    /// diagnostics and the host's duplicate-family collision check (cf. <c>ISagaModule.SagaType</c>).</summary>
    string FamilyName { get; }

    /// <summary>
    /// Register the family-owned notification services — its <see cref="INotificationScheduleRule"/>(s) —
    /// into the host container. The family sources its own pack/configuration parameters (e.g. the
    /// term-deposit opt-out-window width) from <paramref name="ctx"/>, never from a literal in the core.
    /// Family-agnostic services (the <see cref="DepositReadClient"/>, the dedupe ledger) are resolved from
    /// the container being configured.
    /// </summary>
    void ConfigureServices(IServiceCollection services, NotificationModuleContext ctx);
}

/// <summary>
/// The family-owned component the core's loop enumerates each tick (registered via
/// <see cref="IFamilyNotificationModule.ConfigureServices"/>). It owns the two genuinely family-shaped
/// decisions — <em>which instances are due as-of a date</em> and <em>which <c>template_ref</c> + structural
/// data the due notice carries</em> — and returns them as <see cref="ReminderDecision"/>s. It does NOT own
/// the composite-id derivation or the dedupe: those are core primitives the core applies to every decision
/// (ADR-IC-019 §D2), so a family rule never reimplements idempotency.
/// </summary>
public interface INotificationScheduleRule
{
    /// <summary>The family this rule belongs to (e.g. <c>"term_deposit"</c>) — for diagnostics.</summary>
    string FamilyName { get; }

    /// <summary>
    /// Produce the reminders that are due as-of <paramref name="asOf"/> (supplied by the core's clock-owning
    /// worker loop — ADR-PC-023 §6, never read inside the rule, so the rule is a deterministic function of
    /// the date and trivially testable). The core stamps each returned decision with its composite
    /// <c>notification_id</c> and dedupes it, so a rule may return the same decision on every pass without
    /// double-notifying (ADR-PC-025 slot 4).
    /// </summary>
    Task<IReadOnlyList<ReminderDecision>> EvaluateAsync(DateOnly asOf, CancellationToken ct = default);
}

/// <summary>
/// The per-deployment ingredients the host hands each <see cref="IFamilyNotificationModule"/> at
/// composition time — the notification-side <c>FamilyHostContext</c> (ADR-PC-021 §A1). Carries the host
/// configuration root, the engine read endpoint, and the values the host resolved from the instance-pinned
/// regulatory pack: the declared disclosure-template refs and the opt-out-window parameter.
/// </summary>
/// <remarks>
/// It deliberately carries <b>no <c>VerifiedPack</c></b>: that type lives in the engine spine
/// (<c>Babelstone.Packs</c>) the notification core may not reference (ADR-IC-019 §P2). So the host (the §A2
/// composition root, which MAY reference the spine) resolves the pinned pack and conveys the needed values
/// here as PLAIN CLR data (a string list, an <see cref="int"/>) — the core never names a pack type and the
/// <c>NOTIFICATION_FAMILY_AGNOSTIC</c> gate stays green (bd babelstone-60n8.6). A family module reads
/// <see cref="PackTemplateRefs"/> / <see cref="AutoRenewalOptoutWindowDays"/> to source its template-set and
/// window from the pinned pack rather than a family-side constant (ADR-PC-025 §2 pinning). Family-agnostic
/// services are resolved from the <see cref="IServiceCollection"/> the module is configuring.
/// </remarks>
/// <param name="Configuration">The host configuration root.</param>
/// <param name="EngineBaseUrl">The ADR-PC-027 deposit read-surface base URL.</param>
/// <param name="PackTemplateRefs">The instance-pinned pack's declared disclosure-template file refs
/// (<c>pack.yaml</c> <c>template_refs</c>, e.g. <c>notices</c>) — empty when no pack/templates are configured.
/// A family module requires its template-set is in this set and fails loud otherwise.</param>
/// <param name="AutoRenewalOptoutWindowDays">The instance-pinned pack's
/// <c>parameters/constants.yaml</c> <c>auto_renewal_optout_window_days</c>, or <see langword="null"/> when no
/// pack is configured — the family module supplies its documented default as the fallback.</param>
public sealed record NotificationModuleContext(
    IConfiguration Configuration,
    string EngineBaseUrl,
    IReadOnlyList<string> PackTemplateRefs,
    int? AutoRenewalOptoutWindowDays)
{
    /// <summary>Read an integer configuration value (e.g. a config-override window width), or
    /// <see langword="null"/> if unset/unparsable — the caller supplies its family-side default.</summary>
    public int? GetInt32(string key) =>
        int.TryParse(Configuration[key], out var value) ? value : null;
}
