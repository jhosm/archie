namespace Babelstone.Notification.Delivery;

/// <summary>
/// The render-time PII resolve seam (ADR-PC-025). In plain terms: the bus never carries a
/// customer's name or NIF — only the opaque <c>customer_id</c> reference (the CLR signal's
/// <see cref="NotificationDueSignal.CustomerRef"/>) — so the renderer asks the
/// ENGINE for the PII it needs at the moment of rendering, uses it transiently for that one delivery
/// attempt, and discards it. The engine decrypts internally (OpenBao stays inside the engine boundary,
/// ADR-PC-004 §P2) and answers nothing for a crypto-shredded subject (§P3) — so an erased customer's
/// notice simply renders without PII, and nothing here can resurrect it.
/// </summary>
public interface IPiiResolveClient
{
    /// <summary>
    /// Resolve <paramref name="fields"/> (e.g. <c>name</c>, <c>nif</c>) for the subject behind
    /// <paramref name="subjectRef"/>. Returns the resolved plaintext by field name; a crypto-shredded or
    /// unknown subject resolves to an EMPTY map (never an error — erasure is a normal outcome,
    /// ADR-PC-004 §P3). A transport failure throws: the resolve surface being down is transient
    /// backpressure the delivery pass retries later (ADR-PC-025 residual — "retry the render later").
    /// </summary>
    Task<IReadOnlyDictionary<string, string>> ResolveAsync(
        Guid subjectRef, IReadOnlyList<string> fields, CancellationToken ct = default);
}
