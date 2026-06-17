using Babelstone.Orchestrator.Inbox;
using Npgsql;

namespace Babelstone.Families.TermDeposit.Orchestration;

/// <summary>
/// The <see cref="ISagaCommandSink"/> for the <see cref="ConstitutionProcess"/> saga (H.2,
/// babelstone-n55u). It owns ONLY the constitution-specific command-payload assembly (the FULL typed
/// <see cref="CommandPayload"/> built from the saga's pinned <see cref="SagaBusinessReference"/>s); the
/// row write itself — appending to the substrate-owned <c>saga_outbox</c> store on the saga transaction —
/// is delegated to the substrate's <see cref="SagaOutboxWriter"/> (ADR-IC-018 §D2 names <c>saga_outbox</c>
/// a substrate store). The writer commits the row ATOMICALLY with the state move, the transition-history
/// row, and the inbox dedup row (ADR-IC-003 §P1 "saga-emitted commands use the same outbox mechanism as
/// all other services … not a separate publish path"). The drain (a relay, Epic E's mechanism) is the
/// only reader and is out of this issue's scope.
/// </summary>
/// <remarks>
/// <para>
/// <b>The payload is byte-stable (ADR-PC-010 §P5).</b> The body is built byte-stably from the seam's
/// references alone (process id, command type, identity trio, the pinned business references), so
/// re-emitting the same logical command yields identical payload bytes — NO <see cref="Guid.NewGuid"/>,
/// NO <see cref="DateTimeOffset"/>.<see cref="DateTimeOffset.UtcNow"/> inside. The ONE freshly minted
/// value — the delivery <c>message_id</c> a downstream consumer's inbox keys on — is minted by the
/// <see cref="SagaOutboxWriter"/> as an outbox COLUMN, never the body; the minting happens in the shell,
/// never inside any decider or fold.
/// </para>
/// <para>
/// <b>No PII on the row (ADR-PC-004 §P2 / no-PII-on-the-durable-bus).</b> Every column the writer sets —
/// process id, command type, causation/correlation references, the outbound <c>traceparent</c>, and the
/// structural payload — is a reference, never a NIF/IBAN/name/amount. The no-PII test asserts this with a
/// positive ALLOW-LIST over the written bytes, not a deny-list of forbidden patterns.
/// </para>
/// <para>
/// <b>Full business-reference payloads (bd babelstone-t7o3.1).</b> The sink builds the FULL typed
/// <see cref="CommandPayload"/> through <see cref="SagaCommandPayloadFactory"/> from the saga's pinned
/// <see cref="SagaBusinessReference"/>s — the ReserveAccountBalance body carries the real source account +
/// a derived reservation reference, the ActivateDeposit body the deposit/Core-txn references, and so on.
/// Every derived reference is a deterministic function of the process id, never a minted value.
/// </para>
/// <para>
/// <b>References are mandatory — fail-closed (bd babelstone-t7o3.9).</b> Every saga is started at the
/// edge (<c>EdgeSagaStarter</c>), which pins the business references in the SAME transaction as the
/// STARTED row, so they are ALWAYS present by the time any command is emitted. A saga that reaches
/// the sink with no pinned references throws rather than degrading to a minimal seam envelope — the
/// pre-production reference-less consume-loop fallback was removed (babelstone is not in production,
/// so no legacy start path needs preserving).
/// </para>
/// </remarks>
public sealed class SagaCommandOutboxSink(
    SagaBusinessReferenceStore? businessReferenceStore = null,
    SagaOutboxWriter? outbox = null) : ISagaTypedCommandSink
{
    private readonly SagaBusinessReferenceStore _businessReferenceStore =
        businessReferenceStore ?? new SagaBusinessReferenceStore();
    private readonly SagaOutboxWriter _outbox = outbox ?? new SagaOutboxWriter();

    /// <summary>The saga type this sink assembles command bodies for (bd babelstone-mtto PR2) — so the
    /// multi-saga <c>CompositeSagaCommandSink</c> routes the constitution saga's commands here.</summary>
    public string SagaType => ConstitutionProcess.Type;

    /// <inheritdoc />
    public async Task EmitAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid processId,
        string commandType,
        Guid causationMessageId,
        Guid? correlationId,
        CancellationToken ct = default,
        string? traceParent = null)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentException.ThrowIfNullOrWhiteSpace(commandType);

        // The LOGICAL payload body: the FULL typed command payload (the real account/deposit/Core
        // references) built through the pure factory from the saga's pinned business references. Every
        // saga is started at the edge, which pins those references in the SAME transaction as the
        // STARTED row (EdgeSagaStarter), so they are ALWAYS present here — a saga with none is a
        // fail-closed error, not a degraded seam-envelope path (bd babelstone-t7o3.9). The reference LOAD
        // runs on the saga transaction, so it sees the row the same transaction wrote at start.
        var reference = await _businessReferenceStore.LoadAsync(connection, transaction, processId, ct)
            ?? throw new InvalidOperationException(
                $"Saga {processId} has no pinned business references; every saga must be started at the edge " +
                $"(bd babelstone-t7o3.9). Cannot assemble the '{commandType}' command payload.");
        CommandPayload body =
            SagaCommandPayloadFactory.Build(commandType, processId, causationMessageId, correlationId, reference)
            ?? throw new InvalidOperationException(
                $"No command-payload recipe for '{commandType}' on saga {processId}; the factory must cover " +
                $"every command the state machine emits (bd babelstone-t7o3.9).");
        var payload = body.ToBytes();

        // The substrate's saga_outbox store owns the row write + the operational message_id mint
        // (ADR-IC-018 §D2; the row commits atomically on this saga transaction, ADR-IC-003 §P1). This
        // sink owns ONLY the family-specific payload assembly above.
        await _outbox.AppendAsync(
            connection, transaction, processId, commandType, causationMessageId, correlationId, payload, traceParent, ct);
    }
}
