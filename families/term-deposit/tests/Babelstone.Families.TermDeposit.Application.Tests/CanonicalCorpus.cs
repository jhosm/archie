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
/// — the interest shape, term, coupon cadence, and (for the banded early-termination variant) the
/// resolved early-termination schedule the simulation needs to drive the right lifecycle. These
/// mirror the variant YAML the depths-1–4 validator checks (term_days / interest_variant /
/// payment_period_months / early_termination); the simulation reads them by variant id so a new
/// corpus instance only needs its variant registered here, no per-instance code.
/// </summary>
/// <param name="InterestVariant">The engine's interest shape (<c>AT_MATURITY</c>, <c>ADVANCE</c>,
/// <c>PERIODIC</c>) — the same string the constitution command carries. The depth-5 driver also
/// uses the special simulation marker <c>BANDED_EARLY_TERMINATION</c> (see <paramref name="Lifecycle"/>)
/// to drive a banded break instead of running to maturity; that shape still constitutes as an
/// underlying <c>AT_MATURITY</c> deposit, so this stays the real interest variant the constitution uses.</param>
/// <param name="Lifecycle">Which terminal lifecycle the depth-5 simulation drives this variant to:
/// <c>AtMaturity</c> (constitute → … → mature) for the four maturing shapes, or
/// <c>BandedEarlyTermination</c> (constitute → break early on the resolved band schedule) for the
/// 18-month <c>resgate escalonado</c> variant whose load-bearing behaviour is the banded penalty,
/// not at-maturity payout (bd babelstone-3h64).</param>
/// <param name="EarlyTermination">The resolved early-termination policy + the elapsed day the break
/// fires at, ONLY for a <see cref="SimulatedLifecycle.BandedEarlyTermination"/> variant; <c>null</c>
/// for the maturing shapes. The policy is the per-product config the bank's pricing team owns (02
/// §2.5); the depth-5 simulation pins it as the deterministic regression schedule the corpus replays
/// against, exactly as it pins the resolved rate (C.6).</param>
internal sealed record VariantShape(
    string InterestVariant,
    int TermDays,
    int PaymentPeriodMonths,
    SimulatedLifecycle Lifecycle = SimulatedLifecycle.AtMaturity,
    EarlyTerminationShape? EarlyTermination = null,
    PartialWithdrawalShape? PartialWithdrawal = null);

/// <summary>The deterministic partial-withdrawal INPUTS the depth-5 simulation drives a
/// <see cref="SimulatedLifecycle.PartialWithdrawal"/> variant with (bd k6r8.10): the elapsed day the
/// withdrawal fires at and the fixed amount withdrawn. Both are INPUTS (start + N days, fixed cents) so
/// the produced sequence is identical on every run and CI host, mirroring the banded leg's
/// <see cref="EarlyTerminationShape.BreakAfterDays"/>. The policy itself is NOT carried here — the F.12
/// policy rides on the product config (k6r8.8) and is resolved from the REAL <c>resgate parcial</c>
/// variant AT CONSTITUTION through the wired product-config store, then PINNED on the deposit (the
/// withdrawal reads the pinned policy off the position) — so the leg exercises the whole F.12 chain
/// end-to-end. The chosen values must clear that variant's pinned gates (past the carência; at least the
/// minimum withdrawal; leaving at least the minimum remaining balance).</summary>
/// <param name="WithdrawAfterDays">Elapsed days from constitution to the simulated withdrawal — chosen
/// strictly on/after the variant's carência so the lock-up gate passes.</param>
/// <param name="WithdrawnCents">The fixed principal withdrawn, in cents — chosen to clear the minimum
/// withdrawal and leave at least the minimum remaining balance on deposit.</param>
internal sealed record PartialWithdrawalShape(int WithdrawAfterDays, long WithdrawnCents);

/// <summary>Which lifecycle the depth-5 simulation drives a variant to (bd babelstone-3h64, k6r8.10).</summary>
internal enum SimulatedLifecycle
{
    /// <summary>Constitute → (coupons) → mature: the four maturing interest shapes.</summary>
    AtMaturity,

    /// <summary>Constitute → break early on the resolved band schedule: the 18-month banded
    /// <c>resgate escalonado</c> variant, whose distinctive behaviour is the first-match penalty
    /// schedule, never exercised by an at-maturity run.</summary>
    BandedEarlyTermination,

    /// <summary>Constitute → partially withdraw (F.12, bd k6r8.10): the <c>resgate parcial</c> variant.
    /// A partial withdrawal is STATE-PRESERVING (F.3) — it reduces the principal but does NOT close the
    /// deposit, so this leg does NOT run to a terminal state: it ends with the deposit still Active and a
    /// reduced RemainingPrincipal. The load-bearing evidence is the <c>DepositPartiallyWithdrawn</c> event
    /// replaying and the terminal fold carrying the reduced principal.</summary>
    PartialWithdrawal,
}

