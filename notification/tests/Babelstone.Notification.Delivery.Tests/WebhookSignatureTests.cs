using Babelstone.Notification.Delivery;
using Xunit;

namespace Babelstone.Notification.Delivery.Tests;

/// <summary>
/// Pins the ADR-IC-011 §D3 signature scheme: HMAC-SHA256 over <c>"{timestamp}.{raw_body}"</c> UTF-8
/// bytes, lowercase hex, <c>sha256=</c>-prefixed — verified against a fixed vector computed with an
/// independent implementation (Python <c>hmac</c>/<c>hashlib</c>), so a drift in the input framing (a
/// missing dot, a byte-order slip, an uppercase hex) fails against a value this codebase did not produce.
/// </summary>
public sealed class WebhookSignatureTests
{
    private const string Secret = "whsec_test_secret";
    private const long Timestamp = 1783036800;
    private const string Body = /*lang=json,strict*/ """{"idempotency_key":"3f2c8a4e-0000-4000-8000-000000000001"}""";

    // hmac.new(b'whsec_test_secret', b'1783036800.{...}', hashlib.sha256).hexdigest()
    private const string ExpectedVector =
        "sha256=ea0ae054db20aeb3ed0dd95d706e8ab6f8786dedf04f35a30a24343590c191cf";

    [Fact]
    public void Compute_matches_the_independent_vector()
    {
        Assert.Equal(ExpectedVector, WebhookSignature.Compute(Secret, Timestamp, Body));
    }

    [Fact]
    public void Verify_accepts_the_genuine_signature()
    {
        Assert.True(WebhookSignature.Verify(Secret, Timestamp, Body, ExpectedVector));
    }

    [Fact]
    public void Verify_rejects_a_tampered_body()
    {
        Assert.False(WebhookSignature.Verify(Secret, Timestamp, Body + " ", ExpectedVector));
    }

    [Fact]
    public void Verify_rejects_a_shifted_timestamp()
    {
        // The timestamp is IN the signature scope (§D3) — a replayed body under a fresh timestamp must
        // not verify against the old signature.
        Assert.False(WebhookSignature.Verify(Secret, Timestamp + 1, Body, ExpectedVector));
    }

    [Fact]
    public void Verify_rejects_the_wrong_secret()
    {
        Assert.False(WebhookSignature.Verify("whsec_other_secret", Timestamp, Body, ExpectedVector));
    }
}
