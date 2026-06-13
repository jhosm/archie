using System.Collections.Immutable;
using System.Reflection;
using System.Text.RegularExpressions;

namespace Babelstone.Families.TermDeposit.Application.Migrations;

/// <summary>
/// The ordered set of forward-only migrations embedded in this assembly (lifted from the
/// engine's <c>Babelstone.EventStore.Migrations.MigrationSet</c>). Discovery is from embedded
/// <c>Migrations/Sql/NNNN_name.sql</c> resources so a deployed binary carries its own schema —
/// there is no loose-file lookup at runtime. This is the FAMILY's read-model schema set (the
/// family-named <c>read_model.deposits</c> table lives here, not in the engine migrations —
/// ADR-PC-021 family-owned ownership), distinct from the engine event-store set.
/// </summary>
public static partial class MigrationSet
{
    // Rebound from the engine's `\.Sql\.…` to `\.Migrations\.Sql\.…`: these resources are embedded
    // under the family Application assembly with a `Migrations.Sql.` resource-name segment (the
    // project's <EmbeddedResource Include="Migrations/Sql/*.sql" /> rooted at the project dir), not
    // the engine's `Sql.`-rooted layout.
    [GeneratedRegex(@"\.Migrations\.Sql\.(?<version>\d+)_(?<name>[^.]+)\.sql$", RegexOptions.CultureInvariant)]
    private static partial Regex ResourceName();

    private static readonly Lazy<ImmutableArray<Migration>> _all = new(Discover);

    /// <summary>All embedded migrations, ascending by <see cref="Migration.Version"/>.</summary>
    public static ImmutableArray<Migration> All => _all.Value;

    private static ImmutableArray<Migration> Discover()
    {
        var assembly = typeof(MigrationSet).Assembly;
        var migrations = new List<Migration>();

        foreach (var resource in assembly.GetManifestResourceNames())
        {
            var match = ResourceName().Match(resource);
            if (!match.Success)
            {
                continue;
            }

            var version = long.Parse(match.Groups["version"].Value, System.Globalization.CultureInfo.InvariantCulture);
            var name = match.Groups["name"].Value;
            var sql = ReadResource(assembly, resource);
            migrations.Add(new Migration(version, name, sql));
        }

        migrations.Sort((a, b) => a.Version.CompareTo(b.Version));

        // Forward-only discipline (§P5) starts here: a duplicate version is a packaging
        // error, caught before any DDL touches a database.
        for (var i = 1; i < migrations.Count; i++)
        {
            if (migrations[i].Version == migrations[i - 1].Version)
            {
                throw new InvalidOperationException(
                    $"Duplicate migration version {migrations[i].Version} " +
                    $"('{migrations[i - 1].Name}' and '{migrations[i].Name}').");
            }
        }

        return [.. migrations];
    }

    private static string ReadResource(Assembly assembly, string resource)
    {
        using var stream = assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException($"Embedded migration resource '{resource}' could not be opened.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
