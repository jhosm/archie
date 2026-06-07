using Babelstone.Engine;
using Babelstone.Families.TermDeposit;
using Xunit;

namespace Babelstone.InboxConsumer.Tests;

/// <summary>
/// Pure tests for the consumer's record-name → CLR-type seam. Default CI lane — the resolver is what
/// keeps the consumer decode-capable without coupling it to a family's full HandlerRegistry.
/// </summary>
public sealed class InboxEventTypeResolverTests
{
    [Fact]
    public void Resolves_a_registered_event_type_by_record_name()
    {
        var resolver = InboxEventTypeResolver.FromTypes(typeof(DepositMatured), typeof(DepositConstituted));

        Assert.True(resolver.TryResolve(nameof(DepositMatured), out var type));
        Assert.Equal(typeof(DepositMatured), type);
    }

    [Fact]
    public void Returns_false_for_an_unregistered_record_name()
    {
        var resolver = InboxEventTypeResolver.FromTypes(typeof(DepositMatured));
        Assert.False(resolver.TryResolve("SomethingElse", out _));
    }

    [Fact]
    public void Rejects_a_non_domain_event_type()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => InboxEventTypeResolver.FromTypes(typeof(string)));
        Assert.Contains("is not a", ex.Message);
        Assert.Contains(nameof(DomainEvent), ex.Message);
    }

    [Fact]
    public void Rejects_two_types_sharing_a_record_name()
    {
        // Two distinct CLR types with the same simple name would collide on the decode key.
        var ex = Assert.Throws<InvalidOperationException>(
            () => new InboxEventTypeResolver([typeof(Outer.Dup), typeof(Inner.Dup)]));
        Assert.Contains("record name", ex.Message);
    }

    private static class Outer
    {
        public sealed record Dup : DomainEvent;
    }

    private static class Inner
    {
        public sealed record Dup : DomainEvent;
    }
}
