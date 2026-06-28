using Babelstone.Cadence;
using Babelstone.Lifecycle;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

// The lifecycle-command driver worker HOST (ADR-PC-036 §Decision 2, candidate A; ADR-IC-011 runtime — .NET,
// stack-coherent with the engine; ADR-IC-013 in-house estate placement; ADR-PC-019 §P2 extraction-ready
// subtree; bd babelstone-6cpq.7). A long-running BackgroundService host, NOT an HTTP API — so
// Host.CreateApplicationBuilder, the same shape the engine's outbox relay, the orchestrator's consume loop,
// and the notification scheduler run as hosted services.
//
// This is the new SIBLING deployable that owns the clock the engine deliberately lacks (ADR-PC-023): it ticks
// on a cadence, asks each family rule which lifecycle commands are due as-of today, and POSTs each to the
// engine's existing ADR-PC-029 command surface with the canonical, server-derived, number-pinned idempotency
// key (LCD-1). The engine stays CLOCKLESS — the driver reaches it ONLY over the command HTTP surface, never
// the byte store, and never makes the engine read a clock (NO_CLOCK_DRIVEN_ENGINE_SIGNAL holds).
var builder = Host.CreateApplicationBuilder(args);

// The engine's ADR-PC-029 command surface this driver POSTs to. A service ENDPOINT, not a credential, so — like
// the notification host's read endpoint and the orchestrator's — it resolves straight from configuration (no
// ISecretProvider). Fail-loud: a driver that cannot resolve its target engine must not start.
var engineBaseUrl =
    builder.Configuration["Engine:BaseUrl"]
    ?? builder.Configuration.GetConnectionString("Engine")
    ?? Environment.GetEnvironmentVariable("BABELSTONE_ENGINE_BASE_URL")
    ?? throw new InvalidOperationException(
        "No engine API base URL configured. Set Engine:BaseUrl, ConnectionStrings:Engine, or " +
        "BABELSTONE_ENGINE_BASE_URL (the ADR-PC-029 command surface — POST /v1/loans/{id}/installment, " +
        "POST /v1/deposits/{id}/maturity).");

// The wall-clock the worker loop OWNS (ADR-PC-023 §6 — the engine emits no clock-driven signal, so the
// downstream driver owns the clock). TimeProvider.System in production; a test substitutes a fake so the loop
// can be driven with no real wall-clock wait.
builder.Services.AddSingleton(TimeProvider.System);

// The driver's cadence/retry/backoff knobs (ADR-PC-023 §6). Bound from the "Lifecycle" configuration section so
// an operator can tune the poll interval; the generous one-hour default sits well inside a maturity/installment
// due date's latency tolerance (a one-shot maturity may fire up to a poll-interval late — acceptable,
// ADR-PC-036 §Residual risks).
var schedulerOptions = new CadenceSchedulerOptions();
var pollSeconds = builder.Configuration.GetValue<double?>("Lifecycle:PollIntervalSeconds");
if (pollSeconds is > 0)
{
    schedulerOptions = new CadenceSchedulerOptions { PollInterval = TimeSpan.FromSeconds(pollSeconds.Value) };
}

builder.Services.AddSingleton(schedulerOptions);

// The dispatch ledger (ADR-PC-036 §Decision 2): the "already fired this occurrence" memory that makes a re-tick
// of an already-dispatched lifecycle command a no-op, keyed on the canonical number-pinned dispatch id. In-memory
// for v1; a durable, crash-surviving ledger is a later operating concern the host owns as it hardens (the engine's
// command_dedup is the authoritative idempotency backstop regardless).
builder.Services.AddSingleton<LifecycleDispatchLedger>();

// The command-POST SINK (ADR-PC-036 §Decision 2): a typed HttpClient whose BaseAddress is the engine's ADR-PC-029
// command surface, normalised to a trailing "/" so a "/v1/..." command path resolves. This is the ONLY runtime
// path the driver takes to the engine. The sink presents the canonical server-derived idempotency key (LCD-1) and
// the scoped non-interactive SCA principal on money-mover routes (ADR-PC-036 §Decision 1).
builder.Services.AddHttpClient<ILifecycleCommandSink, HttpLifecycleCommandSink>(client =>
    client.BaseAddress = new Uri(engineBaseUrl.EndsWith('/') ? engineBaseUrl : engineBaseUrl + "/"));

// The per-tick engine over the registered family rules + the dispatch ledger + the sink (ADR-PC-036 §Decision 2).
// Family ILifecycleCommandRule contributions plug in here with zero core diff — the first are the sibling bd
// issues babelstone-6cpq.8 (term-deposit maturity, one-shot) and babelstone-6cpq.9 (personal-loan installment,
// recurring), which this host stands up. With no rule registered yet the pass simply runs an empty tick.
builder.Services.AddSingleton<LifecycleSchedulePass>();

// The host shell — the standing BackgroundService the schedule pass runs inside. It OWNS the clock, cadence,
// retry and backoff (ADR-PC-023 §6); it lives here, in a downstream sibling host, never inside the engine
// (a timer there trips BENG004).
builder.Services.AddHostedService<LifecycleWorker>();

var app = builder.Build();
await app.RunAsync();

namespace Babelstone.Lifecycle.Host
{
    /// <summary>
    /// Marker partial so the host assembly exposes a public type a test project can name when it asserts the
    /// composition (the top-level statements above compile into <c>Program</c>). No behaviour — the host is
    /// composed by the statements above.
    /// </summary>
    public sealed partial class Program;
}
