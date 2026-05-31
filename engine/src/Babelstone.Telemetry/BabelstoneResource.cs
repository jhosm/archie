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

    /// <summary>The environment used when neither environment variable is set. Never throws.</summary>
    public const string DefaultEnvironment = "development";

    /// <summary>The engine command/query host's <c>service.name</c> (ADR-PC-021 §D5).</summary>
    public const string EngineApiServiceName = "babelstone-engine-api";

    /// <summary>The rate-sheet deploy host's <c>service.name</c> (ADR-PC-008 §P2).</summary>
    public const string RateSheetsApiServiceName = "babelstone-rate-sheets-api";

    /// <summary>
    /// Resolves <c>deployment.environment</c> from <c>DOTNET_ENVIRONMENT</c>, then
    /// <c>ASPNETCORE_ENVIRONMENT</c>, defaulting to <see cref="DefaultEnvironment"/>. Never throws —
    /// a missing or blank value falls back to the default so host startup cannot fail on telemetry.
    /// </summary>
    public static string ResolveEnvironment()
    {
        var fromDotnet = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT");
        if (!string.IsNullOrWhiteSpace(fromDotnet))
        {
            return fromDotnet;
        }

        var fromAspNet = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");
        return string.IsNullOrWhiteSpace(fromAspNet) ? DefaultEnvironment : fromAspNet;
    }
}
