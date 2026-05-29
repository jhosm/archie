using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Babelstone.Engine.Analyzers;

/// <summary>
/// BENG001/002/003 — bans clock reads, I/O, and randomness inside event-handler
/// <c>Apply</c> bodies (ADR-PC-010 §P5). Scope is exactly the methods that implement
/// <c>Babelstone.Engine.IEventHandler&lt;,&gt;.Apply</c>; the rest of the engine (the
/// hosting layer, the runtime) reads the clock and does I/O as it must, so it is left
/// untouched.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class HandlerPurityAnalyzer : DiagnosticAnalyzer
{
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
        ImmutableArray.Create(
            EngineDiagnostics.ClockInHandler,
            EngineDiagnostics.IoInHandler,
            EngineDiagnostics.RandomnessInHandler);

    public override void Initialize(AnalysisContext context)
    {
        context.EnableConcurrentExecution();
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);

        context.RegisterCompilationStartAction(start =>
        {
            // No handler interface in this compilation → nothing this analyser governs.
            var handlerInterface = start.Compilation.GetTypeByMetadataName("Babelstone.Engine.IEventHandler`2");
            if (handlerInterface is null)
            {
                return;
            }

            start.RegisterSymbolStartAction(symbolStart =>
            {
                var method = (IMethodSymbol)symbolStart.Symbol;
                if (!ImplementsHandlerApply(method, handlerInterface))
                {
                    return;
                }

                // Only inside a handler Apply body: inspect calls, property reads, and `new`.
                symbolStart.RegisterOperationAction(AnalyzeInvocation, OperationKind.Invocation);
                symbolStart.RegisterOperationAction(AnalyzePropertyReference, OperationKind.PropertyReference);
                symbolStart.RegisterOperationAction(AnalyzeObjectCreation, OperationKind.ObjectCreation);
            }, SymbolKind.Method);
        });
    }

    private static bool ImplementsHandlerApply(IMethodSymbol method, INamedTypeSymbol handlerInterface)
    {
        var containingType = method.ContainingType;
        if (containingType is null)
        {
            return false;
        }

        foreach (var iface in containingType.AllInterfaces)
        {
            if (!SymbolEqualityComparer.Default.Equals(iface.OriginalDefinition, handlerInterface))
            {
                continue;
            }

            foreach (var member in iface.GetMembers("Apply"))
            {
                var implementation = containingType.FindImplementationForInterfaceMember(member);
                if (SymbolEqualityComparer.Default.Equals(implementation, method))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static void AnalyzeInvocation(OperationAnalysisContext context)
    {
        var invocation = (IInvocationOperation)context.Operation;
        Inspect(context, invocation.TargetMethod.ContainingType, invocation.TargetMethod.Name, invocation.Syntax.GetLocation());
    }

    private static void AnalyzePropertyReference(OperationAnalysisContext context)
    {
        var reference = (IPropertyReferenceOperation)context.Operation;
        Inspect(context, reference.Property.ContainingType, reference.Property.Name, reference.Syntax.GetLocation());
    }

    private static void AnalyzeObjectCreation(OperationAnalysisContext context)
    {
        var creation = (IObjectCreationOperation)context.Operation;
        Inspect(context, creation.Constructor?.ContainingType, ".ctor", creation.Syntax.GetLocation());
    }

    private static void Inspect(OperationAnalysisContext context, ITypeSymbol? owner, string memberName, Location location)
    {
        if (owner is null)
        {
            return;
        }

        var typeName = owner.ToDisplayString();
        var member = $"{owner.Name}.{memberName}";

        if (IsClock(typeName, memberName))
        {
            context.ReportDiagnostic(Diagnostic.Create(EngineDiagnostics.ClockInHandler, location, member));
        }
        else if (IsRandomness(typeName, memberName))
        {
            context.ReportDiagnostic(Diagnostic.Create(EngineDiagnostics.RandomnessInHandler, location, member));
        }
        else if (IsIo(owner, typeName, memberName))
        {
            context.ReportDiagnostic(Diagnostic.Create(EngineDiagnostics.IoInHandler, location, member));
        }
    }

    private static bool IsClock(string typeName, string memberName) => typeName switch
    {
        "System.DateTime" or "System.DateTimeOffset" => memberName is "Now" or "UtcNow",
        "System.Diagnostics.Stopwatch" => true,
        "System.Environment" => memberName is "TickCount" or "TickCount64",
        _ => false,
    };

    private static bool IsRandomness(string typeName, string memberName) => typeName switch
    {
        "System.Random" => true,
        "System.Security.Cryptography.RandomNumberGenerator" => true,
        "System.Guid" => memberName == "NewGuid",
        _ => false,
    };

    private static bool IsIo(ITypeSymbol owner, string typeName, string memberName)
    {
        switch (typeName)
        {
            case "System.Net.Http.HttpClient":
            case "System.IO.File":
            case "System.IO.Directory":
                return true;
            case "System.Diagnostics.Process":
                return memberName == "Start";
        }

        // DbConnection and anything deriving from it (NpgsqlConnection, …).
        for (var type = owner as ITypeSymbol; type is not null; type = type.BaseType)
        {
            if (type.ToDisplayString() == "System.Data.Common.DbConnection")
            {
                return true;
            }
        }

        return false;
    }
}
