using Babelstone.Packs;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Babelstone.Engine.Hosting;

/// <summary>
/// A family's contribution to the engine host's composition (ADR-PC-021 §D4 "composition at the
/// edge" / §P4 "composition is discovery at the host/test edge"). The host enumerates the modules
/// it runs and lets each one register its own closed-generic <c>AggregateRuntime&lt;TState&gt;</c> +
/// decider (<see cref="ConfigureServices"/>) and map its own HTTP surface (<see cref="MapEndpoints"/>).
///
/// The point: the host's compose block stays family-count-invariant — a fixed loop over the
/// registered modules. Adding a family is a new module + one registration entry (and the host's
/// per-family <c>ProjectReference</c>), never a surgical edit threading a new aggregate type
/// through <c>Program.cs</c>. Because the family owns its own <c>AggregateRuntime&lt;TState&gt;</c>
/// construction here, the host never names a family aggregate type.
///
/// This interface lives in the shared hosting-contract assembly <c>Babelstone.Engine.Hosting</c>
/// (ADR-PC-021 §A1, relocated 2026-06-20) — NOT in the host
/// <c>Babelstone.Engine.Api</c> as originally, and never in the generic engine spine. A family's
/// <c>.Application</c> project can reference this contract assembly to implement its own module
/// without a <c>family → host</c> cycle, while the <c>family → engine</c> arrow stays one-way
/// (§D2/§P2, the <c>ENGINE_FAMILY_AGNOSTIC</c> fitness function). The hosting-contract assembly,
/// like the host, MAY name a family in principle but by design does not — only the spine libraries
/// referencing a family is the forbidden edge.
///
/// Today the host holds an explicit list of modules (ADR-PC-021 §P4 "Option A"); because every
/// module implements this same contract with a public parameterless ctor, swapping the explicit
/// list for <see cref="FamilyModuleLoader"/>-style assembly-scan discovery later is a localized
/// change to the host's discovery loop with zero change to families.
/// </summary>
public interface IFamilyHostModule
{
    /// <summary>The family this module composes (e.g. <c>"term_deposit"</c>) — diagnostics and collision checks.</summary>
    string FamilyName { get; }

    /// <summary>
    /// The family schema version this module composes (e.g. <c>"term_deposit@2026.1"</c>) — the SAME value
    /// the family's <c>IFamilyModule.SchemaVersion</c> stamps onto every <c>EventEnvelope</c> (ADR-PC-009 §P1).
    /// The host's <c>HostModuleLoader</c> cross-checks this against the pinned pack's family-manifest at load
    /// and FAILS CLOSED on a skew — a newer-than-pinned module is an audit/replay hazard.
    /// </summary>
    string SchemaVersion { get; }

    /// <summary>
    /// The event-envelope <c>aggregate_type</c> / bus topic this family writes under — by the engine's
    /// documented convention this equals <see cref="FamilyName"/> (ADR-IC-004 §Consequences). Carried so the
    /// load-time cross-check against the pack family-manifest is over the full pinned tuple
    /// (family_name, aggregate_type, schema_version), not a subset.
    /// </summary>
    string AggregateType { get; }

    /// <summary>
    /// Register the family's runtime + decider into DI. The family owns the closed generic
    /// <c>AggregateRuntime&lt;TState&gt;</c> (and its <c>() =&gt; TState.Empty</c> seed and fold
    /// registry) here. Shared, family-agnostic infrastructure — the event store, codec, PII
    /// protector, clock, rate-sheet store, settlement port — is resolved from the container being
    /// configured; the per-deployment pinned pack and configuration arrive via <paramref name="ctx"/>.
    /// </summary>
    void ConfigureServices(IServiceCollection services, FamilyHostContext ctx);

    /// <summary>Map the family's command/query endpoints onto the host's routing surface.</summary>
    void MapEndpoints(IEndpointRouteBuilder app);
}

/// <summary>
/// The per-deployment ingredients the host hands each <see cref="IFamilyHostModule"/> at
/// composition time that are NOT registered as DI services: the engine-instance's pinned
/// regulatory <see cref="VerifiedPack"/> (shared by every family on the instance, ADR-PC-009),
/// the configuration root (for per-family settings a module wants to read), and the host's
/// already-secret-resolved engine connection string (<see cref="EngineConnectionString"/>) so a
/// family module can register its OWN family-owned Postgres store without re-crossing the
/// <c>ISecretProvider</c> boundary — the host resolves the credential once at the composition root
/// (ADR-PC-004 Amendment A1) and hands the resolved value here. Family-agnostic services are
/// resolved from the <see cref="IServiceCollection"/> the module is configuring.
/// </summary>
public sealed record FamilyHostContext(
    VerifiedPack Pack, IConfiguration Configuration, string EngineConnectionString);
