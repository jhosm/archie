using System.Collections.Immutable;

namespace Babelstone.Orchestrator.Saga;

/// <summary>
/// An upstream-issued precondition verdict the constitution saga consumes as an OPAQUE FACT,
/// never re-adjudicated (ADR-PC-024 §2: the engine "treats it as opaque
/// provenance — recorded, never parsed, never re-adjudicated"; the SCA/clearance edge pattern
/// of Document 05 step 0 lifted in-saga). A verdict asserts that some upstream authority
/// evaluated a named precondition; the saga branches ONLY on whether it is
/// <see cref="Satisfied"/> and whether every required key is present — it owns no screening
/// model, no list, no risk score, and crucially performs NO freshness check.
/// </summary>
/// <remarks>
/// <para>
/// <b>No freshness re-adjudication (replay determinism, ADR-PC-010 §P5).</b>
/// <see cref="EvaluatedAt"/> is carried for AUDIT LINEAGE only — it is NEVER compared against
/// "now" inside <see cref="Accepts"/>. A verdict whose <see cref="EvaluatedAt"/> is far in the
/// past but is <see cref="Satisfied"/> with all required keys present is ACCEPTED, exactly as a
/// recorded <c>aml_clearance_ref</c> rebuilt from the event log re-presents without any re-call
/// (ADR-PC-024 §4 "replay re-presents the recorded verdicts"). A freshness window is an
/// EDGE policy, decided once at issuance and pinned onto the verdict by the upstream authority —
/// re-evaluating it here would make the decider depend on wall-clock and poison replay.
/// </para>
/// <para>
/// <b>No PII (ADR-PC-004 §P2 / no-PII-on-the-durable-bus).</b> <see cref="EvidenceRef"/> is an
/// OPAQUE reference (the upstream-issued token), never the underlying identity data — no NIF,
/// IBAN, name, or screening result. The saga records the reference and resolves anything it
/// needs internally behind the engine's OpenBao boundary.
/// </para>
/// </remarks>
/// <param name="Satisfied">Whether the upstream authority cleared the precondition. The ONLY
/// truth value the decider reads — a <c>false</c> verdict refuses, a <c>true</c> verdict with
/// every required key present accepts.</param>
/// <param name="EvidenceRef">The opaque upstream-issued reference (e.g. a clearance token) the
/// saga records for audit lineage. NOT identity data (ADR-PC-024 §1 "references,
/// not identity data"). A satisfied verdict missing this reference is treated as unsatisfied —
/// a clearance asserted with no evidence to point at is not a clearance.</param>
/// <param name="EvaluatedAt">When the upstream authority evaluated the precondition. AUDIT
/// LINEAGE ONLY — never compared against the current time in any decision
/// (<see cref="Accepts"/>). Pinned at issuance, immutable through the saga.</param>
public sealed record PreconditionVerdict(
    bool Satisfied,
    string? EvidenceRef,
    DateTimeOffset EvaluatedAt)
{
    /// <summary>
    /// Whether this verdict ACCEPTS the precondition for <paramref name="requiredKeys"/>. A PURE
    /// predicate (no clock, no I/O, no randomness — ADR-PC-010 §P5): it returns true iff the
    /// verdict is <see cref="Satisfied"/> AND every required key is present and non-blank in
    /// <see cref="Evidence"/>. It NEVER inspects <see cref="EvaluatedAt"/> — there is no
    /// evaluated-at-vs-now freshness branch (replay determinism).
    /// </summary>
    /// <param name="requiredKeys">The evidence keys this precondition demands be present. Empty
    /// means "presence of a satisfied verdict is enough" — there is no per-key requirement.</param>
    public bool Accepts(params string[] requiredKeys)
    {
        if (!Satisfied)
        {
            return false;
        }

        // A satisfied verdict must point at SOMETHING — an opaque evidence reference. A
        // clearance with no token to record is not a clearance (ADR-PC-024 §1).
        if (string.IsNullOrWhiteSpace(EvidenceRef))
        {
            return false;
        }

        foreach (var key in requiredKeys)
        {
            if (!Evidence.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// The per-key opaque evidence references the verdict carries (e.g. a sanctions-screen
    /// reference, a source-of-funds reference). Each VALUE is an opaque upstream token, never
    /// identity data (ADR-PC-004 §P2). The decider checks only PRESENCE + non-blankness of a
    /// required key, never the value's content or the verdict's age. Empty by default — most
    /// verdicts assert a single fact through <see cref="Satisfied"/> + <see cref="EvidenceRef"/>.
    /// </summary>
    public ImmutableDictionary<string, string> Evidence { get; init; } =
        ImmutableDictionary<string, string>.Empty;
}
