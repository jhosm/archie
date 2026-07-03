using Newtonsoft.Json.Linq;
using PactNet;
using PactNet.Matchers;
using Xunit;

namespace Babelstone.Notification.Delivery.Tests;

/// <summary>
/// The FORMAL Pact CDC consumer half of the notify emit contract (ADR-IC-009) — the AUTHORITATIVE
/// behavioural gate the G.6 emit-contract fitness tests defer to in their doc-comments. In plain
/// English: the notification-delivery estate is the CONSUMER of the engine's
/// EVENT_DRIVEN <c>NotificationDue</c> messages; this test declares, as a real PactV3 message pact,
/// exactly what a consumable message looks like — identity fields present and non-null, money as
/// positive integer cents, the governed trigger taxonomy — proves the consumer's own binding against a
/// sample, and DRIFT-CHECKS the generated pact against the committed
/// <c>contracts/pact/notification-delivery-engine.json</c> the engine's producer verification runs
/// against (<c>NotificationDuePactProviderTests</c> in the engine CI job).
/// </summary>
/// <remarks>
/// <para>
/// <b>The pact speaks the DECODED view of the Avro payload.</b> The wire is Confluent-framed Avro
/// (ADR-IC-002); a message pact pins the logical content, so both sides agree on one JSON projection:
/// uuids as canonical strings, the enum as its SCREAMING_SNAKE_CASE symbol, <c>data</c> as a
/// string→string map, <c>due_at</c> as <c>yyyy-MM-dd</c>. The producer side builds this projection by
/// round-tripping a representative payload through the real Avro codec against the governed
/// <c>.avsc</c> — so the pact behaviourally gates what the schema-level gates (SR compat, shape-lock,
/// the Pact-STYLE <c>NotificationDuePactConsumerTests</c>) only structurally imply.
/// </para>
/// <para>
/// <b>Why PactNet 4.5.</b> The 5.0.x line fails message producer-verification with "builder error
/// for url (message://…)" (pact-foundation/pact-net#530); the 4.5 line runs both halves green — see
/// Directory.Packages.props.
/// </para>
/// <para>
/// <b>Regenerating.</b> The committed pact is the artefact of record (published to the dev-stack Pact
/// Broker via <c>make pact-publish</c>, ADR-IC-009). After a DELIBERATE contract change, run this
/// test with <c>BABELSTONE_PACT_UPDATE=1</c> to rewrite <c>contracts/pact/</c>, and commit the diff.
/// </para>
/// </remarks>
public sealed class NotificationDueMessagePactTests
{
    /// <summary>Pact participant names: the delivery estate consumes; the engine produces the
    /// EVENT_DRIVEN leg (the SCHEDULED leg is self-produced and self-consumed — no cross-team seam,
    /// so no pact; ADR-IC-019).</summary>
    public const string Consumer = "notification-delivery";
    public const string Provider = "engine";

    private const string UuidRegex = "^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$";
    private const string CentsRegex = "^[0-9]+$";          // money-as-cents: digits only, never a float (ADR-IC-009)
    private const string IsoDateRegex = "^[0-9]{4}-[0-9]{2}-[0-9]{2}$";

    [Fact]
    public void Consumer_pact_binds_and_matches_the_committed_contract()
    {
        var pactDir = Path.Combine(Path.GetTempPath(), "babelstone-pacts", Guid.NewGuid().ToString("N"));
        var pact = Pact.V3(Consumer, Provider, new PactConfig { PactDir = pactDir });

        pact.WithMessageInteractions()
            .ExpectsToReceive("a NotificationDue message for a matured term deposit")
            .Given("a term deposit matured with a notification obligation")
            .WithJsonContent(new
            {
                // The identity set (ADR-IC-009: never null) — the dedupe key, the stream, the
                // recipient reference (opaque — PII resolved at render time, ADR-PC-025 Decision 1).
                notification_id = Match.Regex("018f3c1a-2f66-7c3e-9c9a-5b8f0d9e4a01", UuidRegex),
                instance_id = Match.Regex("2b6f2e8c-6f9d-4b7e-8a2c-9d1e0f3a5b7c", UuidRegex),
                customer_id = Match.Regex("7c1d9a3e-5b2f-4e8d-9c6a-1f0e3d5a7b9c", UuidRegex),
                // The instance-pinned pack template (ADR-PC-007/ADR-PC-009).
                template_ref = Match.Type("pt.notice.maturity"),
                template_pack_version = Match.Type("pt.2026.1"),
                // EXACT equality, not a type matcher: this pact is the EVENT_DRIVEN leg's contract
                // (the governed taxonomy symbol, ADR-PC-025).
                trigger_kind = "EVENT_DRIVEN",
                // EVENT_DRIVEN always traces to a causing domain event (ADR-PC-023).
                causation_id = Match.Regex("5e8d7c1d-9a3e-4b2f-8a2c-3d5a7b9c1f0e", UuidRegex),
                // Structural interpolation values only: amounts are INTEGER-CENT STRINGS (money is
                // never a float on any wire — ADR-PC-010 / ADR-IC-009), dates are ISO.
                data = new
                {
                    principal_cents = Match.Regex("1000000", CentsRegex),
                    maturity_date = Match.Regex("2026-09-01", IsoDateRegex),
                },
                due_at = Match.Regex("2026-09-01", IsoDateRegex),
            })
            .Verify<JObject>(BindLikeTheDeliveryEstate);

        var generated = Path.Combine(pactDir, $"{Consumer}-{Provider}.json");
        Assert.True(File.Exists(generated), $"PactNet wrote no pact file at {generated}");

        DriftCheckAgainstCommitted(generated);
    }

