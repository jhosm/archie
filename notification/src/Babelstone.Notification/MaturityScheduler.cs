using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Babelstone.Notification;

/// <summary>
/// The downstream maturity scheduler (ADR-PC-023 §6 / ADR-PC-025) — the clock-owning component the
/// engine deliberately does NOT contain. In plain terms: the engine knows every deposit's maturity
/// date but never says "that date is now close" (it has no clock — ADR-PC-023); this scheduler reads
/// the maturity calendar as-of today, spots the deposits entering the final 14-day pre-maturity
/// opt-out window (02 §2.4.4), and decides a renewal reminder is due — and it is built so that running
/// it twice over the same calendar never double-notifies.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the clock lives HERE, not in the engine.</b> ADR-PC-023 §D removes every clock-driven signal
/// from the engine: a signal whose only cause is "a date arrived" (maturity is approaching) has no
/// causing domain fact, so it cannot be a replayable engine event. The engine instead exposes the
/// maturity-calendar projection and lets this downstream consumer own the question and the clock
/// (ADR-PC-023 §2/§6). The CI determinism gate (<c>DETERMINISM_GATE</c> / <c>NO_CLOCK_DRIVEN_ENGINE_SIGNAL</c>)
/// constrains only engine FOLDS and the engine's emit path — it does NOT constrain this clock-owning
/// component, which is the intended home for the clock (NOTIF-1 / ADR-PC-023 §6 ownership).
/// </para>
/// <para>
/// <b>Idempotency by construction (ADR-PC-025 slot 4).</b> Each due reminder is keyed by a STABLE
/// composite <c>notification_id = instance_id + template_ref + schedule-occurrence-id</c>. For a
/// temporal (SCHEDULED) maturity reminder there is no causing domain event, so the triggering
/// occurrence is the maturity occurrence itself — the deposit's <c>maturity_date</c> — which is fixed
/// on the deposit and identical across re-reads and projection refreshes. Re-running the loop over the
/// same calendar state therefore re-derives the SAME <c>notification_id</c> for the same deposit, so
/// the dedupe set (the consumer-side "already raised this one" memory) absorbs the repeat without a
/// second decision — exactly the slot-4 guarantee, moved to the producer side because v1 carries no
/// emission leg yet (that is bd babelstone-60n8.3). The dedupe ledger is injected so a future emission
/// child can back it with a durable store; the in-memory default proves the invariant.
/// </para>
/// <para>
/// <b>Family-agnostic over the HTTP contract (NOTIF-1 / ADR-IC-019 §D2/§D3).</b> The scheduler reads
/// the calendar only through <see cref="DepositReadClient"/> over the storage-opaque ADR-PC-027 /
/// ADR-IC-005 range-scan resource — it names no family type and takes no engine-kernel dependency.
/// </para>
/// </remarks>
public sealed class MaturityScheduler(
    DepositReadClient depositReadClient,
    INotificationDedupeLedger dedupeLedger,
    ILogger<MaturityScheduler>? logger = null)
{
    /// <summary>
    /// The pre-maturity opt-out window width in days (02 §2.4.4 — "typically the final 14 days before
    /// maturity"). The PT pack carries the canonical value (the engine reads it fail-loud from
    /// <c>AutoRenewalOptoutWindowDays</c>); v1's downstream scheduler uses the documented default.
    /// </summary>
    /// <remarks>
    /// A deposit is "in the window" exactly when the engine's saga-start gate would call it in-window:
    /// the window OPENS at <c>maturity_date − 14 days</c> (the engine's <c>optOutWindowOpens</c>,
    /// TermDepositConstitutionService §3a), so a deposit is in-window when
    /// <c>maturity_date − 14 days &lt;= asOf</c>, i.e. <c>maturity_date &lt;= asOf + 14</c>. As the
    /// engine's range-scan resource is half-open <c>[from, to)</c>, catching every maturity up to AND
    /// INCLUDING <c>asOf + 14</c> means scanning <c>[asOf, asOf + 15)</c> — matching the engine's
    /// inclusive opening boundary rather than excluding the very first day the opt-out right exists.
    /// </remarks>
    public const int OptOutWindowDays = 14;

    /// <summary>The pack-namespaced template for a maturity reminder (ADR-PC-025 slot 1 example
    /// <c>pt.notice.maturity</c>). One of the three parts of the composite notification key.</summary>
    public const string MaturityTemplateRef = "pt.notice.maturity";

    private readonly DepositReadClient _depositReadClient =
        depositReadClient ?? throw new ArgumentNullException(nameof(depositReadClient));

    private readonly INotificationDedupeLedger _dedupeLedger =
        dedupeLedger ?? throw new ArgumentNullException(nameof(dedupeLedger));

    /// <summary>
    /// Run ONE scheduling pass as-of <paramref name="asOf"/>: read the maturity calendar for the
    /// <c>[asOf, asOf + 14 days)</c> window, and for each Active deposit entering the window that has
    /// not already been raised (dedupe on the composite <c>notification_id</c>), produce a
    /// <see cref="MaturityNotificationDecision"/>. Returns only the NEW decisions this pass — running
    /// it again over the same calendar returns an empty list (the dedupe ledger remembers them), which
    /// is the slot-4 "re-runs don't re-notify" guarantee.
    /// </summary>
    /// <param name="asOf">Today, supplied by the caller — the clock lives in the worker loop
    /// (ADR-PC-023 §6), never read inside this method, so the pass is deterministic for a given date
    /// and trivially testable.</param>
    public async Task<IReadOnlyList<MaturityNotificationDecision>> RunOnceAsync(
        DateOnly asOf, CancellationToken ct = default)
    {
        // The half-open window the engine's range-scan resource expects, matching the engine's own
        // opt-out gate: the window OPENS at maturity_date − 14 days, so a deposit maturing exactly
        // asOf + 14 is the FIRST day its opt-out right exists and must be caught. A half-open
        // [asOf, asOf + 15) scan includes maturity_date == asOf + 14 (the inclusive opening boundary)
        // and excludes asOf + 15 (which is still 15 days out — not yet in window). A deposit maturing
        // TODAY is in-window too (its window opened 14 days ago). See OptOutWindowDays.
        var windowEnd = asOf.AddDays(OptOutWindowDays + 1);

        var maturing = await _depositReadClient.ListMaturitiesAsync(asOf, windowEnd, ct);

        var decisions = new List<MaturityNotificationDecision>();
        foreach (var deposit in maturing)
        {
            // Defensive: the range scan is ordered by maturity date and bounded server-side, but a
            // non-Active deposit (already Matured / Renewed / Erased) is never a renewal-reminder
            // target — its opt-out window is moot. Skip rather than notify on a closed instance.
            if (!IsActive(deposit.Lifecycle))
            {
                continue;
            }

            var notificationId = ComputeNotificationId(deposit.DepositId, MaturityTemplateRef, deposit.MaturityDate);

            // Dedupe on the composite key (ADR-PC-025 slot 4): a key already in the ledger was raised
            // on a prior pass (or a projection refresh re-surfaced the same deposit), so re-deriving
            // the SAME key here is the expected idempotent case — not a second notification.
            if (!await _dedupeLedger.TryReserveAsync(notificationId, ct))
            {
                continue;
            }

            decisions.Add(new MaturityNotificationDecision(
                NotificationId: notificationId,
                InstanceId: deposit.DepositId,
                TemplateRef: MaturityTemplateRef,
                MaturityDate: deposit.MaturityDate,
                TotalPayoutCents: deposit.TotalPayoutCents,
                NetInterestCents: deposit.NetInterestCents,
                DueAt: asOf));
        }

        if (decisions.Count > 0)
        {
            logger?.LogInformation(
                "Maturity scheduler raised {Count} new pre-maturity reminder(s) as-of {AsOf} " +
                "(14-day opt-out window, ADR-PC-023/ADR-PC-025).", decisions.Count, asOf);
        }

        return decisions;
    }

    private static bool IsActive(string lifecycle) =>
        string.Equals(lifecycle, "Active", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The stable composite notification id (ADR-PC-025 slot 4): a deterministic UUIDv5-style hash of
    /// <c>instance_id + template_ref + schedule-occurrence-id</c>. For a temporal maturity reminder the
    /// schedule-occurrence-id is the deposit's <c>maturity_date</c> (the occurrence the date marks),
    /// which is fixed on the deposit — so the SAME three inputs always yield the SAME id across
    /// re-reads, projection refreshes, and process restarts. Computed from a SHA-256 over the
    /// canonical UTF-8 join, folded into a RFC-4122 v5 (name-based) GUID; no clock, no randomness, so
    /// the key is replay-stable exactly as slot 4 requires.
    /// </summary>
    public static Guid ComputeNotificationId(Guid instanceId, string templateRef, DateOnly scheduleOccurrence)
    {
        var canonical = $"{instanceId:D}|{templateRef}|{scheduleOccurrence:yyyy-MM-dd}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));

        // Take the first 16 bytes and stamp RFC-4122 version 5 (name-based, SHA-1/SHA-256) + the
        // variant bits, so the value is a well-formed, deterministic GUID — never a v4 random one.
        var bytes = new byte[16];
        Array.Copy(hash, bytes, 16);
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x50); // version 5
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80); // RFC-4122 variant
        return new Guid(bytes);
    }
}

