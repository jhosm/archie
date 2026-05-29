using System.Text;
using Babelstone.Pii;
using Xunit;

namespace Babelstone.Pii.Tests;

/// <summary>
/// Envelope logic over a fake key store — runs in the default lane (no OpenBao). These
/// pin the §P3 erasure behaviour independently of the transit transport.
/// </summary>
public sealed class PiiEnvelopeTests
{
    /// <summary>In-memory IPiiKeyStore: a trivial reversible "cipher" so the envelope's field handling is what's under test.</summary>
    private sealed class FakeKeyStore : IPiiKeyStore
    {
        private readonly HashSet<string> _destroyed = [];

        public Task<byte[]> EncryptAsync(string subjectId, byte[] plaintext, CancellationToken ct = default)
            => Task.FromResult(Wrap(subjectId, plaintext));

        public Task<byte[]?> DecryptAsync(string subjectId, byte[] ciphertext, CancellationToken ct = default)
            => Task.FromResult(_destroyed.Contains(subjectId) ? null : Unwrap(ciphertext));

        public Task DestroyKeyAsync(string subjectId, CancellationToken ct = default)
        {
            _destroyed.Add(subjectId);
            return Task.CompletedTask;
        }

        private static byte[] Wrap(string subjectId, byte[] plaintext)
            => Encoding.UTF8.GetBytes($"{subjectId}:").Concat(plaintext).ToArray();

        private static byte[] Unwrap(byte[] ciphertext)
        {
            var separator = Array.IndexOf(ciphertext, (byte)':');
            return ciphertext[(separator + 1)..];
        }
    }

    private static readonly PiiField Name = new("customer_name", "Ana Silva"u8.ToArray());
    private static readonly PiiField Nif = new("nif", "123456789"u8.ToArray());

    [Fact]
    public async Task Seals_and_opens_round_trips_the_fields()
    {
        var envelope = new PiiEnvelope(new FakeKeyStore());
        var subject = "subject-1";

        var sealed_ = await envelope.SealAsync(subject, [Name, Nif]);
        var opened = await envelope.OpenAsync(subject, sealed_);

        Assert.Equal("customer_name", opened[0]!.Name);
        Assert.Equal(Name.Plaintext, opened[0]!.Plaintext);
        Assert.Equal(Nif.Plaintext, opened[1]!.Plaintext);
    }

    [Fact]
    public async Task Destroying_the_subject_key_erases_every_field()
    {
        var keyStore = new FakeKeyStore();
        var envelope = new PiiEnvelope(keyStore);
        var subject = "subject-2";

        var sealed_ = await envelope.SealAsync(subject, [Name, Nif]);
        await keyStore.DestroyKeyAsync(subject);
        var opened = await envelope.OpenAsync(subject, sealed_);

        Assert.All(opened, field => Assert.Null(field)); // erased = unrecoverable
    }
}
