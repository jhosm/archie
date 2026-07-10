using System.Net;
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
/// SETTLEMENT_LEG_SCA_GATE_CANNOT_BYPASS + SETTLEMENT_CA_SCA_STALE_IS_RETRIABLE (bd babelstone-t7o3.19,
/// babelstone-98mj.5; ADR-PC-032 §A7/§A8; ADR-IC-010 §A11–A12; ADR-IC-006 §P2; ADR-PC-043). In plain English:
/// when a saga matures a deposit or pays a coupon — money the bank can never claw back — it must confirm the
/// customer recently passed strong authentication (SCA) right before the cash actually moves. If the proof is
/// missing or stale the receiver refuses (422 SCA_REQUIRED) and the cash does NOT move — but that refusal is
/// NOT a terminal failure: it is RETRIABLE. The leg stays PENDING under the SAME <c>process_id</c> so that a
/// fresh SCA proof re-drives the SAME cash leg to settlement, never dropping the payout (terminal-FAILED) and
/// never starting a second occurrence (a double move). This proves both halves end-to-end against an in-process
/// Core ACL stub (the lane's sanctioned <c>RecordingHttpServer</c>) that REPRODUCES the receiver's fail-closed
/// SCA check: a saga-driven money-mover with no / stale SCA is refused at the settlement leg and the leg stays
/// retriable-PENDING; then a fresh attested proof on the SAME row re-drives it and the leg settles PUBLISHED.
/// </summary>
/// <remarks>
/// <para>
/// <b>Attest-not-deny (ADR-IC-006 §P2; ADR-PC-019 §P2 / ADR-IC-018 §D2).</b> The substrate ATTESTS — it threads
/// the attested <c>acr</c> / <c>auth_time</c> from the auto-starting Movement-bearing event's CloudEvents
/// headers (the populate hop, bd babelstone-t7o3.20) onto the cash leg's <c>saga_outbox</c> row, and the
/// dispatcher re-emits them as <c>X-SCA-Acr</c> / <c>X-SCA-Auth-Time</c> on the cash-leg delivery. The RECEIVER
/// (the Core ACL settlement leg) is the DENY point, never the substrate. The ACL stub here reproduces that
/// receiver gate (the SAME fail-closed verdict the engine's <c>ScaPrecondition.Check</c> gives — 422 when the
/// proof is absent, non-numeric, in the future, or older than the window; it is reproduced, NOT referenced, so
/// the orchestrator subtree stays extraction-ready, ADR-PC-019 §P2).
/// </para>
/// <para>
/// <b>Re-checked at the settlement-dispatch instant (ADR-PC-032 §A7).</b> The receiver compares
/// <c>now − auth_time</c> against <c>SCA_MAX_AGE</c> (300 s) at the moment the cash leg is delivered — not
/// inherited from saga entry. A proof fresh at saga start but stale when the cash leg fires is refused. A
/// dedicated Postgres container isolates this class's rows so the dispatcher only ever sees what it seeded.
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
public sealed class SettlementLegStepUpScaIntegrationTests : IAsyncLifetime
{
    // The receiver's SCA freshness window mirror (the SAME 300 s the engine-direct gate + Kong route gate use,
    // ADR-IC-006 §P2). The orchestrator subtree cannot reference an engine-side constant (extraction-ready), so
    // the test pins the shared value.
    private const long ScaMaxAgeSeconds = 300;

