using System.Text.Json;
using Babelstone.Engine;
using Babelstone.Engine.Api;
using Babelstone.EventStore;
using Babelstone.Families.TermDeposit;
using Babelstone.Families.TermDeposit.Application;
using Babelstone.OutboxPublisher;
using Babelstone.Pii;
using Babelstone.RateSheets;
using Babelstone.Telemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);

// Typed ProblemDetails on any unhandled failure rather than a bare connection reset
// (mirrors RateSheets.Api).
builder.Services.AddProblemDetails();

// OpenTelemetry tracing (ADR-IC-007 Layer 1, Epic K.1): listen to the engine's manual span
// source (accrual.computed / withholding.applied, emitted in the AggregateRuntime shell) and
// export over OTLP to the Collector (P1 — never direct-to-backend). The resource stamps
// service.name + service.namespace=babelstone + deployment.environment so every trace is
// attributable (OBS-1). Environment resolution fails fast: a host with no DOTNET_ENVIRONMENT /
// ASPNETCORE_ENVIRONMENT refuses to boot rather than mis-attribute traces to an assumed env.
builder.Services.AddOpenTelemetry()
    .ConfigureResource(resource => resource
        .AddService(BabelstoneResource.EngineApiServiceName)
        .AddAttributes(
        [
            new KeyValuePair<string, object>(BabelstoneResource.ServiceNamespaceKey, BabelstoneResource.ServiceNamespace),
            new KeyValuePair<string, object>(BabelstoneResource.DeploymentEnvironmentKey, BabelstoneResource.ResolveEnvironment()),
        ]))
    .WithTracing(tracing => tracing
        .AddSource(BabelstoneTelemetry.ActivitySourceName)
        .AddOtlpExporter())
    // Metrics (ADR-IC-007 Layer 1 / ADR-IC-004 §P4): listen to the engine's meter and export over
    // OTLP to the Collector → Prometheus, where the §P4 warning/critical thresholds alert. The
    // publish-lag SLI (outbox_publish_lag_seconds) and per-row latency histogram are emitted by the
    // outbox relay (Babelstone.OutboxPublisher: OutboxLagObserver + OutboxDrainer), which THIS host
    // co-runs in-process — the relay's hosted service + the OutboxLagObserver singleton are
    // registered below, so the instruments AddMeter picks up here are actually produced in-process.
    .WithMetrics(metrics => metrics
        .AddMeter(BabelstoneTelemetry.MeterName)
        .AddOtlpExporter());

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

// The engine-instance's pinned pack(s) (ADR-PC-007 §P3/§P4, ADR-PC-009). Two modes,
// Engine:PackRegistry — never a silent fallback (HostPackLoading):
//   • 'oci' (production): the durable Postgres pack_versions registry resolves each pinned
//     version to its OCI coordinates; the cosign-verifying OciPackStore eager-loads EVERY pack
//     version any live instance references (events.pack_version) plus the configured primary,
//     fail-loud — a single unresolvable/unverifiable pack aborts startup (this await throws a
//     PackLoadException that escapes Main, so the process exits non-zero with the offending pin
//     logged at Critical, the §P4 fatal-on-load discipline).
//   • 'disk' (the default — what make up/compose and the host integration tests use): the
//     walking-skeleton on-disk structural parse, unchanged. The durable registry is opt-in so
//     existing dev wiring keeps booting; the dev path is an explicit OPT-OUT, not a fallback.
using var packLoadLoggerFactory = LoggerFactory.Create(logging => logging.AddConsole());
var packLoad = await HostPackLoading.LoadAsync(
    builder.Configuration,
    connectionString,
    packLoadLoggerFactory.CreateLogger("Babelstone.Engine.Api.PackLoad"));
var pack = packLoad.PrimaryPack;
// The hot-path pack store a handler resolves primitives/parameters against (pure, no I/O — every
// pack a live instance references was pre-loaded above, ADR-PC-007 §P4).
builder.Services.AddSingleton(packLoad.Store);

// Shared, family-agnostic infrastructure — composed once, resolved by every family module.
// The runtime owns the clock (ADR-PC-010 §P5); the host stamps a missing constituted_at/matured_at.
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<IRateSheetStore>(_ => new PostgresRateSheetStore(connectionString));
builder.Services.AddSingleton<ISettlementPort, LoggingSettlementPort>();
builder.Services.AddSingleton<IEventStore>(_ => new PostgresEventStore(connectionString));
builder.Services.AddSingleton<IEventSink>(serviceProvider =>
    new EventStoreSink(serviceProvider.GetRequiredService<IEventStore>()));
