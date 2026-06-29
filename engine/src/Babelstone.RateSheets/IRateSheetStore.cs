namespace Babelstone.RateSheets;

/// <summary>
/// Persistence for rate sheets (ADR-PC-008): append-only writes, idempotent
/// fetch by version id, and the point-in-time resolution stamped at constitution.
/// Hand-rolled against the table contract (no ORM, ADR-PC-010).
/// </summary>
public interface IRateSheetStore
{
    /// <summary>
    /// Appends a new immutable sheet. Throws <see cref="DuplicateRateSheetVersionException"/>
    /// if the version id already exists or its <c>(product_family, effective_from)</c> is
    /// already taken — the deploy endpoint turns that into the ADR-PC-008 idempotency outcome.
    /// </summary>
    Task InsertAsync(RateSheet sheet, CancellationToken ct = default);

    /// <summary>Fetches a sheet by its version id, or null if none exists.</summary>
    Task<RateSheet?> TryGetAsync(string rateSheetVersionId, CancellationToken ct = default);

    /// <summary>
    /// Resolves the sheet active at <paramref name="asOf"/> for a family (ADR-PC-008):
    /// the highest <c>effective_from</c> not after the instant. Null if no sheet is yet
    /// effective. The <c>(product_family, effective_from)</c> uniqueness constraint makes
    /// this unambiguous.
    /// </summary>
    Task<RateSheetResolution?> ResolveAsync(string productFamily, DateTimeOffset asOf, CancellationToken ct = default);
}

/// <summary>
/// Raised when an insert collides with an existing sheet — either the
/// <c>rate_sheet_version_id</c> primary key or the <c>(product_family, effective_from)</c>
/// unique key. The deploy endpoint re-reads and applies the ADR-PC-008 rule: identical body
/// under the same version id is idempotent success; anything else is a conflict.
/// </summary>
public sealed class DuplicateRateSheetVersionException : Exception
{
    public DuplicateRateSheetVersionException(string rateSheetVersionId, Exception? inner = null)
        : base($"A rate sheet conflicting with version id '{rateSheetVersionId}' already exists.", inner)
        => RateSheetVersionId = rateSheetVersionId;

    public string RateSheetVersionId { get; }
}
