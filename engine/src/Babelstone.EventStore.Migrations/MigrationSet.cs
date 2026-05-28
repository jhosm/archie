using System.Collections.Immutable;
using System.Reflection;
using System.Text.RegularExpressions;

namespace Babelstone.EventStore.Migrations;

/// <summary>
/// The ordered set of forward-only migrations embedded in this assembly. Discovery
/// is from embedded <c>Sql/NNNN_name.sql</c> resources so a deployed binary carries
/// its own schema — there is no loose-file lookup at runtime.
/// </summary>
public static partial class MigrationSet
{
    [GeneratedRegex(@"\.Sql\.(?<version>\d+)_(?<name>[^.]+)\.sql$", RegexOptions.CultureInvariant)]
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

        // Forward-only discipline (§P5) starts here: a duplicate or out-of-order
        // version is a packaging error, caught before any DDL touches a database.
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
