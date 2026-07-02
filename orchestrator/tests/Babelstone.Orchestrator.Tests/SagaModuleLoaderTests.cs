using Babelstone.Orchestrator.Dispatch;
using Babelstone.Orchestrator.Saga;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Babelstone.Orchestrator.Tests;

/// <summary>
/// Tests for <see cref="SagaModuleLoader"/> — the orchestrator host's assembly-scan discovery of family
/// <see cref="ISagaModule"/> contributions (ADR-IC-018 §D6's realized "assembly-scan later";
/// ADR-PC-040 §D3/§D4). In plain terms: the orchestrator host no longer hard-codes which family sagas it
/// runs; it finds them by scanning the family assemblies shipped beside it and hands each the
/// host-supplied <see cref="SagaModuleContext"/>. These tests are the fitness proof of that open/closed
/// property: a family's sagas are discovered from the <c>Babelstone.Families.*.Orchestration</c>
/// assemblies, so adding one is its module + a host <c>ProjectReference</c> — never an edit to the host's
/// composition. The discovery tests name no family TYPE; they assert by saga-type NAME, exactly as the
/// host boots.
/// </summary>
public sealed class SagaModuleLoaderTests
{
    private static SagaModuleContext Context => new(
        RuntimeConnectionString: "Host=localhost;Database=test",
        EngineBaseUrl: "http://localhost:8080",
        SettlementBaseUrl: "http://localhost:8089");

    [Fact]
    public void Discovers_family_saga_modules_by_assembly_scan_without_hardcoding()
    {
        var modules = new SagaModuleLoader().LoadAll(SagaModuleLoader.FamilySagaAssemblies(), Context);

        var sagaTypes = modules.Select(m => m.SagaType).ToList();

        // The shipped family's BOTH saga modules are discovered with no host-composition edit naming
        // them — the host's Program.cs holds no family type, yet the loader finds the constitution and
        // renewal modules purely by assembly-scan + the (SagaModuleContext) activation contract.
        Assert.Contains("ConstitutionProcess", sagaTypes);
        Assert.Contains("RenewalProcess", sagaTypes);

        // Each saga type is governed by exactly one module — the duplicate-key guard would have thrown.
        Assert.Equal(sagaTypes.Count, sagaTypes.Distinct().Count());
    }

    [Fact]
    public void Discovered_family_modules_declare_the_integration_topics_the_settlement_saga_derives()
    {
        var modules = new SagaModuleLoader().LoadAll(SagaModuleLoader.FamilySagaAssemblies(), Context);

        // The host derives the substrate settlement saga's Movement-bearing subscribe set as the union
        // of the DISCOVERED modules' FamilyIntegrationTopics declarations (ADR-PC-040 §D3) — so at
        // least one discovered family module must declare its integration topic(s), or the settlement
        // saga would boot with an empty subscribe set (its ctor fails loud on that). Asserted
        // family-agnostically: non-empty union, no family token named here.
        var union = modules
            .SelectMany(m => m.FamilyIntegrationTopics)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        Assert.NotEmpty(union);
    }

    [Fact]
    public void Returns_modules_in_a_stable_order_across_calls()
    {
        var loader = new SagaModuleLoader();

        var first = loader.LoadAll(SagaModuleLoader.FamilySagaAssemblies(), Context)
            .Select(m => m.SagaType).ToList();
        var second = loader.LoadAll(SagaModuleLoader.FamilySagaAssemblies(), Context)
            .Select(m => m.SagaType).ToList();

        // Stable (assembly-name, then type-name) ordering — independent of reflection's enumeration
        // order — so the host's per-module ConfigureServices/consume-loop registration composes
        // identically across boots.
        Assert.Equal(first, second);
    }

    [Fact]
    public void Fails_loud_when_two_modules_claim_the_same_saga_type()
    {
        // Scan THIS test assembly, which defines two ISagaModule fixtures claiming the same saga_type —
        // the load-time collision the loader must reject before composing (two modules would
        // double-register a saga's machine/bridge/router in the saga_type registries). The generic
        // failure mechanics (default-ctor diagnostic, custom-activator support) are pinned at the
        // scanner level by FamilyModuleScannerTests (Babelstone.Cadence.Tests); this asserts the saga
        // estate's own vocabulary surfaces.
        var ex = Assert.Throws<InvalidOperationException>(
            () => new SagaModuleLoader().LoadAll([typeof(SagaModuleLoaderTests).Assembly], Context));

        Assert.Contains("Duplicate family saga module", ex.Message);
        Assert.Contains(DuplicateSagaType, ex.Message);
    }

    private const string DuplicateSagaType = "duplicate_saga_type_fixture";

    // Two modules claiming the SAME saga_type, defined here so a scan of this test assembly trips the
    // loader's duplicate-key guard. They are not in a Babelstone.Families.* assembly, so the real
    // FamilySagaAssemblies() probe never discovers them — only the explicit LoadAll above does. NB:
    // every concrete ISagaModule in THIS assembly is activated by that scan, so any future fixture
    // here must carry the (SagaModuleContext) activation contract.
    public sealed class DuplicateSagaModuleA(SagaModuleContext context) : FixtureSagaModuleBase(context)
    {
        public override string SagaType => DuplicateSagaType;
    }

    public sealed class DuplicateSagaModuleB(SagaModuleContext context) : FixtureSagaModuleBase(context)
    {
        public override string SagaType => DuplicateSagaType;
    }

    /// <summary>A minimal ISagaModule fixture base — enough shape for the loader's activation and
    /// duplicate-key passes; never composed into a real host, so its members are inert.</summary>
    public abstract class FixtureSagaModuleBase : ISagaModule
    {
        protected FixtureSagaModuleBase(SagaModuleContext context)
        {
            ArgumentNullException.ThrowIfNull(context);
        }

        public abstract string SagaType { get; }

        public ISagaStateMachine StateMachine => throw new NotSupportedException("fixture only");

        public IResultEventBridge ResultEventBridge => throw new NotSupportedException("fixture only");

        public ISagaCommandRouter CommandRouter => new FixtureRouter(SagaType);

        public IReadOnlyList<string> ConsumeTopics => [];

        public string ConsumerGroupId => "fixture";

        public SagaStartMode StartMode => SagaStartMode.EdgeStarted;

        public AutoStartRule? AutoStartRule => null;

        public void ConfigureServices(IServiceCollection services, SagaModuleContext context)
        {
        }

        private sealed class FixtureRouter : ISagaCommandRouter
        {
            public FixtureRouter(string sagaType)
            {
                SagaType = sagaType;
            }

            public string SagaType { get; }

            public CommandRoute? Resolve(string commandType) => null;

            public CommandRoute? Resolve(string commandType, string sagaType) => null;
        }
    }
}
