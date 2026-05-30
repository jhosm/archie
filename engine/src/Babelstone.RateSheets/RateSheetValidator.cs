namespace Babelstone.RateSheets;

/// <summary>
/// The pack-declared bounds a rate sheet's TANs must honour at deploy
/// (ADR-PC-008 §P2, surface §2.5). For pt.2026.1 this is
/// <c>[0, max_consumer_rate_bps]</c> with <c>max_consumer_rate_bps = 2000</c>
/// (packs/pt.2026.1/parameters/constants.yaml). The bounds are supplied by the
/// caller (resolved from the pack) rather than read here, so the validator stays a
/// pure function of (body, bounds).
/// </summary>
public sealed record RateBounds(int MinBasisPoints, int MaxBasisPoints);

/// <summary>Outcome of <see cref="RateSheetValidator.Validate"/>: valid, or a list of human-readable diagnostics.</summary>
public sealed record RateSheetValidationResult(bool IsValid, IReadOnlyList<string> Diagnostics)
{
    public static RateSheetValidationResult Valid { get; } = new(true, []);

    public static RateSheetValidationResult Invalid(IReadOnlyList<string> diagnostics) => new(false, diagnostics);
}

/// <summary>
/// Validates a rate-sheet body at deploy time (ADR-PC-008 §P2): a sheet that leaves
/// a band gap, overlaps bands, or breaches the pack bound is rejected at the
/// <c>POST /v1/rate-sheets</c> boundary, never at first constitution.
/// </summary>
/// <remarks>
/// This covers the <em>self-contained</em> §2.5 invariants — band shape, non-overlap,
/// upward-exhaustiveness, and pack-declared bounds. The <em>cross-artefact</em> invariants
/// ("every referenced product_id exists in an active config"; "every active config's
/// rate_ref (product, role, principal) is covered") need the product-config registry,
/// which does not exist until Epic E/F. They are deliberately NOT enforced here — see the
/// filed follow-up — so this validator never silently half-checks a sheet against configs
/// it cannot see.
/// </remarks>
public sealed class RateSheetValidator
{
    public RateSheetValidationResult Validate(RateSheetBody body, RateBounds bounds)
    {
        var diagnostics = new List<string>();

        if (body.Products.Count == 0)
        {
            diagnostics.Add("Rate sheet has no products.");
        }

        foreach (var (productId, roles) in body.Products)
        {
            if (roles.Count == 0)
            {
                diagnostics.Add($"Product '{productId}' has no roles.");
            }

            foreach (var (role, roleRates) in roles)
            {
                ValidateBands(productId, role, roleRates.Bands, bounds, diagnostics);
            }
        }

        return diagnostics.Count == 0
            ? RateSheetValidationResult.Valid
            : RateSheetValidationResult.Invalid(diagnostics);
    }

    private static void ValidateBands(
        string productId, string role, IReadOnlyList<RateBand> bands, RateBounds bounds, List<string> diagnostics)
    {
        var where = $"{productId}/{role}";
        if (bands.Count == 0)
        {
            diagnostics.Add($"{where}: no bands.");
            return;
        }

        // Per-band shape + bound checks. The contiguity pass below relies on well-shaped
        // bands (exactly [from, to] with a non-null from), so it is skipped if any fail.
        var shapeOk = true;
        for (var i = 0; i < bands.Count; i++)
        {
            var band = bands[i];
            if (band.PrincipalCents.Length != 2)
            {
                diagnostics.Add($"{where} band {i}: principal_cents must have exactly 2 elements [from, to], got {band.PrincipalCents.Length}.");
                shapeOk = false;
                continue;
            }

            if (band.PrincipalCents[0] is not { } from)
            {
                diagnostics.Add($"{where} band {i}: lower bound (principal_cents[0]) must not be null.");
                shapeOk = false;
                continue;
            }

            if (from < 0)
            {
                diagnostics.Add($"{where} band {i}: lower bound {from} must be >= 0.");
                shapeOk = false;
            }

            if (band.PrincipalCents[1] is { } upper && upper <= from)
            {
                diagnostics.Add($"{where} band {i}: upper bound {upper} must be greater than lower bound {from}.");
                shapeOk = false;
            }

            if (band.TanBasisPoints < bounds.MinBasisPoints || band.TanBasisPoints > bounds.MaxBasisPoints)
            {
                diagnostics.Add(
                    $"{where} band {i}: tan_basis_points {band.TanBasisPoints} is outside the pack-declared bounds " +
                    $"[{bounds.MinBasisPoints}, {bounds.MaxBasisPoints}].");
            }
        }

        if (!shapeOk)
        {
            return;
        }

        // Contiguity + exhaustiveness (surface §2.5: non-overlapping, exhaustive over the
        // supported principal range): sorted by lower bound, each band's upper bound must
        // meet the next band's lower bound, and only the highest band is open-ended.
        var sorted = bands.OrderBy(b => b.From).ToList();
        for (var i = 0; i < sorted.Count - 1; i++)
        {
            var current = sorted[i];
            var next = sorted[i + 1];

            if (current.To is null)
            {
                diagnostics.Add($"{where}: an open-ended band (no upper bound) is not the highest band; higher bands are unreachable.");
                break;
            }

            if (current.To.Value != next.From)
            {
                var kind = current.To.Value < next.From ? "gap" : "overlap";
                diagnostics.Add(
                    $"{where}: {kind} between a band ending at {current.To.Value} and a band starting at {next.From}; " +
                    "bands must be contiguous and non-overlapping.");
            }
        }

        if (sorted[^1].To is not null)
        {
            diagnostics.Add(
                $"{where}: the highest band must be open-ended (null upper bound) so the principal range is exhaustive; " +
                $"got upper bound {sorted[^1].To}.");
        }
    }
}
