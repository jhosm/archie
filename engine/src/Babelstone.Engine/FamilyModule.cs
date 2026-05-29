using System.Reflection;

namespace Babelstone.Engine;

/// <summary>Binds an event type to the handler that folds it, the payload CLR type for decode, and its schema version.</summary>
public sealed record HandlerRegistration(
    string EventType, Type PayloadType, IDispatchableHandler Handler, int EventSchemaVersion = 1);

/// <summary>
/// A family's contribution to the engine: its name, schema version, and the
/// event-type→handler bindings. Each family ships as its own assembly exporting one
/// of these (skeleton §5.2). Family modules reference only <c>Babelstone.Engine</c> —
/// never EventStore or Pii — so a handler structurally cannot reach the database.
/// </summary>
public interface IFamilyModule
{
    string FamilyName { get; }                              // "term_deposit"
    string SchemaVersion { get; }                           // "term_deposit@2026.1"
    IReadOnlyList<HandlerRegistration> Handlers { get; }
}

/// <summary>
/// Resolves handlers by event type, built once from the loaded family modules.
/// </summary>
public sealed class HandlerRegistry : IHandlerRegistry
{
    private readonly IReadOnlyDictionary<string, HandlerRegistration> _byEventType;
    private readonly IReadOnlyDictionary<Type, HandlerRegistration> _byPayloadType;

    public HandlerRegistry(IEnumerable<HandlerRegistration> registrations)
    {
        var byEventType = new Dictionary<string, HandlerRegistration>(StringComparer.Ordinal);
        var byPayloadType = new Dictionary<Type, HandlerRegistration>();
        foreach (var registration in registrations)
        {
            if (!byEventType.TryAdd(registration.EventType, registration))
            {
                throw new InvalidOperationException(
                    $"Duplicate handler registration for event type '{registration.EventType}'.");
            }

            if (!byPayloadType.TryAdd(registration.PayloadType, registration))
            {
                throw new InvalidOperationException(
                    $"Payload type '{registration.PayloadType}' is registered for more than one event type.");
            }
        }

        _byEventType = byEventType;
        _byPayloadType = byPayloadType;
    }

    public bool TryResolve(string eventType, out IDispatchableHandler handler)
    {
        if (_byEventType.TryGetValue(eventType, out var registration))
        {
            handler = registration.Handler;
            return true;
        }

        handler = null!;
        return false;
    }

    /// <summary>Resolves the full registration (handler, payload type, schema version) for a stored event type.</summary>
    public bool TryResolveByEventType(string eventType, out HandlerRegistration registration)
        => _byEventType.TryGetValue(eventType, out registration!);

    /// <summary>Reverse lookup: the registration for a domain-event CLR type — what the writer needs to build an envelope.</summary>
    public bool TryResolveByPayloadType(Type payloadType, out HandlerRegistration registration)
        => _byPayloadType.TryGetValue(payloadType, out registration!);
}

/// <summary>
/// Discovers <see cref="IFamilyModule"/> implementations across assemblies and builds
/// the <see cref="HandlerRegistry"/>.
/// </summary>
/// <remarks>
/// The CUE cross-check (skeleton §5.2 / ADR-PC-006) — proving the module's declared
/// event types are exactly the family's CUE-declared taxonomy — is deferred until the
/// CUE schema language (Epic C) and a family module (Epic E) exist. Tracked as a
/// follow-up; until then the loader registers the module's declared bindings as-is.
/// </remarks>
public sealed class FamilyModuleLoader
{
    public IReadOnlyList<IFamilyModule> LoadAll(IReadOnlyList<Assembly> sources)
    {
        var modules = new List<IFamilyModule>();
        foreach (var assembly in sources)
        {
            foreach (var type in LoadableTypes(assembly))
            {
                if (type is not { IsAbstract: false, IsInterface: false } || !typeof(IFamilyModule).IsAssignableFrom(type))
                {
                    continue;
                }

                // A module with a constructor dependency would otherwise fail deep inside
                // Activator with a bare MissingMethodException naming no module. Surface a
                // diagnosable error at the discovery seam instead.
                if (type.GetConstructor(Type.EmptyTypes) is null)
                {
                    throw new InvalidOperationException(
                        $"Family module '{type.FullName}' must have a public parameterless constructor.");
                }

                modules.Add((IFamilyModule)Activator.CreateInstance(type)!);
            }
        }

        return modules;
    }

    // One unloadable type in a scanned assembly must not abort discovery of all modules.
    private static IEnumerable<Type> LoadableTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.Where(t => t is not null)!;
        }
    }

    public HandlerRegistry BuildRegistry(IReadOnlyList<IFamilyModule> modules)
        => new(modules.SelectMany(m => m.Handlers));
}
