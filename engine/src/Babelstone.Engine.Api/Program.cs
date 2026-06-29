using System.Text.Json;
using Babelstone.Engine;
using Babelstone.Engine.Api;
using Babelstone.Engine.Avro;
using Babelstone.Engine.Hosting;
using Babelstone.EventStore;
using Babelstone.OutboxPublisher;
using Babelstone.Packs;
using Babelstone.Pii;
using Babelstone.RateSheets;
using Babelstone.Telemetry;
using Babelstone.Telemetry.Hosting;
using OpenTelemetry.Logs;
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
// attributable (OBS_RESOURCE_ATTRS). Environment resolution fails fast: a host with no DOTNET_ENVIRONMENT /
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
        // The inbound HTTP request becomes a SERVER span. When the caller
        // sends a W3C traceparent (ADR-IC-007 Layer 1 — traceparent is the join), that span adopts
        // its trace id, so the manual deposit.* spans started in the endpoints (children of
        // Activity.Current) nest under THIS trace instead of each becoming its own root. With no
        // traceparent on the wire the server span starts a fresh trace and the deposit.* spans nest
        // under it all the same — either way the request's work is one connected trace.
        .AddAspNetCoreInstrumentation()
        .AddSource(BabelstoneTelemetry.ActivitySourceName)
        // Npgsql's built-in query CLIENT spans (K.5): one span per database command across
        // every engine Postgres call (event-store appends, outbox drain + lag observer,
        // projection/checkpoint stores), registered on THIS same provider so they nest under the
        // request's server span and the manual deposit.* spans — never a second, parallel provider.
        .AddNpgsqlQueryTelemetry()
        // The runtime no-PII guard (OBS_NO_PII_ATTRS / ADR-IC-007 §P4): strips any span tag
        // whose key is outside the admitted babelstone.*/semantic-convention tier at OnEnd, BEFORE the
        // exporter runs — the load-bearing emit-time control over the regulated trace store. Registered
        // LAST so it sees the tags the manual spans + Npgsql/AspNetCore auto-instrumentation produced.
        .AddBabelstonePiiGuard()
        .AddOtlpExporter())
    // Metrics (ADR-IC-007 Layer 1 / ADR-IC-004 §P4): listen to the engine's meter and export over
    // OTLP to the Collector → Prometheus, where the §P4 warning/critical thresholds alert. The
    // publish-lag SLI (outbox_publish_lag_seconds) and per-row latency histogram are emitted by the
    // outbox relay (Babelstone.OutboxPublisher: OutboxLagObserver + OutboxDrainer), which THIS host
    // co-runs in-process — the relay's hosted service + the OutboxLagObserver singleton are
    // registered below, so the instruments AddMeter picks up here are actually produced in-process.
    .WithMetrics(metrics => metrics
        .AddMeter(BabelstoneTelemetry.MeterName)
        // Npgsql's built-in db.client.operation.duration histogram (K.5): query-latency
        // per database command, emitted on THIS same meter provider so it is exported through the
        // one OTLP pipe alongside the engine's own instruments (the outbox-lag SLI et al.).
        .AddNpgsqlQueryTelemetry()
        // The runtime no-PII guard for METRICS (OBS_NO_PII_ATTRS): a View with an explicit TagKeys allowlist over
        // the Babelstone.Engine meter instruments, so a metric dimension whose key is outside the admitted
        // babelstone.*/operational tier is dropped at emit. Scoped to the Babelstone meter, so Npgsql's
        // db.* dimensions are untouched. A View — not a processor — is the only emit-time metric filter.
        .AddBabelstonePiiGuard()
        .AddOtlpExporter())
    // Logs (ADR-IC-007 Layer 1 §P5 / OBS_NO_PII_ATTRS): wire an OTel LoggerProvider so the
    // estate's structured logs flow over the same OTLP pipe, and run the runtime no-PII guard over them —
    // a BaseProcessor<LogRecord> that strips any un-namespaced PII-fragment field (e.g. a {Account}
    // message-template hole) before export, while keeping the §P5 operational references. Registered
    // before the exporter so the strip lands first.
    .WithLogging(logging => logging
        .AddBabelstonePiiGuard()
        .AddOtlpExporter());

