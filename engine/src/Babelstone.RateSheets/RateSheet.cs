namespace Babelstone.RateSheets;

/// <summary>
/// A versioned, immutable rate sheet (ADR-PC-008) — one row of the
/// <c>rate_sheets</c> table. Once published it is never edited; a correction ships
/// as a new <see cref="RateSheetVersionId"/> with a later <see cref="EffectiveFrom"/>
/// (ADR-PC-008, surface §2.6).
/// </summary>
/// <param name="RateSheetVersionId">Unique id of this sheet version — the value pinned on <c>DepositConstituted</c> (ADR-PC-008).</param>
/// <param name="ProductFamily">The product family this sheet prices (e.g. <c>term-deposit</c>).</param>
/// <param name="PackVersion">The regulatory pack version the sheet was authored against.</param>
/// <param name="EffectiveFrom">Instant from which this version is the candidate active sheet (ADR-PC-008); a correction ships as a new version with a later value (ADR-PC-008).</param>
/// <param name="Body">The rate table — resolves <c>(product, role, principal)</c> to <c>tan_basis_points</c>.</param>
/// <param name="ApprovedBy">Who approved the sheet before publication.</param>
/// <param name="ApprovalRef">Reference to the approval record (audit trail).</param>
/// <param name="PublishedBy">Who published the sheet.</param>
/// <param name="PublishedAt">
/// Set by the database default (<c>clock_timestamp()</c>) at insert; null on a
/// not-yet-stored sheet and populated on read-back.
/// </param>
public sealed record RateSheet(
    string RateSheetVersionId,
    string ProductFamily,
    string PackVersion,
    DateTimeOffset EffectiveFrom,
    RateSheetBody Body,
    string ApprovedBy,
    string ApprovalRef,
    string PublishedBy,
    DateTimeOffset? PublishedAt = null);

/// <summary>
/// The sheet resolved as active at a constitution instant (ADR-PC-008). Carries
/// the <see cref="RateSheetVersionId"/> to pin on <c>DepositConstituted</c> and the
/// <see cref="Body"/> to resolve the concrete <c>(product, role, principal) -&gt;
/// tan_basis_points</c>. Storing both the version id and the resolved value on the
/// event is deliberate (ADR-PC-008): the id anchors audit/replay, the value answers "what
/// rate is this deposit paying?" without re-resolution.
/// </summary>
public sealed record RateSheetResolution(string RateSheetVersionId, RateSheetBody Body)
{
    /// <summary>
    /// Resolves <c>(productId, role, principalCents) -&gt; tan_basis_points</c> against the
    /// active sheet's body, or null if the product/role is absent or no band covers the
    /// principal. The deploy-time validator (<see cref="RateSheetValidator"/>) guarantees a
    /// covered, accepted sheet has exactly one matching band, so a null here on a deployed
    /// sheet means the <c>(product, role)</c> pair is genuinely not priced.
    /// </summary>
    public int? ResolveTanBasisPoints(string productId, string role, long principalCents)
    {
        if (!Body.Products.TryGetValue(productId, out var roles) ||
            !roles.TryGetValue(role, out var roleRates))
        {
            return null;
        }

        foreach (var band in roleRates.Bands)
        {
            if (band.Covers(principalCents))
            {
                return band.TanBasisPoints;
            }
        }

        return null;
    }
}
