using System.Security.Cryptography;
using System.Text;

namespace Babelstone.Notification.Delivery;

/// <summary>
/// The ADR-IC-011 §D3/§P2 webhook authenticity scheme — HMAC-SHA256 over
/// <c>"{timestamp}.{raw_body}"</c>. In plain terms: the receiver must be able to prove a delivery came
/// from the bank and is not a replay, so every outbound POST carries a signature computed from a shared
/// secret over the exact bytes sent, scoped by a unix timestamp the receiver bounds to a 5-minute window.
/// The scheme is the industry-standard one (GitHub/Stripe/Shopify shape) ADR-IC-011 chose; this type is
/// both the sender's signer and the receiver-side verifier the tests (and a .NET consumer) use.
/// </summary>
public static class WebhookSignature
{
    /// <summary>The signature header: <c>sha256=&lt;hex HMAC-SHA256(secret, "{timestamp}.{raw_body}")&gt;</c>
    /// (ADR-IC-011 §P2).</summary>
    public const string SignatureHeader = "X-Webhook-Signature";

    /// <summary>The unix-epoch-seconds timestamp in the signature scope (ADR-IC-011 §P2) — the receiver
    /// rejects a delivery older than its replay window (5 minutes, ADR-IC-011 §D3).</summary>
    public const string TimestampHeader = "X-Webhook-Timestamp";

    /// <summary>The subscription the delivery is for (ADR-IC-011 §P2).</summary>
    public const string SubscriptionIdHeader = "X-Webhook-Subscription-Id";

    /// <summary>The per-attempt delivery id (ADR-IC-011 §P2) — unique per attempt, for receiver logging;
    /// NOT the dedupe key (that is the payload's <c>idempotency_key</c>).</summary>
    public const string DeliveryIdHeader = "X-Webhook-Delivery-Id";

    /// <summary>The scheme prefix the signature header value carries (ADR-IC-011 §P2).</summary>
    public const string SchemePrefix = "sha256=";

    /// <summary>
    /// Compute the signature header VALUE for a delivery: <c>sha256=</c> + lowercase-hex
    /// HMAC-SHA256(<paramref name="secret"/>, <c>"{unixTimestampSeconds}.{rawBody}"</c>) over UTF-8 bytes
    /// (ADR-IC-011 §D3). The <paramref name="rawBody"/> MUST be the exact string sent on the wire — the
    /// receiver signs what it received, byte for byte.
    /// </summary>
    public static string Compute(string secret, long unixTimestampSeconds, string rawBody)
    {
        ArgumentException.ThrowIfNullOrEmpty(secret);
        ArgumentNullException.ThrowIfNull(rawBody);

        var key = Encoding.UTF8.GetBytes(secret);
        var payload = Encoding.UTF8.GetBytes(FormattableString.Invariant($"{unixTimestampSeconds}.{rawBody}"));
        var mac = HMACSHA256.HashData(key, payload);
        return SchemePrefix + Convert.ToHexStringLower(mac);
    }

    /// <summary>
    /// Receiver-side verification (ADR-IC-011 §D3 steps 2–3): recompute the signature from the shared
    /// secret and the received timestamp + raw body, and compare against the received header value in
    /// CONSTANT TIME (<see cref="CryptographicOperations.FixedTimeEquals"/> — no timing oracle on the
    /// secret). Replay-window bounding of the timestamp (step 1) is the caller's check — it needs the
    /// receiver's clock, which this pure function deliberately does not read.
    /// </summary>
    public static bool Verify(string secret, long unixTimestampSeconds, string rawBody, string signatureHeaderValue)
    {
        ArgumentNullException.ThrowIfNull(signatureHeaderValue);

        var expected = Encoding.UTF8.GetBytes(Compute(secret, unixTimestampSeconds, rawBody));
        var received = Encoding.UTF8.GetBytes(signatureHeaderValue);
        return CryptographicOperations.FixedTimeEquals(expected, received);
    }
}