// ADR-PC-029 slot 4 (ENGINE_COMMAND_IDEMPOTENT): the command-ingress idempotency ledger's READ
// side — the pre-check a command endpoint consults BEFORE any side effect so an at-least-once
// retry from the saga dispatcher replays the original outcome. The WRITE side is the
// in-transaction command_dedup INSERT inside PostgresEventStore.AppendAsync (migration 0015).
builder.Services.AddSingleton<ICommandLog>(_ => new PostgresCommandLog(connectionString));
// D.2 projection runtime storage (ADR-PC-002 §P4): the byte-oriented projection + checkpoint
// stores are family-agnostic spine components (ADR-PC-021), backed by the same PostgreSQL tier as
// the event store. The family module composes the typed runtime (registry + drainer + relay) over
// them, so it resolves these rather than registering them; migrations 0010/0011 own the
// `projections` discriminator columns + the `projection_checkpoints` table they read/write.
builder.Services.AddSingleton<IProjectionStorage>(_ => new PostgresProjectionStore(connectionString));
builder.Services.AddSingleton<IProjectionCheckpointStore>(_ => new PostgresProjectionCheckpointStore(connectionString));
// D.4 CQRS read model (ADR-IC-005): the denormalized query surface on the SAME PostgreSQL tier.
// The read_model schema is FAMILY-OWNED (ADR-PC-021 family-owned ownership): the term-deposit
// family's own migration set (Babelstone.Families.TermDeposit.Application.Migrations,
// 0001_read_model.sql) creates read_model.deposits, applied by the family's
// ReadModelMigrationHostedService (TermDepositHostModule) — the engine event-store migrations carry
// zero family-named tables. The deposit-shaped table + the maturity range scan name one family's
// domain shape, so the store is FAMILY-OWNED too (ADR-PC-021 §D2/§P2): the engine spine exposes
// only the generic IReadModelStore<TRow> primitive, and the term-deposit family supplies its typed
// row + this Postgres store. The host (composition root) resolves the family store and composes the
// read-model runner over it (TermDepositHostModule), folding the same deposit-position state the
// live read path computes into the flat read-model row the I.2 Query API serves.
builder.Services.AddSingleton<IDepositReadModelStore>(_ => new PostgresDepositReadModelStore(connectionString));
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

// The IC-004 outbox→Redpanda relay (G.1), co-hosted in this process (event-store-skeleton §5.1).
// Two pieces register here, the same proven shape as the projection relay above:
//   • AddHostedService<OutboxRelayService> — the poll loop that drains PENDING rows to Redpanda and
//     records the per-row publish-latency histogram (a G.1 addition).
//   • AddSingleton<OutboxLagObserver> — the §P4 SLI itself: an observable gauge of the oldest
//     PENDING row's age, read fresh each metrics-collection cycle so it keeps climbing during an
//     outage when NOTHING publishes (exactly when the 30s-warn/5min-crit alerts must fire). It
//     owns a Meter named BabelstoneTelemetry.MeterName, so the .WithMetrics → AddMeter above
//     collects its gauge; resolving the singleton at startup is what registers the instrument.
// The Kafka bootstrap address is a broker ENDPOINT, not a credential — it is already plaintext in
// infra/compose.yaml and the k8s manifests — so it resolves straight from IConfiguration
// (Kafka:BootstrapServers via env/appsettings), distinct from ConnectionStrings:Engine which goes
// through the ISecretProvider credential boundary (ADR-PC-004 Amendment A1). When SASL credentials
// land later (the Redpanda secret the Program.cs §54 note anticipates) THOSE will resolve through
// ISecretProvider. The dev default matches the Redpanda external listener in infra/compose.yaml
// (localhost:19092), the same convention `make up` exposes.
var bootstrapServers = builder.Configuration["Kafka:BootstrapServers"] ?? "localhost:19092";
builder.Services.AddSingleton(new OutboxRelayOptions
{
    ConnectionString = connectionString,
    BootstrapServers = bootstrapServers,
});
builder.Services.AddSingleton(serviceProvider =>
    new OutboxDrainer(serviceProvider.GetRequiredService<OutboxRelayOptions>()));
builder.Services.AddHostedService<OutboxRelayService>();
builder.Services.AddSingleton(new OutboxLagObserver(connectionString));

var app = builder.Build();

// Resolve the lag observer eagerly so its observable gauge is created BEFORE the first
// metrics-collection cycle — a lazily-resolved singleton would not register the §P4 instrument
// until something asked for it. The hosted relay starts on its own via the host lifecycle.
app.Services.GetRequiredService<OutboxLagObserver>();

app.UseExceptionHandler();
foreach (var module in familyModules)
{
    module.MapEndpoints(app);
}

app.Run();

// Exposed for WebApplicationFactory<Program> in the integration tests.
public partial class Program;
