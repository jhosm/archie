using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Babelstone.Money.Analyzers;

/// <summary>
/// BMNY003 — flags any user-defined operator or conversion that returns <c>decimal</c>
/// from a <c>Money</c> input (ADR-PC-010 §P1). Money must never silently degrade to
/// decimal through arithmetic or a cast; the only path out is the explicit, read-only
/// <c>Money.ToDecimal()</c> projection.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class MoneyOperatorReturningDecimalAnalyzer : DiagnosticAnalyzer
{
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
        ImmutableArray.Create(MoneyDiagnostics.MoneyOperatorReturningDecimal);

    public override void Initialize(AnalysisContext context)
    {
        context.EnableConcurrentExecution();
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.RegisterSymbolAction(Analyze, SymbolKind.Method);
    }

    private static void Analyze(SymbolAnalysisContext context)
    {
        var method = (IMethodSymbol)context.Symbol;

        if (method.MethodKind is not (MethodKind.UserDefinedOperator or MethodKind.Conversion))
            return;

        if (method.ReturnType.SpecialType != SpecialType.System_Decimal)
            return;

        if (!method.Parameters.Any(p => IsMoney(p.Type)))
            return;

        var location = method.Locations.FirstOrDefault();
        if (location is null)
            return;

        context.ReportDiagnostic(Diagnostic.Create(
            MoneyDiagnostics.MoneyOperatorReturningDecimal, location, method.Name));
    }

    private static bool IsMoney(ITypeSymbol type) =>
        type is { Name: "Money" } &&
        type.ContainingNamespace?.ToDisplayString() == "Babelstone.FinancialTypes";
}