/// <summary>
/// One "a pre-maturity renewal reminder is due" decision the scheduler produces (ADR-PC-025 slot 2
/// semantics: which template, with what structural values, on what date). It carries NO PII — the
/// recipient and any name/NIF a template interpolates are resolved by reference at render time
/// (ADR-PC-025 PII rule); these are the structural interpolation values only. v1 produces the DECISION;
/// turning it into an emitted <c>NotificationDue</c> over the outbox is the sibling child
/// bd babelstone-60n8.3.
/// </summary>
/// <param name="NotificationId">The stable composite idempotency key (ADR-PC-025 slot 4).</param>
/// <param name="InstanceId">The deposit (stream) the reminder is for.</param>
/// <param name="TemplateRef">The pack-namespaced template (<c>pt.notice.maturity</c>).</param>
/// <param name="MaturityDate">The deposit's scheduled maturity date (the occurrence driving the window).</param>
/// <param name="TotalPayoutCents">Total payout at maturity, integer cents (a template interpolation value).</param>
/// <param name="NetInterestCents">Net interest to date, integer cents (a template interpolation value).</param>
/// <param name="DueAt">The valid date of the decision — the as-of date the pass ran for.</param>
public sealed record MaturityNotificationDecision(
    Guid NotificationId,
    Guid InstanceId,
    string TemplateRef,
    DateOnly MaturityDate,
    long TotalPayoutCents,
    long NetInterestCents,
    DateOnly DueAt);

