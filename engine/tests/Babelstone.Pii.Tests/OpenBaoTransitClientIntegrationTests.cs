using Babelstone.Pii;
using Xunit;

namespace Babelstone.Pii.Tests;

/// <summary>
/// The OpenBao transit seam (A.5) against a real OpenBao: encrypt/decrypt round-trip,
/// crypto-shred erasure (§P3), per-subject key isolation, and the envelope end-to-end.
/// </summary>
[Trait("Category", "Integration")]
public sealed class OpenBaoTransitClientIntegrationTests(OpenBaoFixture fixture)
    : IClassFixture<OpenBaoFixture>
{
    private static byte[] Plain(string s) => System.Text.Encoding.UTF8.GetBytes(s);

    [Fact]
    public async Task Encrypts_then_decrypts_under_the_subject_key()
    {
        var client = fixture.CreateClient();
        var subject = $"subject-{Guid.NewGuid():N}";

        var ciphertext = await client.EncryptAsync(subject, Plain("Ana Silva"));
        var plaintext = await client.DecryptAsync(subject, ciphertext);

        Assert.Equal("Ana Silva", System.Text.Encoding.UTF8.GetString(plaintext!));
    }

    [Fact]
    public async Task Destroying_the_key_makes_decrypt_return_null()
    {
        var client = fixture.CreateClient();
        var subject = $"subject-{Guid.NewGuid():N}";
        var ciphertext = await client.EncryptAsync(subject, Plain("123456789"));

        await client.DestroyKeyAsync(subject);

        Assert.Null(await client.DecryptAsync(subject, ciphertext)); // erased = unrecoverable (§P3)
    }

    [Fact]
    public async Task Destroy_is_idempotent()
    {
        var client = fixture.CreateClient();
        var subject = $"subject-{Guid.NewGuid():N}";
        await client.EncryptAsync(subject, Plain("x"));

        await client.DestroyKeyAsync(subject);
        await client.DestroyKeyAsync(subject); // second destroy must not throw
    }

    [Fact]
    public async Task A_subject_cannot_decrypt_another_subjects_ciphertext()
    {
        var client = fixture.CreateClient();
        var alice = $"subject-{Guid.NewGuid():N}";
        var bob = $"subject-{Guid.NewGuid():N}";
        var aliceCiphertext = await client.EncryptAsync(alice, Plain("Alice secret"));
        await client.EncryptAsync(bob, Plain("Bob secret")); // ensure bob's key exists

        // Bob's key cannot open Alice's ciphertext: transit rejects it (4xx → null).
        Assert.Null(await client.DecryptAsync(bob, aliceCiphertext));
    }

    [Fact]
    public async Task Envelope_seals_and_opens_then_erases_over_real_openbao()
    {
        var envelope = new PiiEnvelope(fixture.CreateClient());
        var keyStore = fixture.CreateClient();
        var subject = $"subject-{Guid.NewGuid():N}";
        PiiField[] fields = [new("customer_name", Plain("Ana Silva")), new("nif", Plain("123456789"))];

        var sealed_ = await envelope.SealAsync(subject, fields);
        var opened = await envelope.OpenAsync(subject, sealed_);
        Assert.Equal("Ana Silva", System.Text.Encoding.UTF8.GetString(opened[0]!.Plaintext));
        Assert.Equal("123456789", System.Text.Encoding.UTF8.GetString(opened[1]!.Plaintext));

        await keyStore.DestroyKeyAsync(subject);
        var afterErasure = await envelope.OpenAsync(subject, sealed_);
        Assert.All(afterErasure, field => Assert.Null(field));
    }
}
