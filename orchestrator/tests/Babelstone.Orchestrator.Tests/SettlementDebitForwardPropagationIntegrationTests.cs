using System.Net;
using System.Text.Json;
using Babelstone.Orchestrator.Dispatch;
using Babelstone.Orchestrator.Inbox;
using Babelstone.Orchestrator.Saga;
using Babelstone.Orchestrator.Saga.Settlement;
using Babelstone.TestFixtures;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace Babelstone.Orchestrator.Tests;

/// <summary>
/// SETTLEMENT_LEG_ACCOUNT_REF_PROMOTED for the DEBIT legs, end-to-end (bd babelstone-u79p.22; ADR-PC-043 §D5,
/// ADR-IC-018 §D5). In plain English: a loan installment collects money FROM the customer's conta à ordem, and
/// that debit has to land on the customer's REAL account for the RIGHT amount — not the ACCT-{processId}
/// placeholder. The debit path is two-legged: a reversible ReserveAccountBalance places the hold on the START
/// advance (where the promoted destination is in scope), then the irreversible ConfirmDebit captures it on a
/// LATER advance — driven by a dispatcher-SYNTHESIZED BalanceReserved result event that carries none of the
/// promoted values of its own. This proves the dispatcher FORWARD-PROPAGATES the promoted destination across
/// that reserve→confirm hop (the SAME mechanism the SCA claims already use): both the reserve leg AND the
/// confirm leg reach the engine-CA ingress carrying the SAME promoted account_ref + amount + engine-ca target,
/// so reserve and confirm agree and the capture lands on the real conta à ordem.
/// </summary>
/// <remarks>
/// The Core-ACL stub (the lane's sanctioned <c>RecordingHttpServer</c>) stands in for the engine-CA settlement
/// ingress and records each leg's request BODY; the assertions read the promoted fields straight off the
/// recorded reserve (<c>/v1/reservations</c>) and confirm (<c>/v1/debits</c>) bodies. A dedicated Postgres
/// container isolates this class's rows so the dispatcher only ever sees what it seeded. Both legs route to the
/// engine-CA base URL (the stub) because their bodies carry <c>settlement_target = engine-ca</c> — the router
/// selects the counterparty from that header alone (ADR-IC-018 §D5), never the account_ref.
/// </remarks>
[Trait("Category", "Integration")]
public sealed class SettlementDebitForwardPropagationIntegrationTests : IAsyncLifetime
{
    // The customer's conta à ordem — the engine current-account family's opaque stream id is a GUID string
    // (AccountRef == AccountId.ToString(), ADR-PC-033). The promoted destination the debit must land on.
    private static readonly string ContaAOrdem = Guid.Parse("a0a0a0a0-a0a0-a0a0-a0a0-a0a0a0a0a0a0").ToString();
    private const long CollectionAmountCents = 45_000L; // €450.00 — exactly the source Movement.Amount.

    private readonly PostgreSqlContainer _pg = new PostgreSqlBuilder("postgres:18-alpine").Build();
    private readonly SagaStateStore _stateStore = new();
    private readonly SagaTransitionLog _transitionLog = new();

    private string ConnectionString => _pg.GetConnectionString();

    public async Task InitializeAsync()
    {
        await _pg.GatedStartAsync();
        await new Migrations.MigrationRunner(ConnectionString).ApplyAsync();
    }

    public async Task DisposeAsync() => await _pg.DisposeAsync();

