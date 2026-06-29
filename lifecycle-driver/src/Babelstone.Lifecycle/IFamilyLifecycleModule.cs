using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Babelstone.Lifecycle;

/// <summary>
/// A family's contribution to the lifecycle-command driver host's composition (ADR-PC-036;
/// ADR-PC-021 "composition is discovery at the host edge"; the family → core arrow, ADR-IC-019).
/// In plain terms: the generic driver owns the clock, the dedupe ledger and the command-POST sink, but it must
/// NOT know that a term deposit "matures" or that a personal loan owes a monthly "installment". Each family
/// ships ONE of these — a tiny plug-in that registers its own family-owned read-model store and its own
/// <see cref="ILifecycleCommandRule"/> into DI — and the host discovers and composes them by assembly-scan, so
/// adding a clock-driven lifecycle to a new family touches no host code.
/// </summary>
/// <remarks>
/// <para>
/// It is the lifecycle-side sibling of the engine's <c>IFamilyHostModule</c> (ADR-PC-021) and the
/// notification core's <c>IFamilyNotificationModule</c> (ADR-IC-019): the host (the
/// composition root — the only place that MAY name a family) enumerates the registered modules and
/// calls <see cref="ConfigureServices"/> on each; the driver core (<c>Babelstone.Lifecycle</c>) names no
/// family, so it carries no <c>families/**</c> reference (the family → core arrow stays one-way). The host's
/// <c>LifecycleModuleLoader</c> scans the <c>Babelstone.Families.*</c> assemblies shipped beside it for these
/// modules and FAILS LOUD on a duplicate <see cref="FamilyName"/> — two modules composing the same family
/// would double-register its rule + store.
/// </para>
/// <para>
/// Because every module implements this same contract with a public parameterless constructor, discovery is
/// pure assembly-scan: a new family's clock-driven lifecycle is its own <c>.Lifecycle</c> module + the host's
/// <c>ProjectReference</c> to it (so its dll lands beside the host for the scan) — never an edit to the host's
/// composition (ADR-PC-036 "a fourth rule with zero core diff").
/// </para>
/// </remarks>
public interface IFamilyLifecycleModule
{
    /// <summary>The family this module composes lifecycle commands for (e.g. <c>"term_deposit"</c>,
    /// <c>"personal_loan"</c>) — for diagnostics and the host loader's duplicate-family collision check.</summary>
    string FamilyName { get; }

    /// <summary>
    /// Register the family's lifecycle contribution into DI: its own family-owned read-model store (behind the
    /// family-agnostic store interface its rule depends on) and its <see cref="ILifecycleCommandRule"/>, which
    /// joins the set the core's <see cref="LifecycleSchedulePass"/> enumerates each tick. The family module is
    /// the place that names the concrete Npgsql store (ADR-IC-019): the driver core stays storage-agnostic
    /// and never names a family. The per-deployment read-model connection arrives via <paramref name="ctx"/>.
    /// </summary>
    void ConfigureServices(IServiceCollection services, LifecycleModuleContext ctx);
}

/// <summary>
/// The per-deployment ingredients the host hands each <see cref="IFamilyLifecycleModule"/> at composition time
/// that are NOT registered as DI services: the host's already-secret-resolved engine read-model connection
/// string (<see cref="ReadModelConnectionString"/>) so a family module can register its OWN family-owned
/// Postgres read store without re-crossing the secret boundary, and the configuration root (for any per-family
/// settings a module wants to read). It is the lifecycle-side mirror of the engine's <c>FamilyHostContext</c>
/// and the notification core's <c>NotificationModuleContext</c>. Family-agnostic services are resolved from the
/// <see cref="IServiceCollection"/> the module is configuring.
/// </summary>
/// <param name="Configuration">The host configuration root (for per-family settings a module reads by key).</param>
/// <param name="ReadModelConnectionString">The engine read-model database the family rules range-scan
/// (ADR-IC-005 read-model tier) — the host resolves it once at the composition root and conveys the value here
/// so each family module backs its store interface with its own Npgsql implementation.</param>
public sealed record LifecycleModuleContext(
    IConfiguration Configuration,
    string ReadModelConnectionString)
{
    /// <summary>Read an integer configuration value (e.g. a per-family tuning knob), or <see langword="null"/>
    /// if unset/unparsable — the caller supplies its family-side default.</summary>
    public int? GetInt32(string key) =>
        int.TryParse(Configuration[key], out var value) ? value : null;
}
