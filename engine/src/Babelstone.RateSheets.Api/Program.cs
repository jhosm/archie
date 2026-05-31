using System.Text.Json;
using Babelstone.RateSheets;
using Babelstone.RateSheets.Api;
using Babelstone.Telemetry;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);

// An unexpected failure (DB drop, serialization-failure, an unforeseen constraint) returns a
// structured ProblemDetails 500 rather than a bare connection-reset, so callers and operators see
// a typed error.
builder.Services.AddProblemDetails();

// OpenTelemetry tracing (ADR-IC-007 Layer 1, Epic K.1): the same resource discipline as the
// engine host (OBS-1 parity) — service.name + service.namespace=babelstone + deployment.environment
// — exporting over OTLP to the Collector (P1). It listens to the shared Babelstone.Engine source so
// any manual span this host opens later is captured. Environment resolution fails fast: no
// DOTNET_ENVIRONMENT / ASPNETCORE_ENVIRONMENT means the host refuses to boot (no assumed env).
builder.Services.AddOpenTelemetry()
    .ConfigureResource(resource => resource
        .AddService(BabelstoneResource.RateSheetsApiServiceName)
        .AddAttributes(
        [
            new KeyValuePair<string, object>(BabelstoneResource.ServiceNamespaceKey, BabelstoneResource.ServiceNamespace),
            new KeyValuePair<string, object>(BabelstoneResource.DeploymentEnvironmentKey, BabelstoneResource.ResolveEnvironment()),
        ]))
    .WithTracing(tracing => tracing
        .AddSource(BabelstoneTelemetry.ActivitySourceName)
        .AddOtlpExporter());

// snake_case on the wire (rate_sheet_version_id, principal_cents, tan_basis_points),
// matching the deployed YAML and the stored JSONB — the same shape RateSheetJson uses.
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
    options.SerializerOptions.PropertyNameCaseInsensitive = false;
});

// The rate sheets live in the same PostgreSQL tier as the event log (ADR-PC-008 §P1).
var connectionString = builder.Configuration.GetConnectionString("RateSheets")
    ?? throw new InvalidOperationException(
        "ConnectionStrings:RateSheets is required (the PostgreSQL rate_sheets tier, ADR-PC-008 §P1).");

builder.Services.AddSingleton<IRateSheetStore>(_ => new PostgresRateSheetStore(connectionString));
builder.Services.AddSingleton<RateSheetValidator>();

// INTERIM pack bounds (surface §2.5): a configured ceiling, defaulting to pt.2026.1's
// max_consumer_rate_bps = 2000. Replace with verified-pack-derived bounds when C.5 lands.
var minBps = builder.Configuration.GetValue("RateSheets:MinBasisPoints", 0);
var maxBps = builder.Configuration.GetValue("RateSheets:MaxBasisPoints", 2000);
builder.Services.AddSingleton<IRateBoundsSource>(
    new ConfiguredRateBoundsSource(new RateBounds(minBps, maxBps)));

var app = builder.Build();

// Turns any unhandled exception escaping a handler into a ProblemDetails response.
app.UseExceptionHandler();

app.MapPost("/v1/rate-sheets", DeployRateSheetEndpoint.HandleAsync);

app.Run();

// Exposed for WebApplicationFactory<Program> in the integration tests.
public partial class Program;