/// <summary>
/// The "already raised this one" memory the maturity scheduler dedupes against (ADR-PC-025 slot 4).
/// On the consumer/producer side a composite <c>notification_id</c> is reserved once; a second attempt
/// to reserve the same id is the idempotent replay the contract mandates and returns
/// <see langword="false"/>. Abstracted so the emission child (bd babelstone-60n8.3) can back it with a
/// durable store while v1's scheduler proves the invariant against the in-memory default.
/// </summary>
public interface INotificationDedupeLedger
{
    /// <summary>Reserve <paramref name="notificationId"/> if it is new to this ledger. Returns
    /// <see langword="true"/> the FIRST time an id is seen (the decision is new and should be raised),
    /// and <see langword="false"/> on every subsequent attempt (the idempotent replay — already raised).</summary>
    Task<bool> TryReserveAsync(Guid notificationId, CancellationToken ct = default);
}

/// <summary>
/// The in-memory <see cref="INotificationDedupeLedger"/> v1 uses to prove the slot-4 idempotency
/// invariant. Thread-safe (a single <see cref="HashSet{T}"/> behind a lock — the worker runs one pass
/// at a time, but reserving is cheap and the lock keeps a future concurrent pass honest). A durable,
/// crash-surviving ledger is the emission child's concern (bd babelstone-60n8.3); within one process
/// lifetime this gives the "re-runs don't re-notify" guarantee the acceptance criteria require.
/// </summary>
public sealed class InMemoryNotificationDedupeLedger : INotificationDedupeLedger
{
    private readonly HashSet<Guid> _seen = [];
    private readonly Lock _gate = new();

    public Task<bool> TryReserveAsync(Guid notificationId, CancellationToken ct = default)
    {
        lock (_gate)
        {
            return Task.FromResult(_seen.Add(notificationId));
        }
    }
}
