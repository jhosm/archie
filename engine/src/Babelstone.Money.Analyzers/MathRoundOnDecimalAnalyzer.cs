using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Babelstone.Money.Analyzers;

/// <summary>
/// BMNY001 — bans <c>Math.Round(decimal, …)</c> anywhere except <c>Money.FromCents</c>,
/// the single Decimal→Cents rounding boundary (ADR-PC-010). Rounding decimal more
/// than once accumulates drift; this forces every rounding through that one site. The
/// <c>double</c> overloads of <c>Math.Round</c> are not money math and are not flagged.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class MathRoundOnDecimalAnalyzer : DiagnosticAnalyzer
{
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
        ImmutableArray.Create(MoneyDiagnostics.MathRoundOnDecimal);

    public override void Initialize(AnalysisContext context)
    {
        context.EnableConcurrentExecution();
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.RegisterOperationAction(Analyze, OperationKind.Invocation);
    }

    private static void Analyze(OperationAnalysisContext context)
    {
        var method = ((IInvocationOperation)context.Operation).TargetMethod;

        if (method.Name != "Round" ||
            method.ContainingType?.Name != "Math" ||
            method.ContainingType.ContainingNamespace?.ToDisplayString() != "System" ||
            method.Parameters.Length == 0 ||
            method.Parameters[0].Type.SpecialType != SpecialType.System_Decimal)
            return;

        if (IsInsideMoneyFromCents(context.ContainingSymbol))
            return;

        context.ReportDiagnostic(Diagnostic.Create(
            MoneyDiagnostics.MathRoundOnDecimal, context.Operation.Syntax.GetLocation()));
    }

    private static bool IsInsideMoneyFromCents(ISymbol? symbol)
    {
        for (var s = symbol; s is not null; s = s.ContainingSymbol)
        {
            if (s is IMethodSymbol { Name: "FromCents" } m &&
                m.ContainingType?.Name == "Money" &&
                m.ContainingType.ContainingNamespace?.ToDisplayString() == "Babelstone.FinancialTypes")
                return true;
        }

        return false;
    }
}
