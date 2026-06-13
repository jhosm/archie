using System.Globalization;

namespace Babelstone.Families.TermDeposit.Application.Tests;

/// <summary>
/// One canonical sealed-corpus instance — an INPUT row of
/// <c>packs/pt.2026.1/test-corpus/canonical-instances.yaml</c> (ADR-PC-007 §P5). Every field is
/// an input the depth-5 simulation drives a deposit lifecycle from; nothing here is a computed
/// result (surface §2). <see cref="RateBasisPoints"/> is the rate the corpus PINS so the
/// regression is deterministic even as the live rate sheet runs on its own cadence (C.6).
/// </summary>
internal sealed record CanonicalInstance(
    string TestId,
    string Pack,
    string VariantId,
    long PrincipalCents,
    DateTimeOffset ConstitutedAt,
    int RateBasisPoints);

/// <summary>
/// The sealed test corpus the depth-5 simulation replays (ADR-PC-006 §P4). Loaded straight off the
/// committed pack source on disk — the same <c>canonical-instances.yaml</c> pack.sh validates — so
/// the simulation exercises the REAL corpus, never a hand-built one. A minimal hand-rolled parser
/// (no extra YAML dependency for the test tier) reads the flat <c>tests:</c> list the corpus carries.
/// </summary>
internal sealed class CanonicalCorpus
{
    public required string PackKey { get; init; }

    public required IReadOnlyList<CanonicalInstance> Instances { get; init; }

    public static CanonicalCorpus Load()
    {
        var path = Path.Combine(
            RepoRoot(), "packs", "pt.2026.1", "test-corpus", "canonical-instances.yaml");
        var instances = ParseInstances(File.ReadAllLines(path));
        if (instances.Count == 0)
        {
            throw new InvalidOperationException(
                $"canonical-instances.yaml at {path} carried no `tests:` entries — the depth-5 corpus is empty.");
        }

        return new CanonicalCorpus { PackKey = "pt.2026.1", Instances = instances };
    }

    /// <summary>
    /// Parse the <c>tests:</c> list — a sequence of <c>- key: value</c> blocks. Each new item starts
    /// at a <c>- </c> dash; trailing <c># …</c> comments and blank lines are ignored. The corpus is a
    /// flat, auditor-readable shape (no nesting under a test item), so a small line scanner is enough
    /// and keeps the test tier free of a YAML library it does not otherwise need.
    /// </summary>
    private static List<CanonicalInstance> ParseInstances(IReadOnlyList<string> lines)
    {
        var instances = new List<CanonicalInstance>();
        Dictionary<string, string>? current = null;

        void Flush()
        {
            if (current is not null)
            {
                instances.Add(Materialize(current));
                current = null;
            }
        }

        foreach (var raw in lines)
        {
            var line = StripComment(raw);
            if (line.Trim().Length == 0)
            {
                continue;
            }

            var trimmed = line.TrimStart();
            if (trimmed.StartsWith("- ", StringComparison.Ordinal))
            {
                // A new test item begins; close the previous one and seed this one with its first field.
                Flush();
                current = new Dictionary<string, string>(StringComparer.Ordinal);
                AddField(current, trimmed[2..]);
                continue;
            }

            // A continuation field of the current item (e.g. "  principal_cents: 1000000").
            if (current is not null && trimmed.Contains(':', StringComparison.Ordinal))
            {
                AddField(current, trimmed);
            }
        }

        Flush();
        return instances;
    }

    private static void AddField(IDictionary<string, string> fields, string keyValue)
    {
        var colon = keyValue.IndexOf(':');
        if (colon <= 0)
        {
            return;
        }

        var key = keyValue[..colon].Trim();
        var value = keyValue[(colon + 1)..].Trim();
        fields[key] = value;
    }

    private static CanonicalInstance Materialize(IReadOnlyDictionary<string, string> fields)
    {
        string Require(string key) =>
            fields.TryGetValue(key, out var value) && value.Length > 0
                ? value
                : throw new InvalidOperationException($"corpus instance missing required field '{key}'");

        return new CanonicalInstance(
            TestId: Require("test_id"),
            Pack: Require("pack"),
            VariantId: Require("variant_id"),
            PrincipalCents: long.Parse(Require("principal_cents"), CultureInfo.InvariantCulture),
            ConstitutedAt: ParseConstitutedAt(Require("constituted_at")),
            RateBasisPoints: int.Parse(Require("rate_basis_points"), CultureInfo.InvariantCulture));
    }

    /// <summary>The corpus carries a bare date (<c>2026-01-15</c>); the simulation reads it as
    /// midnight UTC — the as-of instant the rate sheet resolves against and the constitution's
    /// valid time.</summary>
    private static DateTimeOffset ParseConstitutedAt(string value)
    {
        var date = DateOnly.ParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture);
        return new DateTimeOffset(date, TimeOnly.MinValue, TimeSpan.Zero);
    }

    /// <summary>Drop a trailing <c># …</c> comment (the corpus annotates every numeric field).
    /// The corpus carries no <c>#</c> inside a value, so a first-hash split is safe and simple.</summary>
    private static string StripComment(string line)
    {
        var hash = line.IndexOf('#');
        return hash < 0 ? line : line[..hash];
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null
               && !File.Exists(Path.Combine(dir.FullName, "packs", "pt.2026.1", "pack.yaml")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName
            ?? throw new InvalidOperationException(
                $"repo root (containing packs/pt.2026.1/pack.yaml) not found from {AppContext.BaseDirectory}");
    }
}

/// <summary>
/// The structural facts of each committed <c>/product-configs</c> variant the depth-5 corpus names
/// — the interest shape, term, and coupon cadence the simulation needs to drive the right lifecycle.
/// These mirror the variant YAML the depths-1–4 validator checks (term_days / interest_variant /
/// payment_period_months); the simulation reads them by variant id so a new corpus instance only
/// needs its variant registered here, no per-instance code.
/// </summary>
internal sealed record VariantShape(string InterestVariant, int TermDays, int PaymentPeriodMonths);

internal static class TermDepositVariants
{
    // The five v1 launch variants (product-configs/*.yaml), one per interest shape (F.7).
    private static readonly IReadOnlyDictionary<string, VariantShape> Shapes =
        new Dictionary<string, VariantShape>(StringComparer.Ordinal)
        {
            ["dpz_pt_12m_juros_venc"] = new("AT_MATURITY", TermDays: 365, PaymentPeriodMonths: 0),
            ["dpz_pt_12m_juros_mensal"] = new("PERIODIC", TermDays: 365, PaymentPeriodMonths: 1),
            ["dpz_pt_24m_juros_trimestral"] = new("PERIODIC", TermDays: 730, PaymentPeriodMonths: 3),
            ["dpz_pt_6m_juros_antecipados"] = new("ADVANCE", TermDays: 180, PaymentPeriodMonths: 0),
            ["dpz_pt_18m_resgate_escalonado"] = new("AT_MATURITY", TermDays: 545, PaymentPeriodMonths: 0),
        };

    public static VariantShape For(string variantId) =>
        Shapes.TryGetValue(variantId, out var shape)
            ? shape
            : throw new InvalidOperationException(
                $"depth-5 corpus names variant '{variantId}', which is not registered in TermDepositVariants " +
                "(add its interest shape / term / cadence to drive its lifecycle).");
}
