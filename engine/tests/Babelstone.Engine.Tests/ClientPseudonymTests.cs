using Babelstone.Telemetry;
using Xunit;

namespace Babelstone.Engine.Tests;

/// <summary>
/// OBS_NO_PII_ATTRS extension (OBS-3 / ADR-IC-016 plane iii §8): the span-attribute pseudonymizer.
/// A span that needs to reference a customer for debugging carries
/// <see cref="BabelstoneAttributes.SubjectPseudonym"/> — a salted, one-way hash — never the raw
/// <c>client_id</c>. These tests pin the security-relevant properties the ADR's residual-risk note
/// calls out: deterministic (so a customer's spans correlate), salt-dependent and one-way (so the
/// trace backend is not a reversible personal-data index), and fail-loud on a missing salt (so a
/// degraded un-keyed token can never be produced silently).
/// </summary>
public sealed class ClientPseudonymTests
{
    private const string Salt = "an-example-hmac-salt-from-the-secret-boundary";

    [Fact]
    public void Pseudonym_is_deterministic_for_the_same_salt_and_client()
    {
        var a = ClientPseudonym.Of("client-42", Salt);
        var b = ClientPseudonym.Of("client-42", Salt);

        Assert.Equal(a, b);
    }

    [Fact]
    public void Different_clients_get_different_pseudonyms()
    {
        var a = ClientPseudonym.Of("client-42", Salt);
        var b = ClientPseudonym.Of("client-43", Salt);

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void A_rotated_salt_changes_the_pseudonym_for_the_same_client()
    {
        var underOldSalt = ClientPseudonym.Of("client-42", Salt);
        var underNewSalt = ClientPseudonym.Of("client-42", "a-rotated-salt");

        // The salt is the HMAC key: change it and the token changes — the property that makes the
        // salt a true secret and the mapping reversible only inside the store holding that salt.
        Assert.NotEqual(underOldSalt, underNewSalt);
    }

    [Fact]
    public void Pseudonym_does_not_contain_the_raw_client_id()
    {
        const string clientId = "PT-NIF-501234567";
        var pseudonym = ClientPseudonym.Of(clientId, Salt);

        // One-way: the raw id must not be recoverable by substring — the whole point of attaching a
        // hash rather than the id.
        Assert.DoesNotContain(clientId, pseudonym);
    }

    [Fact]
    public void Pseudonym_is_short_lowercase_hex()
    {
        var pseudonym = ClientPseudonym.Of("client-42", Salt);

        Assert.Equal(ClientPseudonym.PseudonymHexLength, pseudonym.Length);
        Assert.All(pseudonym, c => Assert.True(Uri.IsHexDigit(c) && !char.IsUpper(c)));
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void Empty_or_null_salt_fails_loud_rather_than_producing_an_unkeyed_token(string? badSalt)
    {
        // An empty salt would silently yield a reversible, un-keyed token (ADR-IC-016 §8 residual
        // risk). The derivation refuses rather than degrade.
        Assert.Throws<ArgumentException>(() => ClientPseudonym.Of("client-42", badSalt!));
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void Empty_or_null_client_id_fails_loud(string? badClient)
    {
        Assert.Throws<ArgumentException>(() => ClientPseudonym.Of(badClient!, Salt));
    }
}
