using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Babelstone.Engine.Analyzers;

/// <summary>
/// BENG004 — the STRUCTURAL/semantic half of <c>NO_CLOCK_DRIVEN_ENGINE_SIGNAL</c>
/// (commitment-catalogue row 17; ADR-PC-023 §D1). It flags an engine signal/event
/// emission whose <b>cause</b> is the clock — a clock/scheduler/timer read whose value
/// flows into the construction of a <c>Babelstone.Engine.DomainEvent</c> or a
/// <c>Babelstone.Engine.ScheduledEffect</c>. ADR-PC-023 forbids a signal whose <i>only</i>
/// cause is a date arriving: emits must be caused by a command/decision (a domain fact),
/// never by the passage of time.
/// </summary>
/// <remarks>
/// <para>
/// The CONTRACT half is a lexical name-scan over schema + family event types —
/// it bans the <c>DepositMaturityApproaching</c> / <c>PaymentDue</c> forbidden <i>names</i>.
/// That scan is evadable by an off-list clock-driven type (e.g. <c>DepositMaturityForecast</c>):
/// a name the list does not know, but still constructed from a clock read. This analyser is the
/// structural proof the name-scan cannot give — it does not care what the emitted type is
/// <i>called</i>; it cares that a clock value <i>caused</i> the emit.
/// </para>
/// <para>
/// Mechanism (extended-analyser-safe — no <c>GetSemanticModel</c>, RS1030): for every
/// object-creation of a <c>DomainEvent</c>- or <c>ScheduledEffect</c>-typed value, walk
/// the creation's argument operations and the local data-flow behind them <i>within the same
/// method body</i>. If any argument traces back to a clock/scheduler/timer source read — directly,
/// or through a local assigned from one — the emit is clock-caused and is flagged. Time that
/// enters the emit as a <i>value</i> (an event field, a method parameter, a command property —
/// the fact-driven path ADR-PC-023 §D1 preserves) never traces to a clock read, so a
/// command-driven emit is clean. The clock <see cref="IsClockSource"/> set mirrors the one the
/// handler-purity analyser (BENG001) bans inside <c>Apply</c>, widened here to the whole emit path
/// and to <c>DateTime.Today</c> (a date-only clock read the emit path must also reject):
/// BENG001 governs the fold, BENG004 governs the emit, jointly extending <c>DETERMINISM_GATE</c>'s
/// purity stance per ADR-PC-023's "extends the fold to the emit path".
/// </para>
/// <para>
/// A residual gap, narrow by design: a clock value laundered through a field, an out-of-method
/// helper return, or a delegate is not linked by this within-method data-flow walk — the same
/// shape of residual the BENG001 lambda gap names. The common, reviewable case — a handler or
/// decider that reads the clock and stamps it onto the event it emits — is closed.
/// </para>
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class NoClockDrivenEngineSignalAnalyzer : DiagnosticAnalyzer
{
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
        ImmutableArray.Create(EngineDiagnostics.ClockDrivenSignal);

    public override void Initialize(AnalysisContext context)
    {
        context.EnableConcurrentExecution();
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);

        context.RegisterCompilationStartAction(start =>
        {
            // No engine emit surface in this compilation → nothing this analyser governs.
            var domainEvent = start.Compilation.GetTypeByMetadataName("Babelstone.Engine.DomainEvent");
            var scheduledEffect = start.Compilation.GetTypeByMetadataName("Babelstone.Engine.ScheduledEffect");
            if (domainEvent is null && scheduledEffect is null)
            {
                return;
            }

            start.RegisterOperationAction(
                op => AnalyzeCreation((IObjectCreationOperation)op.Operation, domainEvent, scheduledEffect, op),
                OperationKind.ObjectCreation);
        });
    }

    private static void AnalyzeCreation(
        IObjectCreationOperation creation,
        INamedTypeSymbol? domainEvent,
        INamedTypeSymbol? scheduledEffect,
        OperationAnalysisContext context)
    {
        var created = creation.Type;
        if (created is null || !IsEngineSignal(created, domainEvent, scheduledEffect))
        {
            return;
        }

        // Build the same-method map of local → the operation that assigned it, so a clock read
        // routed through a local (the common `var t = clock.GetUtcNow(); … new Event(t)` shape)
        // is still traced back to the clock.
        var body = RootOperation(creation);
        var localSources = BuildLocalSources(body);

        foreach (var argument in creation.Arguments)
        {
            if (FlowsFromClock(argument.Value, localSources, new HashSet<ILocalSymbol>(SymbolEqualityComparer.Default)))
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    EngineDiagnostics.ClockDrivenSignal,
                    creation.Syntax.GetLocation(),
                    created.Name));
                return;
            }
        }
    }

    // Walk up to the top of the operation tree (the method body / declarator the creation lives
    // in) so the local-source map covers every statement that could have fed the clock value in.
    private static IOperation RootOperation(IOperation operation)
    {
        var current = operation;
        while (current.Parent is { } parent)
        {
            current = parent;
        }

        return current;
    }

    private static bool IsEngineSignal(
        ITypeSymbol created, INamedTypeSymbol? domainEvent, INamedTypeSymbol? scheduledEffect)
    {
        if (scheduledEffect is not null && SymbolEqualityComparer.Default.Equals(created, scheduledEffect))
        {
            return true;
        }

        // Any concrete type deriving from DomainEvent is an emitted engine signal.
        for (var type = created; type is not null; type = type.BaseType)
        {
            if (domainEvent is not null && SymbolEqualityComparer.Default.Equals(type, domainEvent))
            {
                return true;
            }
        }

        return false;
    }

    // Map each local symbol to the value operations that flowed into it (simple/compound
    // assignments and declarators within the same method body the creation lives in).
    private static Dictionary<ILocalSymbol, List<IOperation>> BuildLocalSources(IOperation? root)
    {
        var sources = new Dictionary<ILocalSymbol, List<IOperation>>(SymbolEqualityComparer.Default);
        if (root is null)
        {
            return sources;
        }

        foreach (var op in root.DescendantsAndSelf())
        {
            switch (op)
            {
                case IVariableDeclaratorOperation { Symbol: { } local, Initializer.Value: { } init }:
                    Add(sources, local, init);
                    break;
                case ISimpleAssignmentOperation { Target: ILocalReferenceOperation { Local: { } local }, Value: { } value }:
                    Add(sources, local, value);
                    break;
            }
        }

        return sources;
    }

    private static void Add(Dictionary<ILocalSymbol, List<IOperation>> sources, ILocalSymbol local, IOperation value)
    {
        if (!sources.TryGetValue(local, out var list))
        {
            list = new List<IOperation>();
            sources[local] = list;
        }

        list.Add(value);
    }

    // True if the operation's value originates from a clock/scheduler/timer source — directly,
    // through a wrapping expression (cast, arithmetic, member access on a clock result), or
    // through a local assigned from such a source. `visited` guards local self-reference cycles.
    private static bool FlowsFromClock(
        IOperation? value, Dictionary<ILocalSymbol, List<IOperation>> localSources, HashSet<ILocalSymbol> visited)
    {
        if (value is null)
        {
            return false;
        }

        switch (value)
        {
            case ILocalReferenceOperation { Local: { } local }:
                if (!visited.Add(local) || !localSources.TryGetValue(local, out var assignments))
                {
                    return false;
                }

                return assignments.Any(assigned => FlowsFromClock(assigned, localSources, visited));

            case IInvocationOperation invocation:
                return IsClockSource(invocation.TargetMethod.ContainingType, invocation.TargetMethod.Name)
                    || ChildrenFlowFromClock(value, localSources, visited);

            case IPropertyReferenceOperation propertyRef:
                return IsClockSource(propertyRef.Property.ContainingType, propertyRef.Property.Name)
                    || ChildrenFlowFromClock(value, localSources, visited);

            default:
                // Casts, conversions, arithmetic, interpolations, `new DateOnly(clockYear, …)` —
                // a clock value wrapped in any expression still flows through its operands.
                return ChildrenFlowFromClock(value, localSources, visited);
        }
    }

    private static bool ChildrenFlowFromClock(
        IOperation value, Dictionary<ILocalSymbol, List<IOperation>> localSources, HashSet<ILocalSymbol> visited)
        => value.ChildOperations.Any(child => FlowsFromClock(child, localSources, visited));

    // The clock/scheduler/timer source set — the BENG001 set widened with DateTime.Today, applied
    // to the emit path. A read of any of these is "the passage of time" ADR-PC-023 forbids as the
    // cause of an emission.
    private static bool IsClockSource(ITypeSymbol? owner, string memberName)
    {
        if (owner is null)
        {
            return false;
        }

        return owner.ToDisplayString() switch
        {
            "System.DateTime" or "System.DateTimeOffset" => memberName is "Now" or "UtcNow" or "Today",
            "System.TimeProvider" => memberName is "GetUtcNow" or "GetLocalNow" or "GetTimestamp",
            "System.Diagnostics.Stopwatch" => true,
            "System.Environment" => memberName is "TickCount" or "TickCount64",
            _ => false,
        };
    }
}
