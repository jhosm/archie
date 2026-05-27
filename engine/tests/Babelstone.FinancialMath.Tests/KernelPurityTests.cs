using System.IO;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using Babelstone.FinancialMath;
using Babelstone.FinancialTypes;
using Xunit;

namespace Babelstone.FinancialMath.Tests;

/// <summary>
/// Purity gate for the financial-math kernel (ADR-PC-010 §P5): a pure primitive reads no clock,
/// does no I/O, and uses no randomness — every time input is an explicit argument. Rather than
/// trust convention, this scans the compiled kernel assemblies' reference tables for any link to
/// non-deterministic BCL surface, so an accidental <c>DateTime.Now</c> or <c>File.Read</c> fails
/// the build. It is the kernel-scoped precursor to the handler-level DETERMINISM_GATE the
/// commitment catalogue reserves for Epic A (handlers don't exist yet); the discipline is the
/// same, the surface is the kernel.
/// </summary>
public class KernelPurityTests
{
    // Namespaces a pure kernel must never reference at all — I/O and networking.
    private static readonly string[] ForbiddenNamespacePrefixes = { "System.IO", "System.Net" };

    // Impure types: any reference is a violation.
    private static readonly (string Ns, string Name)[] ForbiddenTypes =
    {
        ("System", "Random"),                  // nondeterministic
        ("System", "Console"),                 // I/O
        ("System.Diagnostics", "Stopwatch"),   // wall-clock timing
        ("System.Threading", "Thread"),        // Sleep / nondeterministic scheduling
    };

    // Impure members on otherwise-pure types — the clock and nondeterministic factories. Note the
    // kernel legitimately uses System.DateOnly (an explicit date argument is not a clock read);
    // only the ambient-now accessors are forbidden, hence member-level rather than type-level.
    private static readonly (string Ns, string Type, string Member)[] ForbiddenMembers =
    {
        ("System", "DateTime", "get_Now"),
        ("System", "DateTime", "get_UtcNow"),
        ("System", "DateTime", "get_Today"),
        ("System", "DateTimeOffset", "get_Now"),
        ("System", "DateTimeOffset", "get_UtcNow"),
        ("System", "DateOnly", "FromDateTime"),    // routes through the clock if fed DateTime.Now
        ("System", "Guid", "NewGuid"),
        ("System", "Environment", "get_TickCount"),
        ("System", "Environment", "get_TickCount64"),
    };

    public static IEnumerable<object[]> KernelAssemblies()
    {
        yield return new object[] { typeof(Money).Assembly.Location };     // Babelstone.FinancialTypes
        yield return new object[] { typeof(Accrual).Assembly.Location };   // Babelstone.FinancialMath
    }

    [Theory]
    [MemberData(nameof(KernelAssemblies))]
    public void Kernel_assembly_references_no_nondeterministic_surface(string assemblyPath)
    {
        var violations = ScanForbiddenReferences(assemblyPath);
        Assert.True(
            violations.Count == 0,
            $"Impure references in {Path.GetFileName(assemblyPath)} (ADR-PC-010 §P5 — kernel must be deterministic):\n"
                + string.Join("\n", violations));
    }

    [Fact]
    public void Scanner_catches_impurity_negative_control()
    {
        // A purity gate that silently finds nothing is worthless. This test assembly itself
        // references System.IO (File.OpenRead / Path below), so scanning it MUST report a
        // violation — proving the scanner detects real impurity rather than passing vacuously.
        var violations = ScanForbiddenReferences(typeof(KernelPurityTests).Assembly.Location);
        Assert.Contains(violations, v => v.Contains("System.IO", StringComparison.Ordinal));
    }

    private static List<string> ScanForbiddenReferences(string assemblyPath)
    {
        var violations = new List<string>();
        using var stream = File.OpenRead(assemblyPath);
        using var pe = new PEReader(stream);
        MetadataReader mr = pe.GetMetadataReader();

        // TypeReference table: forbidden namespaces and forbidden whole types.
        foreach (TypeReferenceHandle handle in mr.TypeReferences)
        {
            TypeReference tr = mr.GetTypeReference(handle);
            string ns = mr.GetString(tr.Namespace);
            string name = mr.GetString(tr.Name);

            if (Array.Exists(ForbiddenNamespacePrefixes, p => ns == p || ns.StartsWith(p + ".", StringComparison.Ordinal)))
                violations.Add($"references namespace {ns} (type {name})");
            else if (Array.Exists(ForbiddenTypes, t => t.Ns == ns && t.Name == name))
                violations.Add($"references impure type {ns}.{name}");
        }

        // MemberReference table: forbidden members on otherwise-allowed types (the clock).
        foreach (MemberReferenceHandle handle in mr.MemberReferences)
        {
            MemberReference memberRef = mr.GetMemberReference(handle);
            if (memberRef.Parent.Kind != HandleKind.TypeReference)
                continue;

            TypeReference tr = mr.GetTypeReference((TypeReferenceHandle)memberRef.Parent);
            string ns = mr.GetString(tr.Namespace);
            string type = mr.GetString(tr.Name);
            string member = mr.GetString(memberRef.Name);

            if (Array.Exists(ForbiddenMembers, m => m.Ns == ns && m.Type == type && m.Member == member))
                violations.Add($"calls impure member {ns}.{type}.{member}");
        }

        return violations;
    }
}
