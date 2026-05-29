using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Babelstone.Engine.Analyzers.Tests;

/// <summary>
/// In-memory analyser harness (mirrors Babelstone.Money.Analyzers.Tests): parse a C#
/// snippet, compile it against the framework reference set plus the real Babelstone
/// engine assemblies (so snippets implement the real <c>IEventHandler</c>), run one
/// analyser, and return its diagnostics.
/// </summary>
internal static class AnalyzerHarness
{
    private static readonly ImmutableArray<MetadataReference> References = BuildReferences();

    private static ImmutableArray<MetadataReference> BuildReferences()
    {
        var tpa = (string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!;
        var framework = tpa
            .Split(Path.PathSeparator)
            .Where(path => path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path));

        // The engine assemblies the snippets compile against (IEventHandler lives here).
        var engine = MetadataReference.CreateFromFile(typeof(Babelstone.Engine.DomainEvent).Assembly.Location);

        return [.. framework, engine];
    }

    public static async Task<string[]> DiagnosticIdsAsync(string source, DiagnosticAnalyzer analyzer)
    {
        var compilation = CSharpCompilation.Create(
            assemblyName: "AnalyzerTestAssembly",
            syntaxTrees: [CSharpSyntaxTree.ParseText(source)],
            references: References,
            options: new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                nullableContextOptions: NullableContextOptions.Enable));

        var withAnalyzers = compilation.WithAnalyzers([analyzer]);
        var diagnostics = await withAnalyzers.GetAnalyzerDiagnosticsAsync();
        return diagnostics.Select(d => d.Id).OrderBy(id => id, StringComparer.Ordinal).ToArray();
    }
}