    // The ce_-stripped, lowercased projection of the attested claims as they ride a Movement-bearing event's
    // CloudEvents headers (the populate hop, bd babelstone-t7o3.20 / ADR-PC-032 §A8). The advance handler reads
    // these off message.ExtensionHeaders.
    private const string ScaAcrHeaderKey = "scaacr";
    private const string ScaAuthTimeHeaderKey = "scaauthtime";

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
    public async Task A_saga_maturity_with_NO_SCA_stays_retriable_PENDING_then_a_fresh_proof_re_drives_it()
    {
        // A maturity is an Originated CREDIT; auto-start the settlement saga off the Movement-bearing event but
        // with NO SCA claims on its headers (the absent-proof case). The leg emits ConfirmCredit carrying no
        // SCA; the receiver fail-closes 422 SCA_REQUIRED before the credit lands. Under ADR-PC-043 that 422 is
        // RETRIABLE, not terminal — the row stays PENDING for a re-drive on a fresh proof.
        var processId = Guid.NewGuid();
        var occurrenceId = await AutoStartMaturityAsync(processId, scaAcr: null, scaAuthTime: null);
        var creditId = await OutboxMessageIdAsync(occurrenceId, SettlementProcess.ConfirmCredit);

        await using var acl = new RecordingHttpServer(ScaGatedResponder);
        using var host = BuildHost(acl.BaseUrl);
        await host.StartAsync();
        try
        {
            // The receiver was hit with an absent proof — 422 SCA_REQUIRED — but the leg is NOT flipped
            // terminal: it stays PENDING (retriable), never FAILED, never a dropped payout.
            await WaitUntilAsync(
                () => Task.FromResult(acl.Requests.Any(r => r.Path == "/v1/credits")),
                TimeSpan.FromSeconds(30),
                "the dispatcher never delivered the credit leg to the receiver");
            Assert.Equal("PENDING", await StatusAsync(creditId));       // retriable, not settled
            Assert.Null(await FailureStatusCodeAsync(creditId));         // not terminal-FAILED

            // Snapshot the absent-proof-phase attempts BEFORE the fresh re-stamp. The 422 is retriable
            // (ADR-PC-043), so the leg stays PENDING and the 100 ms poll loop keeps re-driving the SAME row —
            // acl.Requests is a live, still-growing recorder queue and Assert.Single over it races the next
            // poll tick (the same flake as bd babelstone-98mj.15's sibling test). Snapshotting with ToArray()
            // right before the restamp fixes the set to the absent-proof phase and proves the semantics without
            // a count race: at least one attempt WAS forwarded, and EVERY forwarded attempt carried NEITHER SCA
            // header — the gate saw an absent proof, exactly as the engine-direct path does.
            var absentPhaseAttempts = acl.Requests.Where(r => r.Path == "/v1/credits").ToArray();
            Assert.NotEmpty(absentPhaseAttempts);                       // the absent-proof leg WAS forwarded (>=1)
            Assert.All(absentPhaseAttempts, r =>
            {
                Assert.Null(r.ScaAcr);
                Assert.Null(r.ScaAuthTime);
            });

            // A fresh SCA proof arrives: re-stamp the SAME outbox row (same process_id, same seq) with fresh
            // attested claims — modelling the re-attestation that unblocks the leg. The dispatcher re-drives
            // the SAME row and the receiver now lets the credit through → PUBLISHED. No second occurrence.
            var fresh = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            await RestampRowScaAsync(creditId, "urn:bank:sca:psd2", fresh);

            await WaitUntilAsync(
                async () => await StatusAsync(creditId) == "PUBLISHED",
                TimeSpan.FromSeconds(30),
                "the re-driven credit leg did not settle after a fresh SCA proof");
        }
        finally
        {
            await host.StopAsync();
        }

        // The leg settled under the SAME process_id / SAME row — a re-drive, never a fresh occurrence.
        Assert.Equal("PUBLISHED", await StatusAsync(creditId));
        var settled = acl.Requests.Last(r => r.Path == "/v1/credits");
        Assert.Equal("urn:bank:sca:psd2", settled.ScaAcr);
    }

