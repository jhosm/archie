namespace Babelstone.Orchestrator.Saga;

/// <summary>
/// The TERMINAL delivery outcome of a saga command, as the result-event bridge sees it (bd
/// babelstone-t7o3.8). The dispatcher classifies an HTTP response into the ADR-PC-029 §slot-5 error
/// model; only the terminal kinds reach the bridge (a 5xx/timeout stays PENDING and never maps to a
/// result event). The mapper is a pure function of <c>(command_type, kind)</c>, so it is keyed on this
/// closed enum, never a raw status code. A generic SUBSTRATE type — the per-saga
/// <see cref="IResultEventBridge"/> interprets it, the substrate dispatcher produces it.
/// </summary>
public enum CommandDeliveryKind
{
    /// <summary>The target accepted the command (a 2xx — applied or an idempotent replay). The leg
    /// SUCCEEDED, so its corresponding success/result event is the one to synthesize.</summary>
    Applied,

    /// <summary>The target REFUSED the command (a 4xx — an illegal lifecycle transition or a
    /// validation reject). A terminal failure: the failure/compensation result event (if any) is the
    /// one to synthesize.</summary>
    Refused,

    /// <summary>The Core ACL returned an EXPLICIT INDETERMINATE settlement signal (HTTP 202 Accepted on a
    /// ConfirmDebit, bd babelstone-t7o3.10): it accepted the debit but cannot yet confirm whether the Core
    /// executed it (the network dropped after the debit was sent — Document 05 Scenario C). A TERMINAL
    /// delivery outcome distinct from <see cref="Applied"/> (2xx success) and <see cref="Refused"/> (4xx):
    /// the leg is neither confirmed nor refused, so the saga parks in a clearance-wait state rather than
    /// advancing or compensating. NOT a timeout — a ConfirmDebit timeout stays a transient, idempotent
    /// retry; this is an explicit ACL signal that the row is terminally resolved-as-unknown.</summary>
    Indeterminate,
}
