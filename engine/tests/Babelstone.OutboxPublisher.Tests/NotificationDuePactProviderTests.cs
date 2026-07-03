using Avro;
using Avro.Generic;
using Avro.IO;
using Babelstone.Engine.Avro;
using PactNet.Verifier;
using Xunit;

namespace Babelstone.OutboxPublisher.Tests;

/// <summary>
/// The FORMAL Pact CDC producer half of the notify emit contract (ADR-IC-009) — the engine-side
/// verification of the committed
/// <c>contracts/pact/notification-delivery-engine.json</c> the notification-delivery consumer
/// generates (<c>NotificationDueMessagePactTests</c>). In plain English: the consumer wrote down what
/// an EVENT_DRIVEN <c>NotificationDue</c> message must look like — identity fields non-null, money as
/// integer-cent strings, the governed trigger symbol — and THIS test proves the engine's side of the
/// bargain by producing that message through the engine's own machinery and letting the real Pact
/// verifier match it. This is the AUTHORITATIVE behavioural gate the G.6
/// <c>EmitContractFitnessTests</c> defer to in their doc-comments: G.6 proves no synchronous notify
/// port exists and emission rides the outbox; this proves the emitted SHAPE meets the consumer.
/// </summary>
/// <remarks>
/// <para>
/// <b>What is verified.</b> The scenario builds the representative payload as a
/// <see cref="GenericRecord"/> against the ENGINE-EMBEDDED governed schema (the
/// <see cref="AvroSchemaCatalog"/> copy of <c>contracts/avro/operations/NotificationDue.avsc</c>)
/// and ROUND-TRIPS it through the real binary codec (write + read) before projecting the decoded
/// view for Pact — the ADR-IC-009 "verified against messages produced by the real Avro serializer"
/// obligation, scoped to what the engine owns: the schema and the codec (the engine has no runtime
/// EVENT_DRIVEN <c>NotificationDue</c> emission path).
/// </para>
/// <para>
/// <b>Pact source: the committed file, not a live broker.</b> The PR lane stays hermetic (the same
/// stance as every other contracts gate): the consumer test drift-locks the committed pact, and this
/// verifier reads it from disk. The dev-stack Pact Broker (ADR-IC-009, <c>make pact-broker-up</c> /
/// <c>make pact-publish</c>) carries the same artefact for humans and cross-repo consumers.
/// </para>
/// <para>
/// <b>PactNet 4.5, deliberately</b> — 5.0.x fails message producer-verification with "builder error
/// for url (message://…)" (pact-foundation/pact-net#530); see Directory.Packages.props.
/// </para>
/// </remarks>
public sealed class NotificationDuePactProviderTests
{
    /// <summary>The catalogue Test ID this producer-verification anchors (with the consumer half in
    /// <c>Babelstone.Notification.Delivery.Tests</c>).</summary>
    public const string TestId = "NOTIFY_EMIT_PACT";

    private const string Provider = "engine";
    private const string Consumer = "notification-delivery";

    [Fact]
    public void Engine_produces_what_the_notification_delivery_consumer_pinned()
    {
        var pactFile = Path.Combine(RepoRoot(), "contracts", "pact", $"{Consumer}-{Provider}.json");
        Assert.True(File.Exists(pactFile), $"committed consumer pact not found: {pactFile}");

        using var verifier = new PactVerifier(new PactVerifierConfig());
        verifier
            .MessagingProvider(Provider)
            .WithProviderMessages(scenarios =>
            {
                scenarios.Add(
                    "a NotificationDue message for a matured term deposit",
                    () => DecodedViewOf(MaturedDepositNotificationDue()));
            })
            .WithFileSource(new FileInfo(pactFile))
            .Verify();
    }

    /// <summary>
    /// The representative EVENT_DRIVEN payload, built against the ENGINE-EMBEDDED governed schema and
    /// round-tripped through the real Avro binary codec — encode with <see cref="GenericDatumWriter{T}"/>,
    /// decode with <see cref="GenericDatumReader{T}"/> — so what Pact matches is what the wire carries
    /// (minus the Confluent framing the relay adds, which is byte-plumbing, not shape).
    /// </summary>
    private static GenericRecord MaturedDepositNotificationDue()
    {
        var schema = new AvroSchemaCatalog().ForRecordName("NotificationDue").Schema;

        var record = new GenericRecord(schema);
        record.Add("notification_id", Guid.Parse("018f3c1a-2f66-7c3e-9c9a-5b8f0d9e4a01"));
        record.Add("instance_id", Guid.Parse("2b6f2e8c-6f9d-4b7e-8a2c-9d1e0f3a5b7c"));
        record.Add("customer_id", Guid.Parse("7c1d9a3e-5b2f-4e8d-9c6a-1f0e3d5a7b9c"));
        record.Add("template_ref", "pt.notice.maturity");
        record.Add("template_pack_version", "pt.2026.1");
        record.Add("trigger_kind", new GenericEnum((EnumSchema)schema["trigger_kind"].Schema, "EVENT_DRIVEN"));
        record.Add("causation_id", Guid.Parse("5e8d7c1d-9a3e-4b2f-8a2c-3d5a7b9c1f0e"));
        record.Add("data", new Dictionary<string, object>
        {
            ["principal_cents"] = "1000000",   // integer-cent STRING (ADR-PC-010 — money is never a float)
            ["maturity_date"] = "2026-09-01",
        });
        record.Add("due_at", new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc)); // Avro date logical

        // The REAL codec round trip: what comes back is what a consumer decodes off the bus.
        using var stream = new MemoryStream();
        new GenericDatumWriter<GenericRecord>(schema).Write(record, new BinaryEncoder(stream));
        stream.Position = 0;
        return new GenericDatumReader<GenericRecord>(schema, schema).Read(default!, new BinaryDecoder(stream));
    }

    /// <summary>
    /// The agreed JSON projection of a decoded <c>NotificationDue</c> (the same one the consumer pact
    /// speaks — see <c>NotificationDueMessagePactTests</c>): uuids as canonical strings, the enum as
    /// its symbol, <c>data</c> as a string map, <c>due_at</c> as <c>yyyy-MM-dd</c>.
    /// </summary>
    private static Dictionary<string, object?> DecodedViewOf(GenericRecord decoded) => new()
    {
        ["notification_id"] = ((Guid)decoded["notification_id"]).ToString("D"),
        ["instance_id"] = ((Guid)decoded["instance_id"]).ToString("D"),
        ["customer_id"] = ((Guid)decoded["customer_id"]).ToString("D"),
        ["template_ref"] = (string)decoded["template_ref"],
        ["template_pack_version"] = (string)decoded["template_pack_version"],
        ["trigger_kind"] = ((GenericEnum)decoded["trigger_kind"]).Value,
        ["causation_id"] = decoded["causation_id"] is Guid causation ? causation.ToString("D") : null,
        ["data"] = ((IDictionary<string, object>)decoded["data"])
            .ToDictionary(kv => kv.Key, kv => (string)kv.Value),
        ["due_at"] = ((DateTime)decoded["due_at"]).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
    };

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "contracts", "avro", "operations", "NotificationDue.avsc")))
            {
                return dir.FullName;
            }

            dir = dir.Parent!;
        }

        throw new InvalidOperationException(
            $"repo root (containing contracts/avro) not found from {AppContext.BaseDirectory}");
    }
}