    [Fact]
    public async Task A_saga_maturity_with_a_STALE_SCA_stays_retriable_PENDING_then_a_fresh_proof_re_drives_it()
    {
        // SCA happened, but too long ago — auth_time beyond the window at the DISPATCH instant. The claims ARE
        // forwarded (the substrate attests); the RECEIVER re-checks freshness at dispatch and refuses (422
        // SCA_REQUIRED). Under ADR-PC-043 that refusal is RETRIABLE — the leg stays PENDING for a re-drive.
        var processId = Guid.NewGuid();
        var stale = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - (ScaMaxAgeSeconds + 60);
        var occurrenceId = await AutoStartMaturityAsync(processId, scaAcr: "urn:bank:sca:psd2", scaAuthTime: stale);
        var creditId = await OutboxMessageIdAsync(occurrenceId, SettlementProcess.ConfirmCredit);

        await using var acl = new RecordingHttpServer(ScaGatedResponder);
        using var host = BuildHost(acl.BaseUrl);
        await host.StartAsync();
        try
        {
            // The stale claims WERE forwarded (the gate saw them and judged auth_time stale, ADR-PC-032 §A7)
            // — but the 422 leaves the leg PENDING (retriable), not terminal-FAILED.
            await WaitUntilAsync(
                () => Task.FromResult(acl.Requests.Any(r => r.Path == "/v1/credits")),
                TimeSpan.FromSeconds(30),
                "the dispatcher never delivered the credit leg to the receiver");
            Assert.Equal("PENDING", await StatusAsync(creditId));       // retriable, not settled
            Assert.Null(await FailureStatusCodeAsync(creditId));         // not terminal-FAILED

            // Snapshot the stale-phase attempts BEFORE the fresh re-attestation. Because the 422 is retriable
            // (ADR-PC-043) the leg stays PENDING and the 100 ms poll loop keeps re-driving the SAME row, so
            // acl.Requests is a live, still-growing recorder queue — asserting Assert.Single over it races the
            // next poll tick (bd babelstone-98mj.15). Snapshotting with ToArray() right before the restamp
            // fixes the set to the stale phase (no fresh-proof request can enter it) and lets us prove the
            // semantics WITHOUT a count race: at least one attempt WAS forwarded, and EVERY forwarded stale
            // attempt carried exactly the stale proof (strictly stronger than checking one).
            var stalePhaseAttempts = acl.Requests.Where(r => r.Path == "/v1/credits").ToArray();
            Assert.NotEmpty(stalePhaseAttempts);                        // the stale claims WERE forwarded (>=1)
            Assert.All(stalePhaseAttempts, r =>
            {
                Assert.Equal("urn:bank:sca:psd2", r.ScaAcr);
                Assert.Equal(stale.ToString(System.Globalization.CultureInfo.InvariantCulture), r.ScaAuthTime);
            });

            // A fresh re-attestation on the SAME row re-drives the SAME leg to settlement.
            var fresh = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            await RestampRowScaAsync(creditId, "urn:bank:sca:psd2", fresh);

            await WaitUntilAsync(
                async () => await StatusAsync(creditId) == "PUBLISHED",
                TimeSpan.FromSeconds(30),
                "the re-driven credit leg did not settle after a fresh SCA proof");
        }
        finally
        {
            await host.StopAsync();
        }

        Assert.Equal("PUBLISHED", await StatusAsync(creditId));
    }

    [Fact]
    public async Task A_saga_maturity_with_FRESH_attested_SCA_forwards_the_claims_and_settles()
    {
        // Fresh gateway-attested SCA on the auto-starting event's headers: the substrate threads them onto the
        // cash leg's outbox row, the dispatcher re-emits them, and the receiver lets the credit through.
        var processId = Guid.NewGuid();
        var fresh = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var occurrenceId = await AutoStartMaturityAsync(processId, scaAcr: "urn:bank:sca:psd2", scaAuthTime: fresh);
        var creditId = await OutboxMessageIdAsync(occurrenceId, SettlementProcess.ConfirmCredit);

        await using var acl = new RecordingHttpServer(ScaGatedResponder);
        await DrainUntilTerminalAsync(acl, creditId);

        // Fresh attested SCA → the receiver let it through; the leg settled PUBLISHED.
        Assert.Equal("PUBLISHED", await StatusAsync(creditId));

        // The dispatcher forwarded BOTH gateway-attested claims to the receiver — the SAME X-SCA-Acr /
        // X-SCA-Auth-Time the engine-direct money-movers enforce (ADR-IC-010 §A11–A12).
        var hit = Assert.Single(acl.Requests, r => r.Path == "/v1/credits");
        Assert.Equal("urn:bank:sca:psd2", hit.ScaAcr);
        Assert.Equal(fresh.ToString(System.Globalization.CultureInfo.InvariantCulture), hit.ScaAuthTime);
    }