// snake_case on the wire (principal_cents, tan_basis_points, rate_sheet_version_id), money as
// integer cents — the same discipline as RateSheets.Api and the deposit configuration surface.
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
    options.SerializerOptions.PropertyNameCaseInsensitive = false;
});

// Application / integration credentials (the DB connection string AND the Redpanda SASL/SCRAM
// password — see the outbox-relay wiring below) resolve through the ISecretProvider boundary
// (ADR-PC-004 Amendment A1) — distinct from the per-subject PII transit keys (IPiiKeyStore).
// Default to the configuration-backed provider so `make up` keeps working with existing config;
// opt into OpenBao KV v2 with OpenBao:Enabled=true. The resolved credential stays at this
// composition root: never on a saga message (ADR-IC-003 §P7) nor the durable bus (ADR-PC-004 §P2).
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
// the event store. The typed runtime (registry + drainer + relay) is composed family-AGNOSTICALLY at
// this host root by AddProjectionRuntime below — over these stores and EVERY family's registered
// IProjectionModule — so no single family owns it; migrations 0010/0011 own the
// `projections` discriminator columns + the `projection_checkpoints` table they read/write.
builder.Services.AddSingleton<IProjectionStorage>(_ => new PostgresProjectionStore(connectionString));
builder.Services.AddSingleton<IProjectionCheckpointStore>(_ => new PostgresProjectionCheckpointStore(connectionString));
// The spine-owned, account_ref-keyed MOVEMENT LEDGER (ADR-PC-032 §A1 / §95 read side): the read
// half the money-movement ADR named but deferred until the discoverability seam (IMovementBearing)
// existed. It is a family-agnostic spine component (ADR-PC-021 §P2) — ONE account-keyed projection,
// NOT a per-family copy — backed by the same PostgreSQL tier (migration 0019 owns `movement_ledger`).
// The store is registered here beside the other spine projection storage; the MovementLedgerProjector
// folds every Movement-bearing event's Movements into it by pattern-matching the IMovementBearing seam,
// so it stays cross-family and account-keyed (a single account_ref receives movements from many
// streams), which is why it is a directly-driven spine projector rather than a per-family
// IProjectionRunner on the per-stream drainer.
builder.Services.AddSingleton<IMovementLedgerStore>(_ => new PostgresMovementLedgerStore(connectionString));
builder.Services.AddSingleton(serviceProvider =>
    new MovementLedgerProjector(serviceProvider.GetRequiredService<IMovementLedgerStore>()));
// A.11 snapshot runtime wiring (ADR-PC-003): the byte-oriented snapshot store is a family-agnostic
// spine component (ADR-PC-021), backed by the same PostgreSQL tier as the event store (ADR-PC-003 §D
// — snapshots are rows in a `snapshots` table in the SAME database). The family module composes the
// typed SnapshotStore<TState> + the per-N CountBasedSnapshotPolicy over this, threading them into its
// AggregateRuntime to flip snapshots ON for v1; registering the storage here keeps the storage-boundary
// discipline (the only code that touches the snapshots table is PostgresSnapshotStore). Migration 0003
// owns the `snapshots` table.
builder.Services.AddSingleton<ISnapshotStorage>(_ => new PostgresSnapshotStore(connectionString));
// D.4 CQRS read model (ADR-IC-005): the denormalized read-model STORE is FAMILY-OWNED (ADR-PC-021
// §D2/§P2 family-owned ownership) — the deposit-shaped table is one family's domain shape, so its
// IDepositReadModelStore / PostgresDepositReadModelStore registration moved INTO TermDepositHostModule,
// where the read-model runner over it is already composed. The host no longer
// names a family read-model type; the module registers its own store from the host's already-secret-
// resolved engine connection string (FamilyHostContext.EngineConnectionString), so the ISecretProvider
// boundary (ADR-PC-004 A1) still lives only at this composition root.
// The dual-encode split (ADR-PC-028 §Decision / STORE_BUS_ENCODING_EQUIVALENCE):
//   • STORE codec — the self-describing JSON JsonEventSerializer fills events.payload (the book of
//     record, decodable with NO Schema Registry — EVENT_STORE_PAYLOAD_SELF_DESCRIBING). It is the
//     runtime's `serializer` (and its sole decode/replay path), UNCHANGED.
//   • BUS codec — registered IFF Bus:Encoding=avro: real Avro + a registered Schema-Registry schema_id
//     (Babelstone.Engine.Avro) fills outbox.payload. Built lazily (the SR round-trip happens on first
//     resolve), so the default JSON posture boots with no Schema Registry. The family modules thread
//     the optional BusEventSerializer into their AggregateRuntime as `busSerializer`; with none
//     registered the outbox reuses the JSON store codec (the pre-split single-encoding).
builder.Services.AddSingleton<IEventSerializer, JsonEventSerializer>();
builder.Services.AddSingleton<IPiiProtector, NullPiiProtector>();

