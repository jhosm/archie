using Babelstone.Notification;
using Babelstone.Notification.Host;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Babelstone.Notification.Host.Tests;

/// <summary>
/// Tests for <see cref="NotificationModuleLoader"/> — the host's assembly-scan discovery of family
/// <see cref="IFamilyNotificationModule"/> contributions (ADR-IC-019 §D4; ADR-PC-021 §A3). In plain terms: the
/// notification worker host no longer hard-codes which families it notifies for; it finds them by scanning the
/// family assemblies shipped beside it. These tests are the fitness proof of that open/closed property: a
/// family's notifications are discovered by scanning the <c>Babelstone.Families.*.Notification</c> assemblies, so
/// adding one is its module + a host <c>ProjectReference</c> — never an edit to the host's composition. The test
/// names no family TYPE; it asserts the loader returns each family by NAME, exactly as the host boots it.
/// </summary>
public sealed class NotificationModuleLoaderTests
{
    [Fact]
    public void Discovers_family_notification_modules_by_assembly_scan_without_hardcoding()
    {
        var modules = new NotificationModuleLoader().LoadAll(NotificationModuleLoader.FamilyNotificationAssemblies());

        var families = modules.Select(m => m.FamilyName).ToList();

        // The shipped family is discovered with no host-composition edit naming it — the host's Program.cs holds
        // no family type, yet the loader finds the term-deposit notification module purely by assembly-scan.
        Assert.Contains("term_deposit", families);

        // Each family contributes exactly one module — the loader's duplicate-family guard would have thrown.
        Assert.Equal(families.Count, families.Distinct().Count());
    }

    [Fact]
    public void Returns_modules_in_a_stable_order_across_calls()
    {
        var loader = new NotificationModuleLoader();

        var first = loader.LoadAll(NotificationModuleLoader.FamilyNotificationAssemblies())
            .Select(m => m.FamilyName).ToList();
        var second = loader.LoadAll(NotificationModuleLoader.FamilyNotificationAssemblies())
            .Select(m => m.FamilyName).ToList();

        // Stable (assembly-name, then type-name) ordering — independent of reflection's enumeration order — so
        // the host's per-module ConfigureServices loop composes identically across boots.
        Assert.Equal(first, second);
    }

    [Fact]
    public void Fails_loud_when_two_modules_claim_the_same_family()
    {
        // Scan THIS test assembly, which defines two IFamilyNotificationModule types claiming the same family —
        // the load-time collision the loader must reject before composing (two modules would double-register a
        // family's schedule rule + deposit read client). Proves the fail-loud guard the discovery comments assert.
        var ex = Assert.Throws<InvalidOperationException>(
            () => new NotificationModuleLoader().LoadAll([typeof(NotificationModuleLoaderTests).Assembly]));

        Assert.Contains("Duplicate family notification module", ex.Message);
        Assert.Contains(DuplicateFamily, ex.Message);
    }

    private const string DuplicateFamily = "duplicate_family_fixture";

    // Two contributions claiming the SAME family, defined here so a scan of this test assembly trips the
    // loader's duplicate-FamilyName guard. They are not Babelstone.Families.* assemblies, so the real
    // FamilyNotificationAssemblies() probe never discovers them — only the explicit LoadAll above does.
    public sealed class DuplicateFamilyModuleA : IFamilyNotificationModule
    {
        public string FamilyName => DuplicateFamily;

        public void ConfigureServices(IServiceCollection services, NotificationModuleContext ctx)
        {
        }
    }

    public sealed class DuplicateFamilyModuleB : IFamilyNotificationModule
    {
        public string FamilyName => DuplicateFamily;

        public void ConfigureServices(IServiceCollection services, NotificationModuleContext ctx)
        {
        }
    }
}