    // ---- The Core ACL's RECEIVER-side SCA gate (reproduces the deny point; NOT a reference, ADR-PC-019 §P2) -

    /// <summary>
    /// The Core ACL settlement leg's fail-closed SCA verdict, re-checked at the DISPATCH instant (ADR-PC-032
    /// §A7): <c>422 SCA_REQUIRED</c> when <c>X-SCA-Acr</c> is absent/empty, <c>X-SCA-Auth-Time</c> is
    /// absent/non-numeric, or <c>auth_time</c> is in the future or older than the freshness window; otherwise
    /// the credit confirms (200). This is the RECEIVER deny point — the substrate only attested the claims.
    /// Reproduced here (not referenced) so the orchestrator subtree stays extraction-ready. A non-money-mover
    /// path (e.g. /v1/reservations) is not SCA-gated; the credit leg is.
    /// </summary>
    private static (HttpStatusCode Status, string Body) ScaGatedResponder(RecordingHttpServer.RecordedRequest request)
    {
        if (request.Path != "/v1/credits")
        {
            // Any other settlement route is not SCA-gated by THIS test (the reserve/debit legs settle freely).
            return (HttpStatusCode.OK, """{"ok":true}""");
        }

        if (string.IsNullOrWhiteSpace(request.ScaAcr)
            || !long.TryParse(request.ScaAuthTime, System.Globalization.CultureInfo.InvariantCulture, out var authTime))
        {
            return (HttpStatusCode.UnprocessableEntity, """{"code":"SCA_REQUIRED","title":"SCA required"}""");
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (authTime > now || (now - authTime) > ScaMaxAgeSeconds)
        {
            return (HttpStatusCode.UnprocessableEntity, """{"code":"SCA_REQUIRED","title":"SCA required"}""");
        }

        return (HttpStatusCode.OK, """{"credit":"confirmed"}""");
    }

    // ---- Host wiring (the SETTLEMENT saga's bridge + router; the cash leg goes to the ACL stub) -------

    private SagaAdvanceHandler NewHandler(ISagaCommandSink sink, string settlementBaseUrl)
    {
        var context = new SagaModuleContext(
            RuntimeConnectionString: ConnectionString,
            EngineBaseUrl: "http://engine.invalid",
            SettlementBaseUrl: settlementBaseUrl);
        var module = new SettlementSagaModule(context, consumeTopics: ["term_deposit"]);
        return new SagaAdvanceHandler(
            new ISagaStateMachine[] { module.StateMachine },
            _stateStore, _transitionLog, sink,
            new ISagaModule[] { module });
    }

    /// <summary>Auto-start the settlement saga off a maturity Movement-bearing event (an Originated CREDIT)
    /// carrying the attested SCA on its CloudEvents headers (the populate hop). The auto-start emits
    /// ConfirmCredit, whose outbox row the substrate stamps with the SCA claims read off the event headers.
    /// Returns the saga's PER-OCCURRENCE process id (ADR-PC-032 §A9/§A10 Revised 2026-07-04) — the instance
    /// the outbox rows are keyed on, derived from (subject, event id, movement index).</summary>
    private async Task<Guid> AutoStartMaturityAsync(Guid processId, string? scaAcr, long? scaAuthTime)
    {
        var sink = new SettlementCommandOutboxSink(new SagaOutboxWriter());
        // The settlement base URL is irrelevant for the SEED (no HTTP runs here — the advance only writes the
        // outbox row); the drain below points at the live ACL stub.
        var handler = NewHandler(sink, "http://settlement.invalid");

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [SettlementSagaModule.OriginHeader] = "Originated",
            [SettlementMovementFanout.DirectionsHeader] = "Credit",
        };
        if (scaAcr is not null)
        {
            headers[ScaAcrHeaderKey] = scaAcr;
        }

        if (scaAuthTime is { } authTime)
        {
            headers[ScaAuthTimeHeaderKey] = authTime.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        var eventId = Guid.NewGuid();
        await using var connection = await OpenAsync();
        await using var tx = await connection.BeginTransactionAsync();
        var outcome = await handler.AdvanceAsync(connection, tx, new SagaInboxEvent(
            MessageId: eventId, ProcessId: processId, EventType: "DepositMatured",
            SourceTopic: "term_deposit", CorrelationId: null,
            TraceParent: "00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01",
            ExtensionHeaders: headers));
        await tx.CommitAsync();

        Assert.Equal(AdvanceOutcome.Advanced, outcome);
        return SettlementMovementFanout.OccurrenceProcessId(processId, eventId, 0);
    }

    private IHost BuildHost(string settlementBaseUrl)
    {
        var options = new SagaCommandDispatcherOptions
        {
            ConnectionString = ConnectionString,
            EngineBaseUrl = "http://engine.invalid",
            SettlementBaseUrl = settlementBaseUrl,
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

    private async Task DrainUntilTerminalAsync(RecordingHttpServer acl, Guid messageId)
    {
        using var host = BuildHost(acl.BaseUrl);
        await host.StartAsync();
        try
        {
            await WaitUntilAsync(
                async () =>
                {
                    var status = await StatusAsync(messageId);
                    return status is "PUBLISHED" or "FAILED";
                },
                TimeSpan.FromSeconds(30),
                "the dispatcher did not reach a terminal status for the settlement cash leg");
        }
        finally
        {
            await host.StopAsync();
        }
    }

    private async Task<Guid> OutboxMessageIdAsync(Guid processId, string commandType)
    {
        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT message_id FROM saga_outbox WHERE process_id = @p AND command_type = @t ORDER BY seq DESC LIMIT 1;",
            connection);
        command.Parameters.AddWithValue("p", processId);
        command.Parameters.AddWithValue("t", commandType);
        return (Guid)(await command.ExecuteScalarAsync())!;
    }

    private async Task<string> StatusAsync(Guid messageId)
    {
        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT status FROM saga_outbox WHERE message_id = @id;", connection);
        command.Parameters.AddWithValue("id", messageId);
        return (string)(await command.ExecuteScalarAsync())!;
    }

    /// <summary>Model a fresh SCA re-attestation on the SAME retriable-PENDING outbox row (same process_id,
    /// same seq): stamp fresh <c>sca_acr</c> / <c>sca_auth_time</c> so the dispatcher's next re-drive
    /// forwards a proof the receiver accepts. Proves the leg re-drives IN PLACE, never as a new occurrence
    /// (ADR-PC-043).</summary>
    private async Task RestampRowScaAsync(Guid messageId, string scaAcr, long scaAuthTime)
    {
        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand(
            "UPDATE saga_outbox SET sca_acr = @acr, sca_auth_time = @at WHERE message_id = @id;", connection);
        command.Parameters.AddWithValue("acr", scaAcr);
        command.Parameters.AddWithValue("at", scaAuthTime);
        command.Parameters.AddWithValue("id", messageId);
        await command.ExecuteNonQueryAsync();
    }

    private async Task<int?> FailureStatusCodeAsync(Guid messageId)
    {
        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT failure_status_code FROM saga_outbox WHERE message_id = @id;", connection);
        command.Parameters.AddWithValue("id", messageId);
        var raw = await command.ExecuteScalarAsync();
        return raw is null or DBNull ? null : (int)raw;
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
