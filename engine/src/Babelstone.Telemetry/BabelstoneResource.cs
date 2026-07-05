namespace Babelstone.Telemetry;

/// <summary>
/// The shared OTel <i>resource</i> contract (ADR-IC-007): the stable attribute keys and values
/// every Babelstone .NET host stamps on its tracer's resource so traces are attributable to a
/// service, the <c>babelstone</c> namespace, and a deployment environment. Kept SDK-free (plain
/// strings) so it lives in the engine-spine-referenceable Telemetry project; the host turns these
/// into a <c>ResourceBuilder</c> via <c>AddService(serviceName).AddAttributes([..])</c>, and the
/// OBS-1 fitness test (<c>OBS_RESOURCE_ATTRS</c>) reproduces the identical resource from these
/// same values — so the keys/values cannot drift between host and test.
/// </summary>
public static class BabelstoneResource
{
    /// <summary>OTel resource attribute key for the service namespace (semantic-convention key).</summary>
    public const string ServiceNamespaceKey = "service.namespace";

    /// <summary>OTel resource attribute key for the deployment environment (semantic-convention key).</summary>
    public const string DeploymentEnvironmentKey = "deployment.environment";

    /// <summary>Every Babelstone host shares this namespace, so traces group under one estate.</summary>
    public const string ServiceNamespace = "babelstone";

    /// <summary>The engine command/query host's <c>service.name</c> (ADR-PC-021).</summary>
    public const string EngineApiServiceName = "babelstone-engine-api";

    /// <summary>The rate-sheet deploy host's <c>service.name</c> (ADR-PC-008).</summary>
    public const string RateSheetsApiServiceName = "babelstone-rate-sheets-api";

    /// <summary>The product-config deploy host's <c>service.name</c> (ADR-PC-009 §A2 / ADR-PC-008). The
    /// sibling of the rate-sheet deploy host for the product-config artefact family: the treasury/product
    /// gated <c>POST /v1/product-configs</c> versioned deploy registry.</summary>
    public const string ProductConfigsApiServiceName = "babelstone-product-configs-api";

    /// <summary>The saga orchestrator host's <c>service.name</c> (ADR-IC-018 composition root).
    /// Its saga-advance spans (opened on the SHARED <c>Babelstone.Engine</c> source) carry this
    /// service identity, so a saga trace shows the orchestrator and engine as distinct services
    /// under one estate namespace.</summary>
    public const string OrchestratorServiceName = "babelstone-orchestrator";

    /// <summary>The notification worker host's <c>service.name</c> (ADR-IC-011 / ADR-IC-013 in-house
    /// estate). The per-service outbox worker that reads the engine's term-deposit projections over
    /// the ADR-IC-005 read surface; its spans (opened on the SHARED <c>Babelstone.Engine</c> source)
    /// carry this identity, so the notification service shows up as a distinct service under the one
    /// estate namespace alongside the engine and orchestrator.</summary>
    public const string NotificationServiceName = "babelstone-notification";

    /// <summary>The lifecycle-command driver host's <c>service.name</c> (ADR-PC-036 §Decision 2 /
    /// ADR-IC-011 / ADR-IC-013 in-house estate). The clock-owning sibling worker that ticks a cadence,
    /// finds the lifecycle steps due as-of today, and POSTs each to the engine's ADR-PC-029 command
    /// surface; its spans (the per-tick <c>cadence.pass</c> and the per-command <c>lifecycle.dispatch</c>,
    /// opened on the SHARED <c>Babelstone.Engine</c> source) carry this identity, so the driver shows up
    /// as a distinct service under the one estate namespace alongside the engine, orchestrator and
    /// notification worker.</summary>
    public const string LifecycleServiceName = "babelstone-lifecycle";

    /// <summary>
    /// Resolves <c>deployment.environment</c> from <c>DOTNET_ENVIRONMENT</c>, then
    /// <c>ASPNETCORE_ENVIRONMENT</c>. <b>Fails fast</b>: when neither variable is set (or both are
    /// blank), this throws rather than defaulting — a host must not start with traces silently
    /// mis-attributed to an assumed environment. ADR-IC-007 requires a <i>non-blank</i>
    /// <c>deployment.environment</c>; we satisfy it by refusing to boot without an explicit one.
    /// </summary>
    /// <exception cref="InvalidOperationException">Neither environment variable is set to a non-blank value.</exception>
    public static string ResolveEnvironment()
    {
        var fromDotnet = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT");
        if (!string.IsNullOrWhiteSpace(fromDotnet))
        {
            return fromDotnet;
        }

        var fromAspNet = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");
        if (!string.IsNullOrWhiteSpace(fromAspNet))
        {
            return fromAspNet;
        }

        throw new InvalidOperationException(
            "deployment.environment is unresolved: set DOTNET_ENVIRONMENT or ASPNETCORE_ENVIRONMENT. " +
            "Babelstone hosts fail fast rather than mis-attribute traces to a default environment (ADR-IC-007 §P1).");
    }
}
