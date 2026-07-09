using System.Text.Json;
using Babelstone.Packs;
using Babelstone.RateSheets;
using Babelstone.RateSheets.Api;
using Babelstone.Telemetry;
using Babelstone.Telemetry.Hosting;
using OpenTelemetry.Logs;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);

// An unexpected failure (DB drop, serialization-failure, an unforeseen constraint) returns a
// structured ProblemDetails 500 rather than a bare connection-reset, so callers and operators see
// a typed error.
builder.Services.AddProblemDetails();

// OpenTelemetry tracing + logging (ADR-IC-007 Layer 1, Epic K.1): the same resource discipline as
// the engine host (OBS-1 parity) — service.name + service.namespace=babelstone +
// deployment.environment — exporting over OTLP to the Collector (P1, which fans out to Tempo/Loki).
// Tracing subscribes to the shared Babelstone.Engine activity source, mirroring the engine host
// (OBS-1 parity); logging ships the host's structured ILogger records (the 409 conflict +
// unexpected-error events, BabelstoneEvents) down the same OTLP pipe so trace_id/span_id correlate
// a log to its trace (the document-06 / §P1 trace-to-log navigation). Environment resolution fails
// fast: no DOTNET_ENVIRONMENT / ASPNETCORE_ENVIRONMENT means the host refuses to boot (no assumed env).
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
        // Npgsql's built-in query CLIENT spans (K.5): one span per database command the
        // rate-sheet store issues, on THIS same provider so they share the host resource + OTLP pipe
        // (OBS-1 parity with the engine host) — never a second, parallel provider.
        .AddNpgsqlQueryTelemetry()
        .AddOtlpExporter())
    .WithLogging(
        logging => logging.AddOtlpExporter(),
        // Carry the human-readable rendered message and any logging scopes into the OTLP/Loki
        // record — without these the LogRecord ships only the message template + structured state,
        // dropping the formatted body an operator reads in Loki (the §P1 trace-to-log navigation).
        options =>
        {
            options.IncludeFormattedMessage = true;
            options.IncludeScopes = true;
        });

// snake_case on the wire (rate_sheet_version_id, principal_cents, tan_basis_points),
// matching the deployed YAML and the stored JSONB — the same shape RateSheetJson uses.
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
    options.SerializerOptions.PropertyNameCaseInsensitive = false;
});

// The rate sheets live in the same PostgreSQL tier as the event log (ADR-PC-008).
var connectionString = builder.Configuration.GetConnectionString("RateSheets")
    ?? throw new InvalidOperationException(
        "ConnectionStrings:RateSheets is required (the PostgreSQL rate_sheets tier, ADR-PC-008 §P1).");

builder.Services.AddSingleton<IRateSheetStore>(_ => new PostgresRateSheetStore(connectionString));
builder.Services.AddSingleton<RateSheetValidator>();

// Pack bounds (surface §2.5, ADR-PC-008): the ADR-PC-008 rate bound is read from the VERIFIED pack's
// parameters/constants.yaml (max_consumer_rate_bps) keyed on the sheet's pack_version (C.5), not a
// host config knob. A disk-backed HostPackStore is the walking-skeleton stand-in for the cosign-
// verifying OciPackStore (the same load-time/hot-path split); the host pre-loads the configured
// pack versions at startup so the deploy handler resolves on the pure hot path. An unloaded
// pack_version surfaces as a PackLoadException the handler maps to a clean 400 (never a 500).
var packStore = new HostPackStore(builder.Configuration["RateSheets:PacksDir"]);
var preloadVersions = builder.Configuration.GetSection("RateSheets:PackVersions").Get<string[]>()
    ?? ["pt.2026.1"];
foreach (var packVersion in preloadVersions)
{
    await packStore.GetAsync(packVersion);
}

builder.Services.AddSingleton<IPackStore>(packStore);
builder.Services.AddSingleton<IRateBoundsSource, PackRateBoundsSource>();

// INTERIM product-config registry (surface §2.5 cross-artefact invariants): no in-engine
// product-config registry exists until Epic E/F, so the default (EmptyProductConfigSource) reports
// no active configs and the cross-artefact checks pass vacuously — a sheet is judged on its
// self-contained shape alone, never rejected merely because the registry is unwired.
builder.Services.AddSingleton<IProductConfigSource, EmptyProductConfigSource>();

var app = builder.Build();

// One-shot deploy mode (bd babelstone-zla1.21): when RateSheets:DeployOnStartup names a JSON
// rate-sheet file — or a DIRECTORY of them — deploy each once through the SAME validated handler and
// exit. This is how the staging rate-sheet-deploy Job provisions ALL committed sheets on bring-up
// (adding a version/family is just committing its YAML). No HTTP listener starts in this mode; the
// process exit code is the Job's success signal (0 = every sheet deployed / idempotent no-op).
var deployOnStartup = builder.Configuration["RateSheets:DeployOnStartup"];
if (!string.IsNullOrWhiteSpace(deployOnStartup))
{
    return await OneShotDeploy.RunAsync(
        app.Services,
        deployOnStartup,
        builder.Configuration["RateSheets:DeployActor"] ?? "staging-bring-up");
}

// Turns any unhandled exception escaping a handler into a ProblemDetails response.
app.UseExceptionHandler();

app.MapPost("/v1/rate-sheets", DeployRateSheetEndpoint.HandleAsync);

app.Run();
return 0;

// Exposed for WebApplicationFactory<Program> in the integration tests.
public partial class Program;