    [Fact]
    public async Task A_loan_collection_debit_forward_propagates_the_promoted_destination_from_reserve_to_confirm()
    {
        // Auto-start the settlement saga off a loan-installment Movement-bearing event: an Originated DEBIT
        // carrying the promoted destination (movementaccountrefs) + amount (movementamounts) on its headers.
        // The reserve leg is emitted on the START advance with those values in scope.
        var processId = Guid.NewGuid();
        await AutoStartDebitCollectionAsync(processId, ContaAOrdem, CollectionAmountCents);

        // Both settlement legs go to the engine-CA ingress (the stub), which accepts every leg (200). The drain
        // delivers the reserve, synthesizes BalanceReserved (forward-propagating the promoted destination onto
        // its headers), then emits + delivers ConfirmDebit off that later advance.
        await using var acl = new RecordingHttpServer(_ => (HttpStatusCode.OK, """{"ok":true}"""));
        using var host = BuildHost(engineCaBaseUrl: acl.BaseUrl);
        await host.StartAsync();
        try
        {
            await WaitUntilAsync(
                () => Task.FromResult(
                    acl.Requests.Any(r => r.Path == "/v1/reservations")
                    && acl.Requests.Any(r => r.Path == "/v1/debits")),
                TimeSpan.FromSeconds(30),
                "the dispatcher never delivered BOTH the reserve and the confirm-debit legs");
        }
        finally
        {
            await host.StopAsync();
        }

        // The RESERVE leg (START advance, promoted headers directly in scope) carried the promoted destination.
        var reserve = Assert.Single(acl.Requests, r => r.Path == "/v1/reservations");
        AssertPromotedBody(reserve.Body, "the reserve leg");

        // The CONFIRM leg (LATER advance, off the header-less synthesized BalanceReserved event) carried the
        // SAME promoted destination — the forward-propagation worked, so reserve and confirm agree on the
        // account + amount and the capture lands on the real conta à ordem (never the ACCT-{processId}
        // placeholder). This is the bd babelstone-u79p.22 fix.
        var confirm = Assert.Single(acl.Requests, r => r.Path == "/v1/debits");
        AssertPromotedBody(confirm.Body, "the confirm-debit leg");
    }

    // Assert a recorded settlement-leg body carries the promoted engine-CA destination: the real conta à ordem
    // account_ref (never the ACCT-{processId} placeholder), the exact collection amount, and the engine-ca
    // counterparty target.
    private static void AssertPromotedBody(string body, string leg)
    {
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;

        Assert.Equal(ContaAOrdem, root.GetProperty("account_ref").GetString());
        Assert.DoesNotContain(SettlementReferences.AccountPrefix, root.GetProperty("account_ref").GetString()!);
        Assert.Equal(CollectionAmountCents, root.GetProperty("amount_cents").GetInt64());
        Assert.Equal(
            SettlementCommandRouter.EngineCaValue,
            root.GetProperty("settlement_target").GetString());
    }

    /// <summary>Auto-start the settlement saga off a loan-installment Movement-bearing event (an Originated
    /// DEBIT) carrying the promoted destination + amount on its CloudEvents headers (the fan-out reduces this
    /// single leg to a one-entry list). The auto-start emits ReserveAccountBalance, whose outbox row the
    /// substrate stamps with the promoted destination read off the event headers. Returns the saga's
    /// PER-OCCURRENCE process id (ADR-PC-032 §A9/§A10) the outbox rows are keyed on.</summary>
    private async Task<Guid> AutoStartDebitCollectionAsync(Guid processId, string accountRef, long amountCents)
    {
        var sink = new SettlementCommandOutboxSink(new SagaOutboxWriter());
        var handler = NewHandler(sink);

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [SettlementSagaModule.OriginHeader] = "Originated",
            [SettlementMovementFanout.DirectionsHeader] = "Debit",
            [SettlementMovementFanout.AccountRefsHeader] = accountRef,
            [SettlementMovementFanout.AmountsHeader] =
                amountCents.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };

        var eventId = Guid.NewGuid();
        await using var connection = await OpenAsync();
        await using var tx = await connection.BeginTransactionAsync();
        var outcome = await handler.AdvanceAsync(connection, tx, new SagaInboxEvent(
            MessageId: eventId, ProcessId: processId, EventType: "LoanInstallmentPaid",
            SourceTopic: "personal_loan", CorrelationId: null,
            TraceParent: "00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01",
            ExtensionHeaders: headers));
        await tx.CommitAsync();