// The per-subject PII transit-key boundary (IPiiKeyStore, ADR-PC-004 §P2/§P3) — distinct from the
// IPiiProtector encrypt seam above and the ISecretProvider KV seam: this is the GDPR crypto-shred
// primitive (DestroyKeyAsync) the right-to-be-forgotten endpoint drives.
// Default to the identity NullPiiKeyStore so `make up` / local dev wires the erasure flow end-to-end
// without OpenBao (no PII is encrypted yet — the NullPiiProtector posture — so there is no real key to
// shred). With OpenBao:Enabled the real per-subject transit keys drop in via the same seam, no code
// change. The token is the AppRole client token the KV provider already authenticates with.
builder.Services.AddSingleton<IPiiKeyStore>(_ =>
    builder.Configuration.GetValue<bool>("OpenBao:Enabled")
        ? new OpenBaoTransitClient(
            new HttpClient { BaseAddress = new Uri(builder.Configuration["OpenBao:Address"] ?? "http://localhost:8200/") },
            token: builder.Configuration["OpenBao:Token"]
                ?? throw new InvalidOperationException("OpenBao:Enabled is set but OpenBao:Token is missing for the transit key store."),
            mountPath: builder.Configuration["OpenBao:TransitMount"] ?? "transit")
        : new NullPiiKeyStore());

HostBusEncoding.AddBusEncoding(builder.Services, builder.Configuration);

// Catalog-gated relay (ADR-IC-017 §P1 / INTEGRATION_EVENT_CATALOG_GATED): the engine publishes an
// event onto the durable bus IFF it is a catalogued integration event. The governed embedded-schema
// catalogue (Babelstone.Engine.Avro.AvroSchemaCatalog) IS the family-agnostic membership predicate —
// authoring a schema/AsyncAPI entry is the deliberate promotion (§P2), and an uncatalogued event is
// store-only by construction (appended/folded/replayable, never on the bus). Family host modules
// thread this into their AggregateRuntime. Registering the REAL catalogue here (not the publish-all
// stand-in) is what makes the gate fail-closed in production.
builder.Services.AddSingleton<IIntegrationEventCatalog>(_ => new AvroSchemaCatalog());

// Composition at the edge (ADR-PC-021 §D4/§P4): the host enumerates the families it runs as
// IFamilyHostModule contributions and lets each register its own runtime + decider and map its
// own endpoints. This compose block stays family-count-invariant — adding a family is a new
// module + a ProjectReference, never an edit here. The explicit Option-A list
// (§A3, `[new TermDepositHostModule()]`) is now ASSEMBLY-SCAN discovery (§A3 Option B / §P4,
// realized 2026-06-20): HostModuleLoader scans the host's compile-referenced
// Babelstone.Families.* assemblies for public-parameterless-ctor IFamilyHostModule types, fail-loud on
// a duplicate-family collision (the host-module analogue of HandlerRegistry's duplicate-event_type
// throw), and returns them STABLY ordered so the engine-before-family read-model migration ordering
// (§A6) stays reproducible across boots. Reflection is confined to this composition root (ADR-PC-010
// §P5), never the dispatch spine. The ConfigureServices / MapEndpoints loops below are unchanged.
var familyHostContext = new FamilyHostContext(pack, builder.Configuration, connectionString);
IReadOnlyList<IFamilyHostModule> familyModules =
    new HostModuleLoader().LoadAll(HostModuleLoader.FamilyHostAssemblies());

