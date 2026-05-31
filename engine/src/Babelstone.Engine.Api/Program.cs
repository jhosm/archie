using System.Text.Json;
using Babelstone.Engine;
using Babelstone.Engine.Api;
using Babelstone.EventStore;
using Babelstone.Pii;
using Babelstone.RateSheets;

var builder = WebApplication.CreateBuilder(args);

// Typed ProblemDetails on any unhandled failure rather than a bare connection reset
// (mirrors RateSheets.Api). Structured logging + OpenTelemetry is the ADR-IC-007 follow-up.
builder.Services.AddProblemDetails();

// snake_case on the wire (principal_cents, tan_basis_points, rate_sheet_version_id), money as
// integer cents — the same discipline as RateSheets.Api and the deposit configuration surface.
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
    options.SerializerOptions.PropertyNameCaseInsensitive = false;
});

// Application / integration credentials (the DB connection string today, Redpanda SASL
// later) resolve through the ISecretProvider boundary (ADR-PC-004 Amendment A1) — distinct
// from the per-subject PII transit keys (IPiiKeyStore). Default to the configuration-backed
// provider so `make up` keeps working with existing config; opt into OpenBao KV v2 with
// OpenBao:Enabled=true. The resolved credential stays at this composition root: never on a
// saga message (ADR-IC-003 §P7) nor the durable bus (ADR-PC-004 §P2).
ISecretProvider secretProvider = builder.Configuration.GetValue<bool>("OpenBao:Enabled")
    ? new OpenBaoKvSecretProvider(
        new HttpClient { BaseAddress = new Uri(builder.Configuration["OpenBao:Address"] ?? "http://localhost:8200/") },
        roleId: builder.Configuration["OpenBao:RoleId"]
            ?? throw new InvalidOperationException("OpenBao:Enabled is set but OpenBao:RoleId is missing."),
        secretId: builder.Configuration["OpenBao:SecretId"]
            ?? throw new InvalidOperationException("OpenBao:Enabled is set but OpenBao:SecretId is missing."),
        mountPath: builder.Configuration["OpenBao:MountPath"] ?? "secret")
    : new ConfigurationSecretProvider(builder.Configuration);
builder.Services.AddSingleton(secretProvider);

string connectionString;
try
{
    connectionString = await secretProvider.GetSecretAsync("Engine");
}
catch (SecretProviderException)
{
    // A missing/empty credential is the same failure mode as the original null check;
    // preserve the exact ADR-PC-001 §P1 contract message.
    throw new InvalidOperationException(
        "ConnectionStrings:Engine is required (the PostgreSQL event-store tier, ADR-PC-001 §P1).");
}

// The engine-instance's pinned pack (ADR-PC-009): a disk-loaded VerifiedPack stands in for the
// in-engine OCI loader + per-instance pinning registry on the walking-skeleton dev boundary.
var packVersion = builder.Configuration.GetValue("Engine:PackVersion", "pt.2026.1");
var pack = HostPack.Load(builder.Configuration["Engine:PacksDir"], packVersion);

// Shared, family-agnostic infrastructure — composed once, resolved by every family module.
// The runtime owns the clock (ADR-PC-010 §P5); the host stamps a missing constituted_at/matured_at.
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<IRateSheetStore>(_ => new PostgresRateSheetStore(connectionString));
builder.Services.AddSingleton<ISettlementPort, LoggingSettlementPort>();
builder.Services.AddSingleton<IEventStore>(_ => new PostgresEventStore(connectionString));
builder.Services.AddSingleton<IEventSink>(serviceProvider =>
    new EventStoreSink(serviceProvider.GetRequiredService<IEventStore>()));
// The JSON dev codec + null PII protector; the Avro + Schema-Registry codec (E.4,
// Babelstone.Engine.Avro) is the production wiring follow-up.
builder.Services.AddSingleton<IEventSerializer, JsonEventSerializer>();
builder.Services.AddSingleton<IPiiProtector, NullPiiProtector>();

// Composition at the edge (ADR-PC-021 §D4/§P4): the host enumerates the families it runs as
// IFamilyHostModule contributions and lets each register its own runtime + decider and map its
// own endpoints. This compose block stays family-count-invariant — adding a family is a new
// module + a ProjectReference + one entry in the list below, never a surgical edit threading a
// new aggregate type through here. Today this is the explicit list (§P4 "Option A"); because
// every module shares the IFamilyHostModule contract, swapping it for FamilyModuleLoader-style
// assembly-scan discovery later is a localized change here, with zero change to families.
var familyHostContext = new FamilyHostContext(pack, builder.Configuration);
IReadOnlyList<IFamilyHostModule> familyModules = [new TermDepositHostModule()];
foreach (var module in familyModules)
{
    module.ConfigureServices(builder.Services, familyHostContext);
}

var app = builder.Build();

app.UseExceptionHandler();
foreach (var module in familyModules)
{
    module.MapEndpoints(app);
}

app.Run();

// Exposed for WebApplicationFactory<Program> in the integration tests.
public partial class Program;
