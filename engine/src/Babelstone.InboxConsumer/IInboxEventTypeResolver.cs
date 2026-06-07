using Babelstone.Engine;

namespace Babelstone.InboxConsumer;

/// <summary>
/// Maps a record name (the Avro record name == the CLR <see cref="DomainEvent"/> type name, and the
/// last segment of the CloudEvents reverse-DNS <c>ce_type</c>) to the CLR <see cref="Type"/> the
/// codec decodes the Avro value into. This is the consumer-side analogue of the writer's
/// <c>HandlerRegistration.PayloadType</c> lookup — kept as its own small seam so the consumer is NOT
/// coupled to a family's full <c>HandlerRegistry</c> (a consumer of another context's events declares
/// only the event types it cares about).
/// </summary>
public interface IInboxEventTypeResolver
{
    /// <summary>The CLR domain-event type for the given record name, or false if this consumer does
    /// not know it (the loop treats an unknown record name as a poison message — see the pump).</summary>
    bool TryResolve(string recordName, out Type payloadType);
}

/// <summary>
/// An <see cref="IInboxEventTypeResolver"/> built from a flat list of <see cref="DomainEvent"/> CLR
/// types, keyed by the type name (== the Avro record name == the <c>ce_type</c> last segment). The
/// host passes the event types its handler consumes — e.g.
/// <c>InboxEventTypeResolver.FromTypes(typeof(DepositMatured), typeof(DepositConstituted))</c>, or
/// derived from a family module's <c>HandlerRegistration.PayloadType</c>s.
/// </summary>
public sealed class InboxEventTypeResolver : IInboxEventTypeResolver
{
    private readonly IReadOnlyDictionary<string, Type> _byRecordName;

    public InboxEventTypeResolver(IEnumerable<Type> payloadTypes)
    {
        ArgumentNullException.ThrowIfNull(payloadTypes);
        var byRecordName = new Dictionary<string, Type>(StringComparer.Ordinal);
        foreach (var type in payloadTypes)
        {
            if (!typeof(DomainEvent).IsAssignableFrom(type))
            {
                throw new InvalidOperationException(
                    $"Inbox event type '{type.FullName}' is not a {nameof(DomainEvent)}.");
            }

            if (!byRecordName.TryAdd(type.Name, type))
            {
                throw new InvalidOperationException(
                    $"Two event types share the record name '{type.Name}' (record names must be unique).");
            }
        }

        _byRecordName = byRecordName;
    }

    /// <summary>Convenience builder from a params array of domain-event CLR types.</summary>
    public static InboxEventTypeResolver FromTypes(params Type[] payloadTypes) => new(payloadTypes);

    public bool TryResolve(string recordName, out Type payloadType)
        => _byRecordName.TryGetValue(recordName, out payloadType!);
}