// MANDATORY fail-closed version-skew cross-check (ADR-PC-007 §P1 / ADR-PC-009
// §P1): the pinned pack's family-manifest (families.yaml) is the authoritative per-deployment family
// set. Each discovered module stamps its SchemaVersion onto every EventEnvelope (§P1), so a family or
// schema-version skew between the code that loaded and the pack the instance is pinned to is an
// audit/replay hazard — the adversarial design review judged this cross-check MANDATORY, not optional.
// It throws on a skew or on a pinned family with no loadable module; we log at Critical (naming the
// offending assembly) and let it escape so the host exits non-zero BEFORE serving, exactly like an
// unverifiable pack does in HostPackLoading. Disk and OCI pack modes both carry the manifest.
try
{
    HostModuleLoader.CrossCheckAgainstPackManifest(familyModules, pack);
}
catch (PackLoadException ex)
{
    packLoadLoggerFactory.CreateLogger("Babelstone.Engine.Api.FamilyManifest").LogCritical(ex,
        "FATAL: family-manifest cross-check failed for pinned pack '{Version}' — refusing to serve "
        + "(bd babelstone-9w2k.3 / ADR-PC-009 §P1).", pack.VersionKey);
    throw;
}

foreach (var module in familyModules)
{
    module.ConfigureServices(builder.Services, familyHostContext);
}

// The family-AGNOSTIC async projection runtime (ADR-PC-002 §P4, two-modes §5.4): the single
// in-process relay + its registry + drainer, composed ONCE here at the §D4 composition root rather
// than inside any one family module (previously term_deposit owned this, so a
// host running another family alone had no relay and never drained its projections/read model). The
// registry is built lazily from EVERY family's registered IProjectionModule (resolved after Build(),
// so all the ConfigureServices calls above have run); each family contributes only modules, the host
// owns the relay. The same co-hosted in-process shape as the outbox relay below.
builder.Services.AddProjectionRuntime();

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
// through the ISecretProvider credential boundary (ADR-PC-004 Amendment A1). The dev default matches
// the Redpanda external listener in infra/compose.yaml (localhost:19092), the same convention
// `make up` exposes.
var bootstrapServers = builder.Configuration["Kafka:BootstrapServers"] ?? "localhost:19092";
// The relay producer's SASL/SCRAM identity (ADR-IC-016 plane ii §4–§6 / KAFKA_SASL_TOPIC_ACL): THIS
// composition root resolves the actual credential and puts it on the relay option. The username
// (svc-outbox-publisher) is declarative
// config; the PASSWORD resolves through the SAME ISecretProvider seam as ConnectionStrings:Engine
// (OpenBaoKvSecretProvider when OpenBao:Enabled, configuration-backed otherwise — §A1). The resolved
// secret stays here in process memory to open the connection: never logged, never on a span, never on
// the bus (ADR-PC-004 §P2). With no Kafka:Sasl:OutboxPublisher:Username configured this is the OFF
// no-op posture (IsConfigured=false ⇒ ApplyTo does nothing), so plaintext loopback Redpanda (`make up`)
// is unchanged — SASL turns on only when a deployment supplies the identity (SASL_SSL + SCRAM-SHA-256).
var outboxPublisherSasl = await HostSasl.ResolveOutboxPublisherAsync(builder.Configuration, secretProvider);
builder.Services.AddSingleton(new OutboxRelayOptions
{
    ConnectionString = connectionString,
    BootstrapServers = bootstrapServers,
    Sasl = outboxPublisherSasl,
});
builder.Services.AddSingleton(serviceProvider =>
    new OutboxDrainer(serviceProvider.GetRequiredService<OutboxRelayOptions>()));
