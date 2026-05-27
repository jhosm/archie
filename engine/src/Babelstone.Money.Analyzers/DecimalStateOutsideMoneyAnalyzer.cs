using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Babelstone.Money.Analyzers;

/// <summary>
/// BMNY002 — bans <c>decimal</c> (and <c>decimal?</c>) fields and properties outside the
/// <c>Babelstone.FinancialTypes</c> namespace subtree (where the <c>Money</c> type lives;
/// ADR-PC-010 §P1, amended). Money state is
/// <c>Money(long Cents)</c>; decimal is a boundary computation type that may appear only
/// as a local or parameter, never as stored state. Locals and parameters are not symbols
/// of kind Field/Property, so this rule leaves boundary arithmetic untouched.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class DecimalStateOutsideMoneyAnalyzer : DiagnosticAnalyzer
{
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
        ImmutableArray.Create(MoneyDiagnostics.DecimalStateOutsideMoney);

    public override void Initialize(AnalysisContext context)
    {
        context.EnableConcurrentExecution();
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.RegisterSymbolAction(AnalyzeField, SymbolKind.Field);
        context.RegisterSymbolAction(AnalyzeProperty, SymbolKind.Property);
    }

    private static void AnalyzeField(SymbolAnalysisContext context)
    {
        var field = (IFieldSymbol)context.Symbol;
        // Auto-property backing fields are implicit; their property is reported instead.
        if (field.IsImplicitlyDeclared)
            return;

        Report(context, field, field.Type, field.Name);
    }

    private static void AnalyzeProperty(SymbolAnalysisContext context)
    {
        var property = (IPropertySymbol)context.Symbol;
        Report(context, property, property.Type, property.Name);
    }

    private static void Report(SymbolAnalysisContext context, ISymbol symbol, ITypeSymbol type, string name)
    {
        if (!IsDecimal(type) || IsInMoneyNamespace(symbol.ContainingType))
            return;

        var location = symbol.Locations.FirstOrDefault();
        if (location is null)
            return;

        context.ReportDiagnostic(Diagnostic.Create(
            MoneyDiagnostics.DecimalStateOutsideMoney, location, name));
    }

    private static bool IsDecimal(ITypeSymbol type)
    {
        if (type.SpecialType == SpecialType.System_Decimal)
            return true;

        // Nullable<decimal>
        return type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullable &&
               nullable.TypeArguments.Length == 1 &&
               nullable.TypeArguments[0].SpecialType == SpecialType.System_Decimal;
    }

    private static bool IsInMoneyNamespace(INamedTypeSymbol? containingType)
    {
        var ns = containingType?.ContainingNamespace?.ToDisplayString();
        return ns == "Babelstone.FinancialTypes" ||
               (ns is not null && ns.StartsWith("Babelstone.FinancialTypes.", System.StringComparison.Ordinal));
    }
}
