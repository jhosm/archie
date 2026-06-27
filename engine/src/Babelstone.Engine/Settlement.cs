namespace Babelstone.Engine;

/// <summary>
/// The direction of a money move relative to the account it names: a debit takes value out, a credit
/// puts value in. Carried by <see cref="Movement"/> (ADR-PC-032), the engine's single spine atom for
/// moving money. The old eager settlement seam (ISettlementPort / SettlementInstruction /
/// LoggingSettlementPort) that this enum once served was deleted once every cash leg moved onto the
/// append-first Movement pattern and the confirmation-gated settlement saga;
/// only the generic direction primitive survives, now owned by the Movement spine.
/// </summary>
public enum SettlementDirection
{
    /// <summary>Value leaves the named account (engine → legacy: take funds).</summary>
    Debit,

    /// <summary>Value enters the named account (legacy: receive funds).</summary>
    Credit,
}
