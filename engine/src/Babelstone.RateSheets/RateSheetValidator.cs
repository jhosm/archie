namespace Babelstone.RateSheets;

/// <summary>
/// The pack-declared bounds a rate sheet's TANs must honour at deploy
/// (ADR-PC-008 §P2, surface §2.5). For pt.2026.1 this is
/// <c>[0, max_consumer_rate_bps]</c> with <c>max_consumer_rate_bps = 2000</c>
/// (packs/pt.2026.1/parameters/constants.yaml). The bounds are supplied by the
/// caller (resolved from the pack) rather than read here, so the validator stays a
/// pure function of (body, bounds).
/// </summary>
public sealed record RateBounds
{
    public RateBounds(int minBasisPoints, int maxBasisPoints)
    {
        if (minBasisPoints > maxBasisPoints)
        {
            throw new ArgumentException(
                $"MinBasisPoints ({minBasisPoints}) must not exceed MaxBasisPoints ({maxBasisPoints}); " +
                "an inverted bound would silently reject every band.");
        }

        MinBasisPoints = minBasisPoints;
        MaxBasisPoints = maxBasisPoints;
    }

    public int MinBasisPoints { get; }

    public int MaxBasisPoints { get; }
}

/// <summary>Outcome of <see cref="RateSheetValidator.Validate(RateSheetBody, RateBounds, IReadOnlyList{ActiveProductConfig})"/> (either overload): valid, or a list of human-readable diagnostics.</summary>
public sealed record RateSheetValidationResult(bool IsValid, IReadOnlyList<string> Diagnostics)
{
    public static RateSheetValidationResult Valid { get; } = new(true, []);

    public static RateSheetValidationResult Invalid(IReadOnlyList<string> diagnostics) => new(false, diagnostics);
}

/// <summary>
/// One <c>rate_ref</c> a product config asks the active rate sheet to price (surface §2.2,
/// §2.5). The surface words a <c>rate_ref</c> as resolving <c>(product, role, principal)</c>,
/// but the principal is deliberately <em>not</em> part of the ref: a config's <c>rate_ref</c>
/// is a <c>{ sheet, role_selector }</c> rule (surface §2.2) that pins a fact-to-<em>role</em>
/// mapping, never a single principal. The principal arrives only at constitution time, from the
/// deposit operation, and the <em>whole</em> supported principal range is covered by the
/// validator's cross-band exhaustiveness check for the <c>(product, role)</c> pair (a present
/// pair's bands are exhaustive over <c>[min, ∞)</c>). So the <c>(product, role, principal)</c>
/// coverage the surface asks for is the product of two checks — this ref's <c>(product, role)</c>
/// presence, and exhaustiveness over the principal axis — and the ref itself collapses to
/// <c>(product_id, role)</c>. <b>Decision (bd babelstone-ktfx):</b> whole-range principal
/// coverage is the intended §2.5 semantics; per-config principal pinning is explicitly NOT a
/// thing in v1, so the principal axis stays out of the ref. See
/// <see cref="RateSheetValidator.Covers"/> for the coverage primitive this implies.
/// </summary>
public sealed record RateRef(string ProductId, string Role);

/// <summary>
/// An active product config as the cross-artefact validator sees it (surface §2.5): its
/// <see cref="ProductId"/> and the <see cref="RateRefs"/> its <c>role_selector.map</c> can
/// resolve to. This is the minimal projection the rate-sheet side needs — the full config
/// (parameters, day-count, withholding) is owned by the product-config registry (Epic E/F)
/// and never reaches this validator.
/// </summary>
public sealed record ActiveProductConfig(string ProductId, IReadOnlyList<RateRef> RateRefs);

/// <summary>
/// Validates a rate-sheet body at deploy time (ADR-PC-008 §P2): a sheet that leaves
/// a band gap, overlaps bands, or breaches the pack bound is rejected at the
/// <c>POST /v1/rate-sheets</c> boundary, never at first constitution.
/// </summary>
/// <remarks>
/// This covers the <em>cross-band</em> §2.5 invariants — non-overlap, upward-exhaustiveness,
/// and pack-declared TAN bounds. Per-band <em>shape</em> (the <c>[lower, upper]</c> range is
/// well-formed) is no longer checked here: it is correct-by-construction on <see cref="RateBand"/>
/// itself (<see cref="RateBandJsonConverter"/> rejects a malformed range at deserialize), so a
/// malformed band cannot reach this validator. The <em>cross-artefact</em> invariants
/// ("every referenced product_id exists in an active config"; "every active config's
/// <c>rate_ref</c> (product, role) is covered") are enforced against the active product configs
/// the caller supplies (an <see cref="ActiveProductConfig"/> list resolved from the product-config
/// registry). The registry itself is owned by the deploy host, so the validator stays a pure
/// function of <c>(body, bounds, configs)</c>. With <em>no</em> active configs the two
/// cross-artefact checks pass vacuously — a backwards-compatible default that never rejects an
/// existing deploy just because the registry is empty (e.g. the registry is not yet wired in).
/// </remarks>
public sealed class RateSheetValidator
{
    /// <summary>
    /// Validates a sheet against the self-contained §2.5 invariants only (no cross-artefact
    /// checks): used where no product-config registry is in play, and the base case the
    /// cross-artefact overload reduces to with an empty config list.
    /// </summary>
    public RateSheetValidationResult Validate(RateSheetBody body, RateBounds bounds) =>
        Validate(body, bounds, []);

