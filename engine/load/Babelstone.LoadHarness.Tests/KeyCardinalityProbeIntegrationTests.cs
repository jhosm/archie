using Babelstone.Pii;
using Xunit;

namespace Babelstone.LoadHarness.Tests;

/// <summary>
/// The §M.6 key-cardinality seed+measure pass against a REAL (dev-mode) OpenBao (bd c14p.2). Seeds a
/// bounded population of per-subject transit keys through the engine's OWN
/// <see cref="OpenBaoTransitClient"/> and asserts the encrypt/decrypt/destroy latency stays FLAT — judged
/// on the median, see <see cref="KeyCardinalityReport.LatencyIsFlat"/> — as the resident key count grows:
/// the falsifiable signal that per-subject named keys (ADR-PC-004 §P2/§P3) scale without per-key op
/// degradation.
/// </summary>
/// <remarks>
/// <para>
/// In plain English: this actually makes a pile of per-customer keys in a real OpenBao and times the
/// encrypt / decrypt / forget operations as the pile grows, proving the times do not balloon. The CI N
/// is small (hundreds), enough to expose a per-key slope cheaply; the production sizing run dials N up to
/// v4 cardinality (millions) on a Raft-backed cluster, where the snapshot/unseal dimensions this dev-mode
/// container cannot show become measurable — see the ADR-PC-004 / ADR-PC-005 residual-risk amendments.
/// </para>
/// <para>
/// Carries <c>[Trait("Category", "Integration")]</c> so the default unit lane skips it (it needs Docker
/// + OpenBao); the CI integration lane (<c>--filter "Category=Integration"</c>) runs it.
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
public sealed class KeyCardinalityProbeIntegrationTests(OpenBaoCardinalityFixture fixture)
    : IClassFixture<OpenBaoCardinalityFixture>
{
    [Fact]
    public async Task Seeding_per_subject_keys_keeps_op_latency_flat_as_cardinality_grows()
    {
        var probe = new KeyCardinalityProbe(fixture.CreateKeyStore());

        // A bounded CI population: 400 resident per-subject keys, a latency checkpoint every 100 — four
        // checkpoints, enough to expose a per-key slope without a multi-minute run. The production sizing
        // pass dials totalSubjects up to v4 cardinality on a Raft-backed cluster.
        //
        // sampleSize 100 + a 10-cycle warm-up per checkpoint hardens the probe against CI-runner noise
        // (bd babelstone-tihv): the slope verdict is judged on the MEDIAN, which a single slow round-trip
        // to the dev-mode container cannot move, and the warm-up discards cold-start/connection-pool spikes
        // so the first checkpoint is a fair baseline. 100 samples also make the REPORTED p99 a genuine
        // percentile (the 2nd-slowest), not the single slowest request a 15-sample p99 degenerated to —
        // which is what flipped this test FLAT→DEGRADING on jitter (e.g. PR #316).
        var report = await probe.SeedAndMeasureAsync(
            totalSubjects: 400, checkpointEvery: 100, seed: 4242, sampleSize: 100, warmupIterations: 10);

        Assert.Equal(4, report.Checkpoints.Count);
        // Op latency must not degrade materially with cardinality — the per-subject-named-key invariant.
        Assert.True(report.LatencyIsFlat(),
            $"per-key op latency degraded with cardinality: {report.Summary()}");
        Assert.Contains("FLAT", report.Summary());
    }

    [Fact]
    public async Task Each_seeded_subject_is_an_independently_destroyable_shred_root()
    {
        // The cardinality cost buys the crypto-shred property (ADR-PC-004 §P3): each per-subject key is
        // its own destroyable unit, so erasing ONE subject leaves every other subject's key — and PII —
        // intact. This is exactly the property a shared-KEK envelope scheme would BREAK, which is why key
        // count cannot simply be collapsed (the c14p.2 crux).
        var keyStore = fixture.CreateKeyStore();
        var alice = $"shred-{Guid.NewGuid():N}";
        var bob = $"shred-{Guid.NewGuid():N}";
        var aliceCiphertext = await keyStore.EncryptAsync(alice, "Ana Silva"u8.ToArray());
        var bobCiphertext = await keyStore.EncryptAsync(bob, "Bruno Costa"u8.ToArray());

        await keyStore.DestroyKeyAsync(alice); // erase ONLY Alice

        // Alice's ciphertext is now unrecoverable — her key is gone (the post-erasure state, §P3).
        Assert.Null(await keyStore.DecryptAsync(alice, aliceCiphertext));
        // Bob is untouched — his per-subject key survives Alice's erasure.
        var bobPlain = await keyStore.DecryptAsync(bob, bobCiphertext);
        Assert.Equal("Bruno Costa", System.Text.Encoding.UTF8.GetString(bobPlain!));
    }
}