    /// <summary>
    /// The consumer-side binding proof: a bus-source implementation must construct
    /// <see cref="NotificationDueSignal"/> from exactly this message — ids parse as GUIDs, the
    /// trigger symbol maps through the governed <see cref="TriggerKindWire"/> vocabulary, amounts
    /// are integer cents.
    /// </summary>
    private static void BindLikeTheDeliveryEstate(JObject message)
    {
        var signal = new NotificationDueSignal(
            NotificationId: Guid.Parse((string)message["notification_id"]!),
            InstanceId: Guid.Parse((string)message["instance_id"]!),
            CustomerRef: Guid.Parse((string)message["customer_id"]!),
            TemplateRef: (string)message["template_ref"]!,
            TemplatePackVersion: (string)message["template_pack_version"]!,
            TriggerKind: TriggerKindWire.FromWire((string)message["trigger_kind"]!),
            CausationId: Guid.Parse((string)message["causation_id"]!),
            Data: message["data"]!.ToObject<Dictionary<string, string>>()!,
            DueAt: DateOnly.Parse((string)message["due_at"]!, System.Globalization.CultureInfo.InvariantCulture));

        Assert.NotEqual(Guid.Empty, signal.NotificationId);
        Assert.NotEqual(Guid.Empty, signal.InstanceId);
        Assert.Equal(NotificationTriggerKind.EventDriven, signal.TriggerKind);
        Assert.NotNull(signal.CausationId);
        Assert.All(
            signal.Data.Where(kv => kv.Key.EndsWith("_cents", StringComparison.Ordinal)),
            kv => Assert.Matches(CentsRegex, kv.Value));
    }

    /// <summary>
    /// The committed <c>contracts/pact/</c> artefact must equal what this consumer just generated —
    /// semantically (the <c>metadata</c> node, which carries tool versions, is excluded). A mismatch
    /// means the consumer's expectations moved without the committed contract (and therefore the
    /// producer verification) moving with them.
    /// </summary>
    private static void DriftCheckAgainstCommitted(string generatedPath)
    {
        var committedPath = Path.Combine(RepoRoot(), "contracts", "pact", $"{Consumer}-{Provider}.json");

        if (Environment.GetEnvironmentVariable("BABELSTONE_PACT_UPDATE") == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(committedPath)!);
            File.Copy(generatedPath, committedPath, overwrite: true);
        }

        Assert.True(
            File.Exists(committedPath),
            $"No committed pact at {committedPath}. Run this test once with BABELSTONE_PACT_UPDATE=1 and commit it.");

        var generated = Strip(JObject.Parse(File.ReadAllText(generatedPath)));
        var committed = Strip(JObject.Parse(File.ReadAllText(committedPath)));
        Assert.True(
            JToken.DeepEquals(generated, committed),
            "The generated consumer pact differs from the committed contracts/pact artefact. If the "
            + "contract change is deliberate, regenerate with BABELSTONE_PACT_UPDATE=1, commit the "
            + "diff, and expect the engine-side producer verification to answer for it.");

        static JObject Strip(JObject pact)
        {
            pact.Remove("metadata"); // tool-version bookkeeping, not contract content
            return pact;
        }
    }

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
