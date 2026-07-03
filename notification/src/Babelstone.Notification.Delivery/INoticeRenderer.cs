using Microsoft.Extensions.Logging;

namespace Babelstone.Notification.Delivery;

/// <summary>
/// A rendered notice — the instance-pinned pack template's field set fully materialised for one delivery
/// attempt: the signal's structural values plus the PII resolved at render time. TRANSIENT BY CONTRACT
/// (ADR-PC-025 §PII): this value rides one webhook POST and is discarded; it is never written to the
/// outbox, a log, or any durable medium — PII materialises at the consumer, at the moment of use, only.
/// </summary>
/// <param name="TemplateRef">The pack-namespaced template the fields fill (pack-owned, ADR-PC-007).</param>
/// <param name="TemplatePackVersion">The pack version pinned on the instance (ADR-PC-007/ADR-PC-009).</param>
/// <param name="Fields">The full interpolation set: the signal's structural <c>data</c> merged with the
/// render-time-resolved PII fields.</param>
/// <param name="PiiResolved">Whether any PII resolved — <see langword="false"/> for a crypto-shredded
/// subject (ADR-PC-004 §P3) or a signal carrying no recipient reference, so the consumer knows the notice
/// is structurally complete but unaddressed.</param>
public sealed record RenderedNotice(
    string TemplateRef,
    string TemplatePackVersion,
    IReadOnlyDictionary<string, string> Fields,
    bool PiiResolved);

/// <summary>
/// Renders an EVENT_DRIVEN notice for delivery (bd babelstone-60n8.7): resolve the subject's PII by
/// reference at render time (never from the bus payload — ADR-PC-025 §PII) and produce the transient
/// <see cref="RenderedNotice"/> the webhook carries. Called per delivery ATTEMPT, inside the drain — a
/// failed resolve fails only that attempt, which retries on the §D4 backoff (the "retry the render later"
/// posture ADR-PC-025 names).
/// </summary>
public interface INoticeRenderer
{
    /// <summary>Render <paramref name="signal"/> for one delivery attempt. Throws on a transient resolve
    /// failure (the attempt retries); a shredded/absent subject renders without PII, never throws.</summary>
    Task<RenderedNotice> RenderAsync(NotificationDueSignal signal, CancellationToken ct = default);
}

/// <summary>
/// The v1 <see cref="INoticeRenderer"/>: merge the signal's structural <c>data</c> with the
/// render-time-resolved PII fields into the template's full interpolation set. STRUCTURAL rendering only,
/// deliberately — the pack ships the templates' DECLARATIVE half (the <c>interpolates</c> field names; bd
/// babelstone-oyts), so there is no body text to typeset yet: "rendering" v1 = assembling every value the
/// declared template interpolates, which is exactly what the downstream channel needs to produce the
/// letter. A body-producing renderer lands with the pack's rendering half, behind this same port.
/// </summary>
public sealed class PiiResolvingNoticeRenderer(
    IPiiResolveClient piiResolveClient,
    WebhookDeliveryOptions options,
    ILogger<PiiResolvingNoticeRenderer>? logger = null) : INoticeRenderer
{
    private readonly IPiiResolveClient _piiResolveClient =
        piiResolveClient ?? throw new ArgumentNullException(nameof(piiResolveClient));

    private readonly WebhookDeliveryOptions _options = options ?? throw new ArgumentNullException(nameof(options));

    /// <inheritdoc />
    public async Task<RenderedNotice> RenderAsync(NotificationDueSignal signal, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(signal);

        // Structural values first (ADR-PC-025 Decision 1) …
        var fields = new Dictionary<string, string>(signal.Data, StringComparer.Ordinal);

        // … then the PII, resolved by reference at render time and merged transiently (§PII). A signal
        // with no recipient reference renders structurally (the v1 SCHEDULED leg's shape).
        var piiResolved = false;
        if (signal.CustomerRef is { } subjectRef)
        {
            var pii = await _piiResolveClient.ResolveAsync(subjectRef, _options.PiiFields, ct);
            foreach (var (field, value) in pii)
            {
                fields[field] = value; // PII wins a (misconfigured) structural key collision — it is the fresher truth
            }

            piiResolved = pii.Count > 0;
            if (!piiResolved)
            {
                logger?.LogInformation(
                    "No PII resolved for notification {NotificationId} (shredded or unknown subject); "
                    + "rendering structurally (ADR-PC-004 §P3).", signal.NotificationId);
            }
        }

        return new RenderedNotice(signal.TemplateRef, signal.TemplatePackVersion, fields, piiResolved);
    }
}
