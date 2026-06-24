using Xunit;

namespace Babelstone.Engine.Analyzers.Tests;

/// <summary>
/// BENG005 — the BUILD-TIME mechanical half of <c>OBS_NO_PII_ATTRS</c> (commitment-catalogue row
/// OBS-3; ADR-IC-007 §P4; ADR-PC-004 §P2). A telemetry span/log attribute whose key or constant
/// value carries personal data (NIF, IBAN, account number, customer name, e-mail) fails the build;
/// the structural <c>babelstone.*</c> operational tier — including the salted
/// <c>babelstone.subject_pseudonym</c> (ADR-IC-016 plane (iii) §8) — and integer-cents money pass.
/// The cases pivot on §P4's headline failure mode: one engineer adding <c>client_nif</c> /
/// <c>client_email</c> as a span attribute turns the trace backend into a GDPR incident, so this
/// gate breaks the build the moment such an attribute is written, not in a forgotten runtime test.
/// </summary>
public sealed class NoPiiTelemetryAttributeAnalyzerTests
{
    private static Task<string[]> Ids(string source) =>
        AnalyzerHarness.DiagnosticIdsAsync(source, new NoPiiTelemetryAttributeAnalyzer());

    // A telemetry call site on a real System.Diagnostics.Activity, parameterised on the body.
    private static string Tag(string body) => $$"""
        using System.Diagnostics;

        public static class Instrumentation
        {
            public static void Tag(Activity activity)
            {
                {{body}}
            }
        }
        """;

    [Fact]
    public async Task A_pii_key_on_a_span_attribute_is_BENG005()
    {
        // The headline §P4 incident: a PII attribute KEY (a raw client NIF) added to a span. The
        // build must break — the trace backend may never become a searchable index of personal data.
        var src = Tag("""activity.SetTag("deposit.client_nif", "234567891");""");
        Assert.Equal([EngineDiagnostics.PiiTelemetryAttributeId], await Ids(src));
    }

    [Fact]
    public async Task A_structural_only_attribute_is_clean()
    {
        // The admitted shape: a structural babelstone.* operational-tier key with a non-PII value.
        // Nothing personal rides the trace, so the build passes.
        var src = Tag("""activity.SetTag("babelstone.partition_key", "stream-0001");""");
        Assert.Empty(await Ids(src));
    }

    [Fact]
    public async Task An_account_or_iban_key_is_BENG005()
    {
        // core.account (a full account number / IBAN) is the §P4 personal-restricted example: a
        // non-babelstone.* key carrying the `account` PII fragment is flagged.
        var src = Tag("""activity.SetTag("core.account", "PT50003300000451612345705");""");
        Assert.Equal([EngineDiagnostics.PiiTelemetryAttributeId], await Ids(src));
    }

    [Fact]
    public async Task A_pii_email_value_under_a_structural_key_is_BENG005()
    {
        // Even a structurally-named key fails when an e-mail VALUE is stamped onto it — the PII is in
        // the value, not the key, and still lands in the regulated trace store.
        var src = Tag("""activity.SetTag("babelstone.product_code", "alice@example.com");""");
        Assert.Equal([EngineDiagnostics.PiiTelemetryAttributeId], await Ids(src));
    }

    [Fact]
    public async Task An_iban_value_is_BENG005()
    {
        // A Portuguese IBAN literal stamped onto a tag value is the §P4 personal-restricted leak.
        var src = Tag("""activity.SetTag("babelstone.partition_key", "PT50 0033 0000 45161234567 05");""");
        Assert.Equal([EngineDiagnostics.PiiTelemetryAttributeId], await Ids(src));
    }

    [Fact]
    public async Task The_subject_pseudonym_key_passes()
    {
        // ADR-IC-016 plane (iii) §8: where a span must reference a customer it carries a salted
        // one-way hash under babelstone.subject_pseudonym — it starts with babelstone. and avoids
        // every PII fragment, so it survives the same scan a real call site would.
        var src = Tag("""activity.SetTag("babelstone.subject_pseudonym", "c0ffee");""");
        Assert.Empty(await Ids(src));
    }

    [Fact]
    public async Task Money_as_integer_cents_passes()
    {
        // Money rides as an integer-cents value under a babelstone.*_cents key — never a formatted
        // decimal, never PII. The cents long is not a PII-shaped value.
        var src = Tag("""activity.SetTag("babelstone.interest_cents", 100L);""");
        Assert.Empty(await Ids(src));
    }

    [Fact]
    public async Task A_non_constant_pii_key_is_not_flagged()
    {
        // The narrow, by-design residual (mirrors the BENG004 within-method limit): a key laundered
        // through a non-constant expression is not constant-foldable here and is not flagged — the
        // closed case is the literal PII key/value written at the call site.
        var src = """
            using System.Diagnostics;

            public static class Instrumentation
            {
                public static void Tag(Activity activity, string dynamicKey)
                {
                    activity.SetTag(dynamicKey, "value");
                }
            }
            """;
        Assert.Empty(await Ids(src));
    }

    [Fact]
    public async Task A_non_telemetry_setter_is_out_of_scope()
    {
        // This analyser governs the telemetry attribute surface, not every dictionary write. A plain
        // Dictionary.Add with a PII-looking key is not a span/log attribute and is left alone.
        var src = """
            using System.Collections.Generic;

            public static class NotTelemetry
            {
                public static void Add(Dictionary<string, string> bag)
                {
                    bag.Add("client_nif", "234567891");
                }
            }
            """;
        Assert.Empty(await Ids(src));
    }
}
