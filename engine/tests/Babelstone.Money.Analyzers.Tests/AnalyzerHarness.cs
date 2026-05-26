using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Babelstone.Money.Analyzers.Tests;

/// <summary>
/// A self-contained in-memory analyser harness: parse a C# snippet, build a compilation
/// against the running framework's reference set, run a single analyser over it, and
/// return its diagnostics. Depends only on Microsoft.CodeAnalysis.CSharp — no external
/// test-framework meta-package whose version must be kept in lockstep with the SDK's Roslyn.
/// </summary>
internal static class AnalyzerHarness
{
    private static readonly ImmutableArray<MetadataReference> References = BuildReferences();

    private static ImmutableArray<MetadataReference> BuildReferences()
    {
        // The trusted-platform-assemblies list is the framework's full reference set —
        // enough for decimal, System.Math, MidpointRounding and friends to resolve.
        var tpa = (string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!;
        return tpa
            .Split(Path.PathSeparator)
            .Where(path => path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path))
            .ToImmutableArray();
    }

    public static async Task<ImmutableArray<Diagnostic>> AnalyzeAsync(string source, DiagnosticAnalyzer analyzer)
    {
        var compilation = CSharpCompilation.Create(
            assemblyName: "AnalyzerTestAssembly",
            syntaxTrees: new[] { CSharpSyntaxTree.ParseText(source) },
            references: References,
            options: new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                nullableContextOptions: NullableContextOptions.Enable));

        var withAnalyzers = compilation.WithAnalyzers(ImmutableArray.Create(analyzer));
        return await withAnalyzers.GetAnalyzerDiagnosticsAsync();
    }

    /// <summary>The diagnostic IDs the analyser raised, sorted for stable assertions.</summary>
    public static async Task<string[]> DiagnosticIdsAsync(string source, DiagnosticAnalyzer analyzer)
    {
        var diagnostics = await AnalyzeAsync(source, analyzer);
        return diagnostics.Select(d => d.Id).OrderBy(id => id, StringComparer.Ordinal).ToArray();
    }
}
