using Microsoft.CodeAnalysis;

namespace Babelstone.Money.Analyzers;

/// <summary>
/// The diagnostic descriptors for the Money boundary analysers (ADR-PC-010 §P1–§P2).
/// These are the MECHANICAL half of MONEY_BOUNDARY_FIXTURES (commitment-catalogue); the
/// unit half lives in the sealed fixture corpus. All three are warnings, and the engine
/// builds warnings-as-errors, so a violation fails the build.
/// </summary>
internal static class MoneyDiagnostics
{
    public const string Category = "Babelstone.Money";

    public const string MathRoundId = "BMNY001";
    public const string DecimalStateId = "BMNY002";
    public const string MoneyOperatorId = "BMNY003";

    public static readonly DiagnosticDescriptor MathRoundOnDecimal = new(
        id: MathRoundId,
        title: "Math.Round on decimal is allowed only inside Money.FromCents",
        messageFormat: "Round through Money.FromCents instead — it is the single Decimal→Cents rounding boundary (ADR-PC-010 §P2)",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "ADR-PC-010 §P2: compute the whole expression in decimal at full precision and round exactly once, HALF_EVEN, at Money.FromCents. Any other Math.Round(decimal) risks double-rounding drift.");

    public static readonly DiagnosticDescriptor DecimalStateOutsideMoney = new(
        id: DecimalStateId,
        title: "decimal state is allowed only in the Babelstone.FinancialTypes namespace",
        messageFormat: "'{0}' stores decimal outside Babelstone.FinancialTypes — money state is 'long Cents'; decimal is a boundary computation type, never stored state (ADR-PC-010 §P1)",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "ADR-PC-010 §P1: money in domain code, event payloads, projections, snapshot and saga state is Money(long Cents). decimal enters only as a local or parameter at boundary call sites, never as a stored field or property outside Babelstone.FinancialTypes.");

    public static readonly DiagnosticDescriptor MoneyOperatorReturningDecimal = new(
        id: MoneyOperatorId,
        title: "Money exposes no operator returning decimal",
        messageFormat: "Operator '{0}' returns decimal from a Money input — Money intentionally exposes no decimal-returning operator (ADR-PC-010 §P1)",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "ADR-PC-010 §P1: Money never silently degrades to decimal through an operator or conversion. Use the explicit, read-only Money.ToDecimal() projection for display and reporting.");
}
