using Microsoft.Extensions.Logging;

namespace Babelstone.Telemetry;

/// <summary>
/// The stable <see cref="EventId"/> contract for Babelstone host structured logs (ADR-IC-007
/// Layer 1). An event id is a wire identifier an operator alerts and dashboards on (the log analogue
/// of the versioned <see cref="BabelstoneAttributes"/> span-key contract) — <b>never renumber an id
/// or repurpose its name</b>; add a new one. The numeric space is banded per host so ids stay unique
/// across the estate: the rate-sheet deploy host owns <c>1000–1099</c>.
///
/// As with span attributes, a log emitted under these ids carries only ADR-IC-007
/// operational-tier structural identifiers (a version id, a product family, a deploy actor) — no
/// NIF, IBAN, name, or e-mail (the rate-sheet deploy surface carries no depositor data, but the
/// discipline is the same one the trace backend enforces).
/// </summary>
public static class BabelstoneEvents
{
    /// <summary>
    /// A rate-sheet deploy was rejected as a 409 conflict — a re-POST under an existing
    /// <c>rate_sheet_version_id</c> with a different definition, or a second version id claiming a
    /// family's <c>effective_from</c> (ADR-PC-008 forward-only immutability). Logged with the
    /// deploy context so the conflict leaves a server-side record, not just a bare HTTP 409.
    /// </summary>
    public static readonly EventId RateSheetDeployConflict = new(1001, nameof(RateSheetDeployConflict));

    /// <summary>
    /// An unexpected exception escaped the rate-sheet deploy handler (e.g. a failed <c>InsertAsync</c>,
    /// a read-back invariant violation) and is about to become a ProblemDetails 500. Logged at Error
    /// with the deploy context <i>before</i> it surfaces as the 500, so the operator has the version
    /// id / family / actor the generic exception handler cannot see.
    /// </summary>
    public static readonly EventId RateSheetDeployUnexpectedError = new(1002, nameof(RateSheetDeployUnexpectedError));
}