/// <summary>The resolved banded early-termination schedule the depth-5 simulation drives a
/// <see cref="SimulatedLifecycle.BandedEarlyTermination"/> variant against, plus the elapsed day the
/// break fires at (so the band first-match is deterministic and the expected sequence is fixed). The
/// policy is the engine-instance early-termination config the service resolves (02 §2.5), pinned here
/// as the regression schedule exactly as the corpus pins the resolved rate.</summary>
/// <param name="Policy">The ordered (window → penalty) band schedule with its optional floor.</param>
/// <param name="BreakAfterDays">Elapsed days from constitution to the simulated break — chosen so a
/// specific band wins first-match, making the penalty (and thus the lifecycle shape) deterministic.</param>
internal sealed record EarlyTerminationShape(EarlyTerminationPolicy Policy, int BreakAfterDays);

internal static class TermDepositVariants
{
    // The §2.5-shaped banded schedule the 18-month `resgate escalonado` variant resolves to (the
    // engine-instance early-termination config, ADR-PC-009 stand-in): a staggered penalty on the
    // accrued interest — 100% inside the first band, 50% in the second, 25% on the open tail — no
    // floor. This IS the variant's load-bearing behaviour; the depth-5 sim breaks at day 200 (the
    // second band, ≤365d → 50% of accrued) so the banded first-match path is actually replayed
    // rather than mapped to a plain at-maturity shape (bd babelstone-3h64).
    private static readonly EarlyTerminationShape BandedResgateEscalonado = new(
        EarlyTerminationPolicy.Banded(
        [
            new EarlyTerminationBand(UpToDays: 90, PenaltyBasisPoints: 10_000, PenaltyBasis.AccruedInterest),
            new EarlyTerminationBand(UpToDays: 365, PenaltyBasisPoints: 5_000, PenaltyBasis.AccruedInterest),
            new EarlyTerminationBand(UpToDays: null, PenaltyBasisPoints: 2_500, PenaltyBasis.AccruedInterest),
        ]),
        BreakAfterDays: 200);

    // The five v1 launch variants (product-configs/*.yaml), one per interest shape (F.7). The
    // 18-month `resgate escalonado` constitutes as an underlying AT_MATURITY deposit but the
    // simulation drives it to a BANDED early termination — its distinctive behaviour (bd babelstone-3h64).
    private static readonly IReadOnlyDictionary<string, VariantShape> Shapes =
        new Dictionary<string, VariantShape>(StringComparer.Ordinal)
        {
            ["dpz_pt_12m_juros_venc"] = new("AT_MATURITY", TermDays: 365, PaymentPeriodMonths: 0),
            ["dpz_pt_12m_juros_mensal"] = new("PERIODIC", TermDays: 365, PaymentPeriodMonths: 1),
            ["dpz_pt_24m_juros_trimestral"] = new("PERIODIC", TermDays: 730, PaymentPeriodMonths: 3),
            ["dpz_pt_6m_juros_antecipados"] = new("ADVANCE", TermDays: 180, PaymentPeriodMonths: 0),
            ["dpz_pt_18m_resgate_escalonado"] = new(
                "AT_MATURITY", TermDays: 545, PaymentPeriodMonths: 0,
                SimulatedLifecycle.BandedEarlyTermination, BandedResgateEscalonado),
            // The F.12 `resgate parcial` variant (bd k6r8.10): an underlying AT_MATURITY deposit the
            // simulation drives through a PARTIAL withdrawal instead of to maturity. Its declared policy
            // (min €500 withdrawal, min €1,000 remaining, 90-day carência) is resolved from the product
            // config — not pinned here. The withdrawal fires on day 120 (past the 90-day carência) for
            // €10,000, leaving €30,000 of a €40,000 principal (clears every gate). The deposit stays
            // Active afterward (partial withdrawal is state-preserving, F.3).
            ["dpz_pt_12m_resgate_parcial"] = new(
                "AT_MATURITY", TermDays: 365, PaymentPeriodMonths: 0,
                SimulatedLifecycle.PartialWithdrawal,
                PartialWithdrawal: new PartialWithdrawalShape(WithdrawAfterDays: 120, WithdrawnCents: 1_000_000)),
        };

    public static VariantShape For(string variantId) =>
        Shapes.TryGetValue(variantId, out var shape)
            ? shape
            : throw new InvalidOperationException(
                $"depth-5 corpus names variant '{variantId}', which is not registered in TermDepositVariants " +
                "(add its interest shape / term / cadence to drive its lifecycle).");
}
