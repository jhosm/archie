using System.Text.Json;
using Babelstone.Engine;
using Babelstone.Engine.Api;
using Babelstone.EventStore;
using Babelstone.Families.TermDeposit;
using Babelstone.Families.TermDeposit.Application;
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

var connectionString = builder.Configuration.GetConnectionString("Engine")
    ?? throw new InvalidOperationException(
        "ConnectionStrings:Engine is required (the PostgreSQL event-store tier, ADR-PC-001 §P1).");

// The engine-instance's pinned pack (ADR-PC-009): a disk-loaded VerifiedPack stands in for the
// in-engine OCI loader + per-instance pinning registry on the walking-skeleton dev boundary.
var packVersion = builder.Configuration.GetValue("Engine:PackVersion", "pt.2026.1");
var pack = HostPack.Load(builder.Configuration["Engine:PacksDir"], packVersion);

// The runtime owns the clock (ADR-PC-010 §P5); the host stamps a missing constituted_at/matured_at.
builder.Services.AddSingleton(TimeProvider.System);

builder.Services.AddSingleton<IRateSheetStore>(_ => new PostgresRateSheetStore(connectionString));
builder.Services.AddSingleton<ISettlementPort, LoggingSettlementPort>();

// The durable runtime over the term-deposit family. Uses the JSON dev codec; the Avro +
// Schema-Registry codec (E.4, Babelstone.Engine.Avro) is the production wiring follow-up.
builder.Services.AddSingleton(serviceProvider =>
{
    var store = new PostgresEventStore(connectionString);
    return new AggregateRuntime<DepositPosition>(
        store,
        new EventStoreSink(store),
        TermDepositFamilyModule.Registry(),
        new JsonEventSerializer(),
        new NullPiiProtector(),
        serviceProvider.GetRequiredService<TimeProvider>(),
        () => DepositPosition.Empty);
});

// The term-deposit decider (ADR-PC-021): the host is its composition root (§D4).
builder.Services.AddSingleton(serviceProvider => new TermDepositConstitutionService(
    serviceProvider.GetRequiredService<AggregateRuntime<DepositPosition>>(),
    serviceProvider.GetRequiredService<IRateSheetStore>(),
    serviceProvider.GetRequiredService<ISettlementPort>(),
    pack,
    dayCountPrimitive: "act_360",
    withholdingPrimitive: "irs_juros"));

var app = builder.Build();

app.UseExceptionHandler();
DepositsEndpoints.Map(app);

app.Run();

// Exposed for WebApplicationFactory<Program> in the integration tests.
public partial class Program;
