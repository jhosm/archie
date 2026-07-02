using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Babelstone.Engine.Analyzers;

/// <summary>
/// BENG001/002/003 — bans clock reads, I/O, and randomness REACHABLE FROM event-handler
/// <c>Apply</c> bodies (ADR-PC-010). Scope is the methods that implement
/// <c>Babelstone.Engine.IEventHandler&lt;,&gt;.Apply</c> AND every method they transitively
/// call within the same assembly — so a clock/I/O/randomness read routed through a private
/// helper is caught, not just one written inline in <c>Apply</c>. The rest of the engine
/// (the hosting layer, the runtime) reads the clock and does I/O as it must, so it is left
/// untouched.
/// </summary>
/// <remarks>
/// The call graph is built from operation actions only — the extended analyser rules
/// (RS1030) forbid <c>Compilation.GetSemanticModel</c> inside an analyser. For every method
/// body in the compilation we record the impure calls it makes directly and the
/// same-assembly methods it calls; at compilation end we walk that graph from each
/// <c>Apply</c> method and attribute any reachable impurity back to the handler. Calls into
/// other assemblies (the BCL) are classified at the call site, never walked into. A residual
/// gap: impurity inside a lambda invoked via a delegate is not linked by a call edge — a far
/// narrower evasion than the inline/helper cases this closes.
/// </remarks>
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

            var facts = new ConcurrentDictionary<IMethodSymbol, MethodFacts>(SymbolEqualityComparer.Default);
            var applyMethods = new ConcurrentBag<IMethodSymbol>();

            start.RegisterSymbolAction(symbolContext =>
            {
                var method = (IMethodSymbol)symbolContext.Symbol;
                if (ImplementsHandlerApply(method, handlerInterface))
                {
                    applyMethods.Add(method.OriginalDefinition);
                }
            }, SymbolKind.Method);

            start.RegisterOperationAction(
                opContext => Record(opContext, start.Compilation, facts),
                OperationKind.Invocation, OperationKind.PropertyReference, OperationKind.ObjectCreation);

            start.RegisterCompilationEndAction(endContext =>
            {
                var reported = new HashSet<(string, int, int)>();
                foreach (var apply in applyMethods)
                {
                    var visited = new HashSet<IMethodSymbol>(SymbolEqualityComparer.Default);
                    Walk(apply, facts, visited, endContext, reported);
                }
            });
        });
    }

    private static void Record(
        OperationAnalysisContext context,
        Compilation compilation,
        ConcurrentDictionary<IMethodSymbol, MethodFacts> facts)
    {
        if (context.ContainingSymbol is not IMethodSymbol containing)
        {
            return;
        }

        var entry = facts.GetOrAdd(containing.OriginalDefinition, _ => new MethodFacts());

        switch (context.Operation)
        {
            case IInvocationOperation invocation:
                Classify(entry, invocation.TargetMethod.ContainingType, invocation.TargetMethod.Name, invocation.Syntax.GetLocation());
                RecordEdge(entry, invocation.TargetMethod, compilation);
                break;
            case IPropertyReferenceOperation propertyRef:
                Classify(entry, propertyRef.Property.ContainingType, propertyRef.Property.Name, propertyRef.Syntax.GetLocation());
                RecordEdge(entry, propertyRef.Property.GetMethod, compilation);
                break;
            case IObjectCreationOperation creation:
                Classify(entry, creation.Constructor?.ContainingType, ".ctor", creation.Syntax.GetLocation());
                break;
        }
    }

    // Follow calls only into methods we can see the body of in THIS assembly — BCL calls
    // are classified at the call site, never walked into.
    private static void RecordEdge(MethodFacts entry, IMethodSymbol? target, Compilation compilation)
    {
        if (target is not null
            && SymbolEqualityComparer.Default.Equals(target.ContainingAssembly, compilation.Assembly)
            && !target.DeclaringSyntaxReferences.IsEmpty)
        {
            entry.Callees.Add(target.OriginalDefinition);
        }
    }

    private static void Classify(MethodFacts entry, ITypeSymbol? owner, string memberName, Location location)
    {
        if (owner is null)
        {
            return;
        }

        var typeName = owner.ToDisplayString();
        var member = $"{owner.Name}.{memberName}";

        if (IsClock(typeName, memberName))
        {
            entry.Violations.Add(new Violation(EngineDiagnostics.ClockInHandler, location, member));
        }
        else if (IsRandomness(typeName, memberName))
        {
            entry.Violations.Add(new Violation(EngineDiagnostics.RandomnessInHandler, location, member));
        }
        else if (IsIo(owner, typeName, memberName))
        {
            entry.Violations.Add(new Violation(EngineDiagnostics.IoInHandler, location, member));
        }
    }

    private static void Walk(
        IMethodSymbol method,
        ConcurrentDictionary<IMethodSymbol, MethodFacts> facts,
        HashSet<IMethodSymbol> visited,
        CompilationAnalysisContext context,
        HashSet<(string, int, int)> reported)
    {
        if (!visited.Add(method) || !facts.TryGetValue(method, out var entry))
        {
            return;
        }

        foreach (var violation in entry.Violations)
        {
            var span = violation.Location.SourceSpan;
            // Dedup so a helper reached from two handlers reports once per site, not twice.
            if (reported.Add((violation.Location.SourceTree?.FilePath ?? string.Empty, span.Start, span.End)))
            {
                context.ReportDiagnostic(Diagnostic.Create(violation.Descriptor, violation.Location, violation.Member));
            }
        }

        foreach (var callee in entry.Callees)
        {
            Walk(callee, facts, visited, context, reported);
        }
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

    private static bool IsClock(string typeName, string memberName) => typeName switch
    {
        "System.DateTime" or "System.DateTimeOffset" => memberName is "Now" or "UtcNow",
        "System.TimeProvider" => memberName is "GetUtcNow" or "GetLocalNow" or "GetTimestamp",
        "System.Diagnostics.Stopwatch" => true,
        "System.Environment" => memberName is "TickCount" or "TickCount64",
        _ => false,
    };

    private static bool IsRandomness(string typeName, string memberName) => typeName switch
    {
        "System.Random" => true,
        "System.Security.Cryptography.RandomNumberGenerator" => true,
        "System.Guid" => memberName is "NewGuid" or "CreateVersion7",
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

    private sealed class MethodFacts
    {
        public ConcurrentBag<Violation> Violations { get; } = new ConcurrentBag<Violation>();

        public ConcurrentBag<IMethodSymbol> Callees { get; } = new ConcurrentBag<IMethodSymbol>();
    }

    private sealed class Violation
    {
        public Violation(DiagnosticDescriptor descriptor, Location location, string member)
        {
            Descriptor = descriptor;
            Location = location;
            Member = member;
        }

        public DiagnosticDescriptor Descriptor { get; }

        public Location Location { get; }

        public string Member { get; }
    }
}