    /// <summary>
    /// Validates a sheet against the full §2.5 invariant set — the self-contained ones
    /// (band shape via construction, non-overlap, upward-exhaustiveness, pack bounds) plus the
    /// two <em>cross-artefact</em> ones evaluated against <paramref name="activeConfigs"/>:
    /// (1) every product the sheet prices exists in an active config; (2) every active config's
    /// <c>rate_ref</c> is covered by the sheet. An empty <paramref name="activeConfigs"/> makes
    /// both cross-artefact checks pass vacuously (backwards-compatible).
    /// </summary>
    public RateSheetValidationResult Validate(
        RateSheetBody body, RateBounds bounds, IReadOnlyList<ActiveProductConfig> activeConfigs)
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

        ValidateCrossArtefact(body, activeConfigs, diagnostics);

        return diagnostics.Count == 0
            ? RateSheetValidationResult.Valid
            : RateSheetValidationResult.Invalid(diagnostics);
    }

    /// <summary>
    /// True if <paramref name="body"/> prices <paramref name="rateRef"/> — the reusable
    /// coverage primitive behind the symmetric §2.5 invariant. A future product-config deploy
    /// path uses this in reverse: it rejects a config whose <c>rate_ref</c> the active sheet
    /// does not cover (surface §2.5 "At product-config deploy"), so the engine never accepts a
    /// state where the two artefacts disagree, whichever deploys first. Coverage is
    /// <c>(product, role)</c> presence with at least one band: a present pair's bands are
    /// exhaustive over the WHOLE supported principal range by the cross-band check
    /// (<see cref="ValidateBands"/>), so a covered pair prices every principal and no per-principal
    /// probe is needed. This is exactly why the surface's <c>(product, role, principal)</c> coverage
    /// is met by a <c>(product, role)</c> ref: the principal axis is whole-range, never per-config
    /// (the §2.5 decision, bd babelstone-ktfx — see <see cref="RateRef"/>).
    /// </summary>
    public static bool Covers(RateSheetBody body, RateRef rateRef) =>
        body.Products.TryGetValue(rateRef.ProductId, out var roles)
        && roles.TryGetValue(rateRef.Role, out var roleRates)
        && roleRates.Bands.Count > 0;

    private static void ValidateCrossArtefact(
        RateSheetBody body, IReadOnlyList<ActiveProductConfig> activeConfigs, List<string> diagnostics)
    {
        if (activeConfigs.Count == 0)
        {
            // No active configs: nothing to cross-check against. Both invariants hold vacuously,
            // so the sheet is judged on its self-contained shape alone (surface §2.5).
            return;
        }

        // (1) Every product the sheet prices must exist in an active config — a sheet pricing a
        // product no config references is a stale/orphaned reference, rejected at deploy.
        var activeProductIds = activeConfigs.Select(c => c.ProductId).ToHashSet(StringComparer.Ordinal);
        foreach (var productId in body.Products.Keys)
        {
            if (!activeProductIds.Contains(productId))
            {
                diagnostics.Add(
                    $"Product '{productId}' is priced by the sheet but has no active product config; " +
                    "every referenced product_id must exist in an active config (surface §2.5).");
            }
        }

        // (2) Every active config's rate_ref must be covered by the sheet — a config asking for a
        // (product, role) the sheet doesn't price would leave a constitution unpriceable, so it is
        // rejected at deploy, never at first constitution. Coverage is whole-range over the
        // principal axis (the §2.5 decision, bd babelstone-ktfx): a present (product, role) pair's
        // bands are exhaustive over [min, ∞), so a covered ref prices EVERY principal — there is no
        // per-config principal to pin, and so none to check.
        foreach (var config in activeConfigs)
        {
            foreach (var rateRef in config.RateRefs)
            {
                // Cross-check the ref's product against the config's own product. A config's
                // rate_ref points at the SAME product the config configures (surface §2.2: the
                // rate_ref lives inside the product config and resolves that product's role); a ref
                // naming a different product_id is a malformed config, caught here at deploy rather
                // than silently mispricing at constitution.
                if (!string.Equals(rateRef.ProductId, config.ProductId, StringComparison.Ordinal))
                {
                    diagnostics.Add(
                        $"Active config '{config.ProductId}' has a rate_ref naming a different product " +
                        $"'{rateRef.ProductId}'; a config's rate_ref must reference its own product_id " +
                        "(surface §2.2/§2.5).");
                    continue;
                }

                if (!Covers(body, rateRef))
                {
                    diagnostics.Add(
                        $"Active config '{config.ProductId}' references rate_ref " +
                        $"({rateRef.ProductId}, {rateRef.Role}) which the sheet does not cover; " +
                        "every active config's rate_ref must be covered (surface §2.5).");
                }
            }
        }
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

        // Per-band SHAPE is correct-by-construction: a RateBand cannot exist with a malformed
        // [lower, upper] range (RateBandJsonConverter rejects it at deserialize, the constructor
        // at author time), so From/To are always well-shaped here. The only per-band check that
        // remains is the pack-declared TAN bound, which depends on the external bounds the
        // converter cannot see. Cross-band contiguity/exhaustiveness still lives below.
        for (var i = 0; i < bands.Count; i++)
        {
            var band = bands[i];
            if (band.TanBasisPoints < bounds.MinBasisPoints || band.TanBasisPoints > bounds.MaxBasisPoints)
            {
                diagnostics.Add(
                    $"{where} band {i}: tan_basis_points {band.TanBasisPoints} is outside the pack-declared bounds " +
                    $"[{bounds.MinBasisPoints}, {bounds.MaxBasisPoints}].");
            }
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
