using System.Text.Json;
using Babelstone.ProductConfigs.Api;
using Babelstone.RateSheets;
using Babelstone.Telemetry;
using Babelstone.Telemetry.Hosting;
using OpenTelemetry.Logs;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);

// An unexpected failure (DB drop, serialization-failure, an unforeseen constraint) returns a
// structured ProblemDetails 500 rather than a bare connection-reset, so callers and operators see a
// typed error.
builder.Services.AddProblemDetails();

// OpenTelemetry tracing + logging (ADR-IC-007 Layer 1, Epic K.1): the same resource discipline as the
// rate-sheet deploy host (OBS-1 parity) — service.name + service.namespace=babelstone +
// deployment.environment — exporting over OTLP to the Collector. Tracing subscribes to the shared
// Babelstone.Engine activity source; logging ships the host's structured ILogger records (the 409
// conflict + unexpected-error events, BabelstoneEvents) down the same OTLP pipe so trace_id/span_id
// correlate a log to its trace. Environment resolution fails fast: no DOTNET_ENVIRONMENT /
// ASPNETCORE_ENVIRONMENT means the host refuses to boot (no assumed env).
builder.Services.AddOpenTelemetry()
    .ConfigureResource(resource => resource
        .AddService(BabelstoneResource.ProductConfigsApiServiceName)
        .AddAttributes(
        [
            new KeyValuePair<string, object>(BabelstoneResource.ServiceNamespaceKey, BabelstoneResource.ServiceNamespace),
            new KeyValuePair<string, object>(BabelstoneResource.DeploymentEnvironmentKey, BabelstoneResource.ResolveEnvironment()),
        ]))
    .WithTracing(tracing => tracing
        .AddSource(BabelstoneTelemetry.ActivitySourceName)
        // Npgsql's built-in query CLIENT spans (K.5): one span per database command the registry store
        // issues, on THIS same provider so they share the host resource + OTLP pipe (OBS-1 parity).
        .AddNpgsqlQueryTelemetry()
        .AddOtlpExporter())
    .WithLogging(
        logging => logging.AddOtlpExporter(),
        // Carry the human-readable rendered message and any logging scopes into the OTLP/Loki record —
        // without these the LogRecord ships only the message template + structured state, dropping the
        // formatted body an operator reads in Loki.
        options =>
        {
            options.IncludeFormattedMessage = true;
            options.IncludeScopes = true;
        });

// snake_case on the wire (product_config_version_id, pack_version, effective_from), matching the
// deployed YAML and the stored JSONB. The config body is a JsonObject node whose own keys are preserved
// verbatim (the naming policy renames DTO properties, not dynamic-node keys).
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
    options.SerializerOptions.PropertyNameCaseInsensitive = false;
});

// The product-config registry lives in the same PostgreSQL tier as the event log and the rate sheets
// (ADR-PC-009 §A2, ADR-PC-008 §S2).
var connectionString = builder.Configuration.GetConnectionString("ProductConfigs")
    ?? throw new InvalidOperationException(
        "ConnectionStrings:ProductConfigs is required (the PostgreSQL product_config_versions tier, ADR-PC-009 §A2).");

builder.Services.AddSingleton<IProductConfigVersionStore>(_ => new PostgresProductConfigVersionStore(connectionString));

var app = builder.Build();

// Turns any unhandled exception escaping a handler into a ProblemDetails response.
app.UseExceptionHandler();

app.MapPost("/v1/product-configs", DeployProductConfigEndpoint.HandleAsync);

app.Run();

// Exposed for WebApplicationFactory<Program> in the integration tests.
public partial class Program;