        Assert.Equal(AdvanceOutcome.Advanced, outcome);
        return SettlementMovementFanout.OccurrenceProcessId(processId, eventId, 0);
    }

    private SagaAdvanceHandler NewHandler(ISagaCommandSink sink)
    {
        // The SEED only writes the reserve outbox row (no HTTP runs here); the engine-CA base URL is irrelevant
        // for the seed, the drain below points it at the live stub.
        var context = new SagaModuleContext(
            RuntimeConnectionString: ConnectionString,
            EngineBaseUrl: "http://engine.invalid",
            SettlementBaseUrl: "http://legacy.invalid",
            EngineCaSettlementBaseUrl: "http://engine-ca.invalid");
        var module = new SettlementSagaModule(context, consumeTopics: ["personal_loan"]);
        return new SagaAdvanceHandler(
            new ISagaStateMachine[] { module.StateMachine },
            _stateStore, _transitionLog, sink,
            new ISagaModule[] { module });
    }

    private IHost BuildHost(string engineCaBaseUrl)
    {
        var options = new SagaCommandDispatcherOptions
        {
            ConnectionString = ConnectionString,
            EngineBaseUrl = "http://engine.invalid",
            // Legacy points nowhere reachable: every leg here is engine-ca, so a leg that mis-routed legacy
            // would fail loudly rather than pass silently on the wrong counterparty.
            SettlementBaseUrl = "http://legacy.invalid",
            EngineCaSettlementBaseUrl = engineCaBaseUrl,
            PollInterval = TimeSpan.FromMilliseconds(100),
        };
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton<ISagaCommandRouter>(new SettlementCommandRouter(options));
        builder.Services.AddSingleton<ICommandRouter>(sp =>
            new CompositeCommandRouter(sp.GetServices<ISagaCommandRouter>()));
        builder.Services.AddHttpClient();
        builder.Services.AddSingleton<ISagaStateMachine, SettlementProcess>();
        builder.Services.AddSingleton<SagaStateStore>();
        builder.Services.AddSingleton<SagaTransitionLog>();
        builder.Services.AddSingleton<SagaOutboxWriter>();
        builder.Services.AddSingleton<ISagaTypedCommandSink>(sp =>
            new SettlementCommandOutboxSink(sp.GetRequiredService<SagaOutboxWriter>()));
        builder.Services.AddSingleton<ISagaCommandSink>(sp =>
            new CompositeSagaCommandSink(sp.GetServices<ISagaTypedCommandSink>()));
        builder.Services.AddSingleton(sp => new SagaAdvanceHandler(
            sp.GetServices<ISagaStateMachine>(),
            sp.GetRequiredService<SagaStateStore>(),
            sp.GetRequiredService<SagaTransitionLog>(),
            sp.GetRequiredService<ISagaCommandSink>()));
        builder.Services.AddSingleton<IResultEventBridge, SettlementResultEvents.Bridge>();
        builder.Services.AddSingleton(sp => new SagaCommandDispatchDrainer(
            sp.GetRequiredService<SagaCommandDispatcherOptions>(),
            sp.GetRequiredService<ICommandRouter>(),
            sp.GetRequiredService<IHttpClientFactory>(),
            sp.GetRequiredService<SagaAdvanceHandler>(),
            sp.GetServices<IResultEventBridge>()));
        builder.Services.AddHostedService<SagaCommandDispatcherService>();
        return builder.Build();
    }

    private async Task<NpgsqlConnection> OpenAsync()
    {
        var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        return connection;
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, TimeSpan timeout, string failureMessage)
    {
        var deadline = DateTime.UtcNow.Add(timeout);
        while (DateTime.UtcNow < deadline)
        {
            if (await condition())
            {
                return;
            }

            await Task.Delay(100);
        }

        throw new TimeoutException($"Timed out after {timeout.TotalSeconds}s: {failureMessage}.");
    }
}