builder.Services.AddHostedService<OutboxRelayService>();
builder.Services.AddSingleton(new OutboxLagObserver(connectionString));

// The dedup-ledger retention sweep, co-hosted in this process (the same
// §5.1 in-process-loop shape as the outbox relay). It bounds the unbounded growth of the two dedup
// ledgers — command_dedup (migration 0015) and inbox (migration 0012) — by deleting their aged tail
// on a slow housekeeping cadence. The per-table retention windows are deliberately ASYMMETRIC: the
// command window is load-bearing (ADR-PC-029 §4 — pruning a receipt before the stream's active
// lifetime + the dispatcher's retry horizon elapses could replay a command into a DUPLICATE deposit),
// so it defaults to 3 years; the inbox window is the simpler Kafka-retention × N (Document 04),
// defaulting to 30 days. All four knobs — the two retention windows AND the operational tuning
// (BatchSize, SweepInterval) — are overridable via the Engine:DedupRetention config section, so an
// operator can retune the sweep (e.g. drain a first backlog faster, or ease off the primary) without
// a recompile; an unset key keeps the conservative default. See DedupRetentionConfiguration.
builder.Services.AddSingleton(
    DedupRetentionConfiguration.FromConfiguration(builder.Configuration, connectionString));
builder.Services.AddSingleton(serviceProvider =>
    new DedupRetentionSweeper(serviceProvider.GetRequiredService<DedupRetentionOptions>()));
builder.Services.AddHostedService<DedupRetentionSweepService>();

var app = builder.Build();

// Resolve the lag observer eagerly so its observable gauge is created BEFORE the first
// metrics-collection cycle — a lazily-resolved singleton would not register the §P4 instrument
// until something asked for it. The hosted relay starts on its own via the host lifecycle.
app.Services.GetRequiredService<OutboxLagObserver>();

app.UseExceptionHandler();

// Hand the active trace id back to the caller on EVERY response — command and query alike
// (ADR-IC-007 Layer 1). The id is read from Activity.Current (the inbound
// request's SERVER span, created by AddAspNetCoreInstrumentation above) inside Response.OnStarting,
// so it is captured just before the headers flush, when the request activity is still current. The
// value is the bare 32-hex W3C trace id (TraceResponseHeader.Name), which a caller — Mission
// Control's Telemetry tab — uses to fetch the trace from Grafana Tempo. The
// trace id is an opaque operational identifier, never PII (ADR-IC-007 §P4 / ADR-PC-004 §P2). Placed
// before endpoint mapping so it wraps every endpoint; a request with no server span (none here once
// instrumentation is on) simply omits the header rather than emitting an empty one.
app.Use(async (context, next) =>
{
    context.Response.OnStarting(() =>
    {
        var traceId = System.Diagnostics.Activity.Current?.TraceId.ToString();
        if (traceId is not null && !context.Response.Headers.ContainsKey(TraceResponseHeader.Name))
        {
            context.Response.Headers[TraceResponseHeader.Name] = traceId;
        }

        return Task.CompletedTask;
    });

    await next(context);
});

foreach (var module in familyModules)
{
    module.MapEndpoints(app);
}

// The operator pack-migration command surface (ADR-PC-009 §P3 / surface §3.6): POST /v1/pack-migrations,
// registered ONCE at host level (family-agnostic). It dispatches on the request's product_family to the
// right family's registered IPackMigrationService / IPackMigrationInstanceResolver — so a multi-family
// host has ONE route, not the per-family collision a Map<TState>-per-module loop would create.
PackMigrationsEndpoints.Map(app);

app.Run();

// Exposed for WebApplicationFactory<Program> in the integration tests.
public partial class Program;
